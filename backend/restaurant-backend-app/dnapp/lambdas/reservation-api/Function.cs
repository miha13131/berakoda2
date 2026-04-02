using System;
using System.Collections.Generic;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ReservationApi;

public class Function
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tablesTable;
    private readonly string _reservationsTable;
    private readonly string _bookingLocksTable;
    private readonly string _waitersTable;
    private readonly string _locationsTable;

    private const int ReservationDurationMinutes = 90;
    private const int CleaningGapMinutes = 15;
    private const int LockBucketMinutes = 15;
    private const string AnonymousCustomerId = "guest-anonymous";
    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public Function()
    {
        _dynamoDb = new AmazonDynamoDBClient();
        _tablesTable = Environment.GetEnvironmentVariable("TABLES_TABLE") ?? "Tables";
        _reservationsTable = Environment.GetEnvironmentVariable("RESERVATIONS_TABLE") ?? "Reservations";
        _bookingLocksTable = Environment.GetEnvironmentVariable("BOOKING_LOCKS_TABLE") ?? "BookingLocks";
        _waitersTable = Environment.GetEnvironmentVariable("WAITERS_TABLE") ?? "waiters-list";
        _locationsTable = Environment.GetEnvironmentVariable("LOCATIONS_TABLE") ?? "Locations";
    }

    public Function(IAmazonDynamoDB dynamoDb, string tablesTable = "Tables", string reservationsTable = "Reservations", string bookingLocksTable = "BookingLocks", string waitersTable = "waiters-list", string locationsTable = "Locations")
    {
        _dynamoDb = dynamoDb;
        _tablesTable = tablesTable;
        _reservationsTable = reservationsTable;
        _bookingLocksTable = bookingLocksTable;
        _waitersTable = waitersTable;
        _locationsTable = locationsTable;
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        try
        {
            if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                return ResponseCreator.CreateResponse(405, "Method Not Allowed", "Only POST is allowed.");
            }

            var actorId = ResolveCustomerId(request);
            var actorEmail = ResolveActorEmail(request);
            var customerIdFromBody = ResolveCustomerIdFromBody(request.Body);
            var isWaiterActor = await IsWaiterEmailAsync(actorEmail);
            var customerId = isWaiterActor
                ? customerIdFromBody ?? actorId ?? actorEmail ?? AnonymousCustomerId
                : actorId ?? AnonymousCustomerId;

            context.Logger.LogLine($"CustomerId: {customerId}");

            var locationIdResolution = ResolveLocationIdFromPath(request);
            if (locationIdResolution.HasError)
            {
                return ResponseCreator.CreateResponse(400, "Bad Request", locationIdResolution.ErrorMessage!);
            }

            var pathLocationId = locationIdResolution.LocationId;

            if (pathLocationId.HasValue && pathLocationId.Value <= 0)
            {
                return ResponseCreator.CreateResponse(400, "Bad Request", "location id must be a positive integer.");
            }

            if (!pathLocationId.HasValue)
            {
                return ResponseCreator.CreateResponse(400, "Bad Request", "location id is required in path.");
            }

            if (pathLocationId.HasValue && !await LocationExistsAsync(pathLocationId.Value))
            {
                return ResponseCreator.CreateResponse(404, "Not Found", "Location not found.");
            }

            context.Logger.LogLine($"LocationId: {pathLocationId}");

            if (string.IsNullOrWhiteSpace(request.Body))
            {
                return ResponseCreator.CreateResponse(400, "Bad Request",
                    "tableId, guests and reservationStart are required.");
            }

            var payload = JsonSerializer.Deserialize<CreateBookingRequest>(request.Body, StrictJsonOptions);

            if (payload == null || payload.TableId <= 0 || payload.Guests <= 0 || payload.SlotId <= 0)
            {
                return ResponseCreator.CreateResponse(400, "Bad Request",
                    "tableId, guests, slot and reservationStart are required.");
            }

            if (!TryParseReservationStart(payload.ReservationStart, out var reservationStart))
            {
                return ResponseCreator.CreateResponse(400, "Bad Request",
                    "reservationStart must be either yyyy-MM-dd#HH:mm or a valid ISO-8601 date-time.");
            }

            if (reservationStart < DateTime.UtcNow)
            {
                return ResponseCreator.CreateResponse(400, "Bad Request", "Reservation time cannot be in the past.");
            }

            var table = await GetTableAsync(payload.TableId, pathLocationId);
            if (table == null)
            {
                return ResponseCreator.CreateResponse(404, "Not Found", "Table not found.");
            }

            if (pathLocationId.HasValue &&
                table.LocationId != pathLocationId.Value.ToString(CultureInfo.InvariantCulture))
            {
                return ResponseCreator.CreateResponse(400, "Bad Request",
                    "Table does not belong to the location from path. Use the table from the same location.");
            }

            if (table.Capacity < payload.Guests)
            {
                return ResponseCreator.CreateResponse(400, "Bad Request",
                    "Selected table capacity is lower than guests count.");
            }

            if (string.IsNullOrWhiteSpace(table.WaiterId))
            {
                return ResponseCreator.CreateResponse(400, "Bad Request", "Selected table has no assigned waiter.");
            }

            var reservationWaiterId = isWaiterActor && !string.IsNullOrWhiteSpace(actorEmail)
                ? actorEmail
                : table.WaiterId;

            var reservationId = Guid.NewGuid().ToString("N");
            var reservationEnd = reservationStart.AddMinutes(ReservationDurationMinutes);
            var reservationDate = reservationStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var dateTimeStart = $"{reservationDate}#{reservationStart.ToString("HH:mm", CultureInfo.InvariantCulture)}";
            var reservationIdSk = $"{reservationDate}#{payload.SlotId}#{payload.TableId}";
            var lockKeys = BuildLockKeys(payload.TableId, reservationStart);

            var transactItems = new List<TransactWriteItem>();

            foreach (var lockKey in lockKeys)
            {
                transactItems.Add(new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = _bookingLocksTable,
                        Item = new Dictionary<string, AttributeValue>
                        {
                            ["lock_id"] = new AttributeValue { S = lockKey },
                            ["reservation_id"] = new AttributeValue { S = reservationId },
                            ["table_id"] = new AttributeValue
                            { S = payload.TableId.ToString(CultureInfo.InvariantCulture) },
                            ["expires_at"] = new AttributeValue
                            {
                                N = new DateTimeOffset(reservationEnd.AddMinutes(CleaningGapMinutes))
                                    .ToUnixTimeSeconds().ToString()
                            }
                        },
                        ConditionExpression = "attribute_not_exists(lock_id)"
                    }
                });
            }

            transactItems.Add(new TransactWriteItem
            {
                Put = new Put
                {
                    TableName = _reservationsTable,
                    Item = new Dictionary<string, AttributeValue>
                    {
                        ["location_id"] = new AttributeValue { N = table.LocationId },
                        ["reservation_id_sk"] = new AttributeValue { S = reservationIdSk },
                        ["date_time_start"] = new AttributeValue { S = dateTimeStart },
                        ["table_id"] =
                            new AttributeValue { N = payload.TableId.ToString(CultureInfo.InvariantCulture) },
                        ["reservation_id"] = new AttributeValue { S = reservationId },
                        ["reservation_start"] = new AttributeValue
                        { S = reservationStart.ToString("O", CultureInfo.InvariantCulture) },
                        ["reservation_end"] = new AttributeValue
                        { S = reservationEnd.ToString("O", CultureInfo.InvariantCulture) },
                        ["reservation_date"] = new AttributeValue { S = reservationDate },
                        ["waiter_id"] = new AttributeValue { S = reservationWaiterId },
                        ["customer_id"] = new AttributeValue { S = customerId },
                        ["status"] = new AttributeValue { S = "Reserved" },
                        ["guests"] = new AttributeValue { N = payload.Guests.ToString(CultureInfo.InvariantCulture) }
                    },
                    ConditionExpression =
                        "attribute_not_exists(location_id) AND attribute_not_exists(reservation_id_sk)"
                }
            });

            await _dynamoDb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems = transactItems
            });

            var waiterProfile = await GetWaiterProfileAsync(reservationWaiterId);

            var reservation = new ReservationDto
            {
                ReservationId = reservationId,
                ReservationDate = reservationDate,
                ReservationStart = reservationStart.ToString("O", CultureInfo.InvariantCulture),
                ReservationEnd = reservationEnd.ToString("O", CultureInfo.InvariantCulture),
                TableId = payload.TableId,
                Waiter = waiterProfile,
                CustomerId = customerId,
                Guests = payload.Guests,
                Status = "Reserved",
                CreatedBy = isWaiterActor ? "waiter" : "customer"
            };

            var description = isWaiterActor
                ? "The reservation was made by a waiter. He will be serving your table."
                : "Created";

            return ResponseCreator.CreateResponse(201, description, reservation);
        }
        catch (TransactionCanceledException ex)
        {
            context.Logger.LogWarning($"Booking transaction cancelled: {ex.Message}");
            return ResponseCreator.CreateResponse(409, "Conflict",
                "The selected table is already booked for this timeslot.");
        }
        catch (JsonException ex)
        {
            return ResponseCreator.CreateResponse(400, "Invalid request", $"Invalid payload: {ex.Message}");
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error creating reservation: {ex.Message}");
            return ResponseCreator.CreateResponse(500, "Internal Server Error", $"An error occurred: {ex.Message}");
        }
    }

    private static string? ResolveCustomerId(APIGatewayProxyRequest request)
    {
        var claims = request?.RequestContext?.Authorizer?.Claims;
        if (claims != null)
        {
            if (claims.TryGetValue("sub", out var sub) && !string.IsNullOrWhiteSpace(sub)) return sub;
            if (claims.TryGetValue("email", out var email) && !string.IsNullOrWhiteSpace(email)) return email;
        }

        var authHeader = request?.Headers?.FirstOrDefault(h => string.Equals(h.Key, "Authorization", StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(authHeader)) return null;

        var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader["Bearer ".Length..].Trim()
            : authHeader.Trim();

        if (string.IsNullOrWhiteSpace(token)) return null;

        try
        {
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
            var subClaim = jwt.Claims.FirstOrDefault(c => c.Type == "sub")?.Value;
            if (!string.IsNullOrWhiteSpace(subClaim)) return subClaim;

            var emailClaim = jwt.Claims.FirstOrDefault(c => c.Type == "email")?.Value;
            if (!string.IsNullOrWhiteSpace(emailClaim)) return emailClaim;
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? ResolveActorEmail(APIGatewayProxyRequest request)
    {
        var claims = request?.RequestContext?.Authorizer?.Claims;
        if (claims != null &&
            claims.TryGetValue("email", out var email) &&
            !string.IsNullOrWhiteSpace(email))
        {
            return email.Trim().ToLowerInvariant();
        }

        var authHeader = request?.Headers?
            .FirstOrDefault(h => string.Equals(h.Key, "Authorization", StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(authHeader))
        {
            return null;
        }

        var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader["Bearer ".Length..].Trim()
            : authHeader.Trim();

        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
            var emailClaim = jwt.Claims.FirstOrDefault(c => c.Type == "email")?.Value;
            return string.IsNullOrWhiteSpace(emailClaim) ? null : emailClaim.Trim().ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveCustomerIdFromBody(string? requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<CreateBookingRequest>(requestBody, StrictJsonOptions);
            return string.IsNullOrWhiteSpace(payload?.CustomerId) ? null : payload.CustomerId;
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> IsWaiterEmailAsync(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _waitersTable,
            Key = new Dictionary<string, AttributeValue>
            {
                ["email"] = new AttributeValue { S = email.Trim().ToLowerInvariant() }
            },
            ProjectionExpression = "email",
            ConsistentRead = true
        });

        return response.Item != null && response.Item.Count > 0;
    }

    private static LocationIdResolution ResolveLocationIdFromPath(APIGatewayProxyRequest request)
    {
        if (request?.PathParameters == null)
        {
            return LocationIdResolution.Success(null);
        }

        var pathParameters = request.PathParameters;
        if (!pathParameters.TryGetValue("id", out var rawLocationId) &&
            !pathParameters.TryGetValue("locationId", out rawLocationId))
        {
            rawLocationId = request.QueryStringParameters != null &&
                            request.QueryStringParameters.TryGetValue("location_id", out var locationFromQuery)
                ? locationFromQuery
                : null;

            if (string.IsNullOrWhiteSpace(rawLocationId))
            {
                return LocationIdResolution.Success(null);
            }
        }

        if (!int.TryParse(rawLocationId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var locationId))
        {
            return LocationIdResolution.Error("location id in path must be an integer.");
        }

        return LocationIdResolution.Success(locationId);
    }

    private async Task<bool> LocationExistsAsync(int locationId)
    {
        var location = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _locationsTable,
            Key = new Dictionary<string, AttributeValue>
            {
                ["location_id"] = new AttributeValue { N = locationId.ToString(CultureInfo.InvariantCulture) }
            },
            ProjectionExpression = "location_id",
            ConsistentRead = true
        });

        return location.Item != null && location.Item.Count > 0;
    }

    private async Task<TableMetadata?> GetTableAsync(int tableId, int? locId)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tablesTable,
            Key = new Dictionary<string, AttributeValue>
            {
                ["location_id"] = new AttributeValue { N = locId.ToString() },
                ["table_id"] = new AttributeValue { N = tableId.ToString(CultureInfo.InvariantCulture) }
            },
            ConsistentRead = true
        });

        if (response.Item == null || response.Item.Count == 0) return null;

        var item = response.Item;
        var waiterId = item.ContainsKey("waiter_id") ? item["waiter_id"].S : string.Empty;
        var locationId = GetNumericOrString(item, "location_id", "0");
        var capacityValue = GetNumericOrString(item, "capacity", "0");
        var address = item.ContainsKey("address") ? item["address"].S : string.Empty;
        _ = int.TryParse(capacityValue, out var capacity);

        return new TableMetadata
        {
            WaiterId = waiterId,
            LocationId = locationId,
            Capacity = capacity,
            Address = address
        };
    }

    private async Task<WaiterProfileDto?> GetWaiterProfileAsync(string waiterId)
    {
        if (string.IsNullOrWhiteSpace(waiterId))
        {
            return null;
        }

        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _waitersTable,
            Key = new Dictionary<string, AttributeValue>
            {
                ["email"] = new AttributeValue { S = waiterId }
            },
            ConsistentRead = true
        });

        if (response.Item == null || response.Item.Count == 0)
        {
            return null;
        }

        var item = response.Item;

        return new WaiterProfileDto
        {
            Id = waiterId,
            Name = GetNumericOrString(item, "name", waiterId),
            Photo = GetNumericOrString(item, "photo", string.Empty),
            Rating = GetDouble(item, "rating")
        };
    }

    private static string GetNumericOrString(IReadOnlyDictionary<string, AttributeValue> item, string key, string defaultValue)
    {
        if (!item.TryGetValue(key, out var value))
        {
            return defaultValue;
        }

        if (!string.IsNullOrWhiteSpace(value.N)) return value.N;
        if (!string.IsNullOrWhiteSpace(value.S)) return value.S;

        return defaultValue;
    }

    private static double GetDouble(IReadOnlyDictionary<string, AttributeValue> item, string key)
    {
        if (!item.TryGetValue(key, out var value))
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(value.N) &&
            double.TryParse(value.N, NumberStyles.Float, CultureInfo.InvariantCulture, out var numFromN))
        {
            return numFromN;
        }

        if (!string.IsNullOrWhiteSpace(value.S) &&
            double.TryParse(value.S, NumberStyles.Float, CultureInfo.InvariantCulture, out var numFromS))
        {
            return numFromS;
        }

        return 0;
    }

    private static bool TryParseReservationStart(string raw, out DateTime reservationStart)
    {
        if (DateTime.TryParseExact(raw, "yyyy-MM-dd#HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out reservationStart))
        {
            return true;
        }

        return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out reservationStart);
    }

    private static List<string> BuildLockKeys(int tableId, DateTime reservationStartUtc)
    {
        var keys = new List<string>();
        var minOffset = -(ReservationDurationMinutes + CleaningGapMinutes);
        var maxOffset = ReservationDurationMinutes + CleaningGapMinutes;

        for (var offset = minOffset; offset <= maxOffset; offset += LockBucketMinutes)
        {
            var slot = reservationStartUtc.AddMinutes(offset);
            var bucketMinute = (slot.Minute / LockBucketMinutes) * LockBucketMinutes;
            var bucket = new DateTime(slot.Year, slot.Month, slot.Day, slot.Hour, bucketMinute, 0, DateTimeKind.Utc);
            keys.Add($"{tableId}#{bucket:yyyyMMddHHmm}");
        }

        return keys.Distinct(StringComparer.Ordinal).ToList();
    }

    private sealed class TableMetadata
    {
        public string WaiterId { get; init; } = string.Empty;
        public string Address { get; init; } = string.Empty;
        public string LocationId { get; init; } = "0";
        public int Capacity { get; init; }
    }

    private sealed class CreateBookingRequest
    {
        public int TableId { get; init; }
        public int Guests { get; init; }
        public int SlotId { get; init; }
        public string ReservationStart { get; init; } = string.Empty;
        public string? CustomerId { get; init; }
    }

    private sealed class ReservationDto
    {
        public string ReservationId { get; init; } = string.Empty;
        public string ReservationDate { get; init; } = string.Empty;
        public string ReservationStart { get; init; } = string.Empty;
        public string ReservationEnd { get; init; } = string.Empty;
        public int TableId { get; init; }
        public WaiterProfileDto? Waiter { get; init; }
        public string CustomerId { get; init; } = string.Empty;
        public int Guests { get; init; }
        public string Status { get; init; } = string.Empty;
        public string CreatedBy { get; init; } = string.Empty;
    }

    private sealed class WaiterProfileDto
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Photo { get; init; } = string.Empty;
        public double Rating { get; init; }
    }

    private sealed class LocationIdResolution
    {
        public int? LocationId { get; init; }
        public bool HasError { get; init; }
        public string? ErrorMessage { get; init; }

        public static LocationIdResolution Success(int? locationId) => new() { LocationId = locationId };
        public static LocationIdResolution Error(string message) => new() { HasError = true, ErrorMessage = message };
    }
}

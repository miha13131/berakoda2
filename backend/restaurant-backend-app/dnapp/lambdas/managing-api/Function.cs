using System;
using System.Collections.Generic;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ManagingApi;

public class Function
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _reservationsTable;
    private readonly string _tablesTable;
    private readonly string _bookingLocksTable;
    private readonly string _waitersTable;
    private readonly string _reservationIdPatchIndex;

    private const int ReservationDurationMinutes = 90;
    private const int CleaningGapMinutes = 15;
    private const int LockBucketMinutes = 15;

    public Function()
    {
        _dynamoDb = new AmazonDynamoDBClient();
        _reservationsTable = Environment.GetEnvironmentVariable("RESERVATIONS_TABLE") ?? "Reservations";
        _tablesTable = Environment.GetEnvironmentVariable("TABLES_TABLE") ?? "Tables";
        _bookingLocksTable = Environment.GetEnvironmentVariable("BOOKING_LOCKS_TABLE") ?? "BookingLocks";
        _waitersTable = Environment.GetEnvironmentVariable("WAITERS_TABLE") ?? "waiters-list";
        _reservationIdPatchIndex = Environment.GetEnvironmentVariable("RESERVATION_ID_PATCH_INDEX") ?? "reservation_id_patch";
    }

    public Function(IAmazonDynamoDB dynamoDb, string reservationsTable = "Reservations", string tablesTable = "Tables", string bookingLocksTable = "BookingLocks", string waitersTable = "waiters-list", string reservationIdPatchIndex = "reservation_id_patch")
    {
        _dynamoDb = dynamoDb;
        _reservationsTable = reservationsTable;
        _tablesTable = tablesTable;
        _bookingLocksTable = bookingLocksTable;
        _waitersTable = waitersTable;
        _reservationIdPatchIndex = reservationIdPatchIndex;
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        try
        {
            if (string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleGetAsync(request);
            }

            if (string.Equals(request.HttpMethod, "PATCH", StringComparison.OrdinalIgnoreCase))
            {
                return await HandlePatchAsync(request);
            }

            return ResponseCreator.CreateResponse(405, "Method Not Allowed", "Only GET and PATCH are supported.");
        }
        catch (JsonException ex)
        {
            return ResponseCreator.CreateResponse(400, "Bad Request", $"Invalid payload: {ex.Message}");
        }
        catch (TransactionCanceledException ex)
        {
            context.Logger.LogWarning($"Managing reservation transaction canceled: {ex.Message}");
            return ResponseCreator.CreateResponse(409, "Conflict", "Requested reservation update conflicts with existing booking.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error in managing-api: {ex.Message}");
            return ResponseCreator.CreateResponse(500, "Internal Server Error", "An unexpected error occurred.");
        }
    }

    private async Task<APIGatewayProxyResponse> HandleGetAsync(APIGatewayProxyRequest request)
    {
        var query = request.QueryStringParameters ?? new Dictionary<string, string>();

        if (!query.TryGetValue("location_id", out var locationIdRaw) || string.IsNullOrWhiteSpace(locationIdRaw) ||
            !int.TryParse(locationIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var locationId))
        {
            return ResponseCreator.CreateResponse(400, "Bad Request", "location_id query parameter is required.");
        }

        if (!query.TryGetValue("date", out var date) || string.IsNullOrWhiteSpace(date) ||
            !DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return ResponseCreator.CreateResponse(400, "Bad Request", "date query parameter is required in format yyyy-MM-dd.");
        }

        if (!query.TryGetValue("time", out var time) || string.IsNullOrWhiteSpace(time) ||
            !TimeSpan.TryParseExact(time, @"hh\:mm", CultureInfo.InvariantCulture, out _))
        {
            return ResponseCreator.CreateResponse(400, "Bad Request", "time query parameter is required in format HH:mm.");
        }

        int? tableId = null;
        if (query.TryGetValue("table_id", out var tableRaw) && !string.IsNullOrWhiteSpace(tableRaw))
        {
            if (!int.TryParse(tableRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tableValue))
            {
                return ResponseCreator.CreateResponse(400, "Bad Request", "table_id must be a valid integer.");
            }

            tableId = tableValue;
        }

        var queryResponse = await _dynamoDb.QueryAsync(new QueryRequest
        {
            TableName = _reservationsTable,
            KeyConditionExpression = "location_id = :locationId AND begins_with(reservation_id_sk, :datePrefix)",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":locationId"] = new AttributeValue { N = locationId.ToString(CultureInfo.InvariantCulture) },
                [":datePrefix"] = new AttributeValue { S = $"{date}#" }
            }
        });

        var expectedDateTimeStart = $"{date}#{time}";
        var reservations = queryResponse.Items
            .Where(item => GetString(item, "date_time_start") == expectedDateTimeStart)
            .Where(item => !tableId.HasValue || GetInt(item, "table_id") == tableId.Value)
            .Select(MapReservation)
            .ToList();

        return ResponseCreator.CreateResponse(200, "Success", reservations);
    }

    private async Task<APIGatewayProxyResponse> HandlePatchAsync(APIGatewayProxyRequest request)
    {
        var waiterEmail = ResolveWaiterEmail(request);
        if (string.IsNullOrWhiteSpace(waiterEmail))
        {
            return ResponseCreator.CreateResponse(401, "Unauthorized", "Missing waiter identity.");
        }

        var isWaiterActor = await IsWaiterEmailAsync(waiterEmail);
        if (!isWaiterActor)
        {
            return ResponseCreator.CreateResponse(403, "Forbidden", "Only waiter account can manage reservations.");
        }

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return ResponseCreator.CreateResponse(400, "Bad Request", "Request body is required.");
        }

        var payload = JsonSerializer.Deserialize<ManageReservationRequest>(request.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (payload == null || string.IsNullOrWhiteSpace(payload.ReservationId))
        {
            return ResponseCreator.CreateResponse(400, "Bad Request", "reservationId is required.");
        }

        if (!Enum.TryParse<ManageAction>(payload.Action, true, out var action))
        {
            return ResponseCreator.CreateResponse(400, "Bad Request", "action must be one of: cancel, postpone, change_table.");
        }

        var reservation = await GetReservationByIdAsync(payload.ReservationId);
        if (reservation == null)
        {
            return ResponseCreator.CreateResponse(404, "Not Found", "Reservation not found.");
        }

        if (!string.Equals(reservation.WaiterId, waiterEmail, StringComparison.OrdinalIgnoreCase))
        {
            return ResponseCreator.CreateResponse(403, "Forbidden", "Waiter can manage only own reservations for assigned location.");
        }

        return action switch
        {
            ManageAction.cancel => await CancelReservationAsync(reservation),
            ManageAction.postpone => await PostponeOrChangeTableAsync(reservation, payload, postponeOnly: true),
            ManageAction.change_table => await PostponeOrChangeTableAsync(reservation, payload, postponeOnly: false),
            _ => ResponseCreator.CreateResponse(400, "Bad Request", "Unsupported action.")
        };
    }

    private async Task<APIGatewayProxyResponse> CancelReservationAsync(ReservationRecord reservation)
    {
        await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _reservationsTable,
            Key = new Dictionary<string, AttributeValue>
            {
                ["location_id"] = new AttributeValue { N = reservation.LocationId.ToString(CultureInfo.InvariantCulture) },
                ["reservation_id_sk"] = new AttributeValue { S = reservation.ReservationIdSk }
            },
            UpdateExpression = "SET #status = :status",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#status"] = "status"
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":status"] = new AttributeValue { S = "CancelledByWaiter" }
            }
        });

        return ResponseCreator.CreateResponse(200, "Success", "Reservation cancelled by waiter.");
    }

    private async Task<APIGatewayProxyResponse> PostponeOrChangeTableAsync(ReservationRecord reservation, ManageReservationRequest payload, bool postponeOnly)
    {
        if (postponeOnly && string.IsNullOrWhiteSpace(payload.NewReservationStart))
        {
            return ResponseCreator.CreateResponse(400, "Bad Request", "newReservationStart is required for postpone action.");
        }

        var newStart = reservation.ReservationStart;
        if (!string.IsNullOrWhiteSpace(payload.NewReservationStart))
        {
            if (!TryParseReservationStart(payload.NewReservationStart, out newStart))
            {
                return ResponseCreator.CreateResponse(400, "Bad Request", "newReservationStart must be ISO-8601 or yyyy-MM-dd#HH:mm.");
            }
        }

        var newTableId = reservation.TableId;
        if (!postponeOnly && payload.NewTableId.HasValue)
        {
            newTableId = payload.NewTableId.Value;
        }
        else if (!postponeOnly && !payload.NewTableId.HasValue)
        {
            return ResponseCreator.CreateResponse(400, "Bad Request", "newTableId is required for change_table action.");
        }

        var newTable = await GetTableAsync(reservation.LocationId, newTableId);
        if (newTable == null)
        {
            return ResponseCreator.CreateResponse(404, "Not Found", "Requested table is not found in waiter location.");
        }

        if (newTable.Capacity < reservation.Guests)
        {
            return ResponseCreator.CreateResponse(400, "Bad Request", "Requested table capacity is lower than guests count.");
        }

        var newEnd = newStart.AddMinutes(ReservationDurationMinutes);
        var newDate = newStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var slotId = reservation.SlotId;
        var newSk = $"{newDate}#{slotId}#{newTableId}";
        var newDateTimeStart = $"{newDate}#{newStart:HH:mm}";

        var transactItems = new List<TransactWriteItem>();

        foreach (var lockKey in BuildLockKeys(reservation.TableId, reservation.ReservationStart))
        {
            transactItems.Add(new TransactWriteItem
            {
                Delete = new Delete
                {
                    TableName = _bookingLocksTable,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["lock_id"] = new AttributeValue { S = lockKey }
                    }
                }
            });
        }

        foreach (var lockKey in BuildLockKeys(newTableId, newStart))
        {
            transactItems.Add(new TransactWriteItem
            {
                Put = new Put
                {
                    TableName = _bookingLocksTable,
                    Item = new Dictionary<string, AttributeValue>
                    {
                        ["lock_id"] = new AttributeValue { S = lockKey },
                        ["reservation_id"] = new AttributeValue { S = reservation.ReservationId },
                        ["table_id"] = new AttributeValue { N = newTableId.ToString(CultureInfo.InvariantCulture) },
                        ["expires_at"] = new AttributeValue
                        {
                            N = new DateTimeOffset(newEnd.AddMinutes(CleaningGapMinutes)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
                        }
                    },
                    ConditionExpression = "attribute_not_exists(lock_id)"
                }
            });
        }

        transactItems.Add(new TransactWriteItem
        {
            Delete = new Delete
            {
                TableName = _reservationsTable,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["location_id"] = new AttributeValue { N = reservation.LocationId.ToString(CultureInfo.InvariantCulture) },
                    ["reservation_id_sk"] = new AttributeValue { S = reservation.ReservationIdSk }
                }
            }
        });

        transactItems.Add(new TransactWriteItem
        {
            Put = new Put
            {
                TableName = _reservationsTable,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["location_id"] = new AttributeValue { N = reservation.LocationId.ToString(CultureInfo.InvariantCulture) },
                    ["reservation_id_sk"] = new AttributeValue { S = newSk },
                    ["date_time_start"] = new AttributeValue { S = newDateTimeStart },
                    ["table_id"] = new AttributeValue { N = newTableId.ToString(CultureInfo.InvariantCulture) },
                    ["reservation_id"] = new AttributeValue { S = reservation.ReservationId },
                    ["reservation_start"] = new AttributeValue { S = newStart.ToString("O", CultureInfo.InvariantCulture) },
                    ["reservation_end"] = new AttributeValue { S = newEnd.ToString("O", CultureInfo.InvariantCulture) },
                    ["reservation_date"] = new AttributeValue { S = newDate },
                    ["waiter_id"] = new AttributeValue { S = reservation.WaiterId },
                    ["customer_id"] = new AttributeValue { S = reservation.CustomerId },
                    ["status"] = new AttributeValue { S = postponeOnly ? "PostponedByWaiter" : "ChangedTableByWaiter" },
                    ["guests"] = new AttributeValue { N = reservation.Guests.ToString(CultureInfo.InvariantCulture) }
                },
                ConditionExpression = "attribute_not_exists(location_id) AND attribute_not_exists(reservation_id_sk)"
            }
        });

        await _dynamoDb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = transactItems
        });

        return ResponseCreator.CreateResponse(200, "Success", new
        {
            reservationId = reservation.ReservationId,
            tableId = newTableId,
            reservationStart = newStart.ToString("O", CultureInfo.InvariantCulture),
            reservationEnd = newEnd.ToString("O", CultureInfo.InvariantCulture),
            status = postponeOnly ? "PostponedByWaiter" : "ChangedTableByWaiter"
        });
    }

    private async Task<ReservationRecord?> GetReservationByIdAsync(string reservationId)
    {
        try
        {
            var queryResponse = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _reservationsTable,
                IndexName = _reservationIdPatchIndex,
                KeyConditionExpression = "reservation_id = :reservationId",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":reservationId"] = new AttributeValue { S = reservationId }
                },
                Limit = 1
            });

            var queryItem = queryResponse.Items.FirstOrDefault();
            if (queryItem != null)
            {
                return ParseReservation(queryItem);
            }
        }
        catch (AmazonDynamoDBException)
        {
            // fallback below
        }

        var scanResponse = await _dynamoDb.ScanAsync(new ScanRequest
        {
            TableName = _reservationsTable,
            FilterExpression = "reservation_id = :reservationId",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":reservationId"] = new AttributeValue { S = reservationId }
            },
            Limit = 1
        });

        var scanItem = scanResponse.Items.FirstOrDefault();
        return scanItem == null ? null : ParseReservation(scanItem);
    }

    private async Task<TableRecord?> GetTableAsync(int locationId, int tableId)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tablesTable,
            Key = new Dictionary<string, AttributeValue>
            {
                ["location_id"] = new AttributeValue { N = locationId.ToString(CultureInfo.InvariantCulture) },
                ["table_id"] = new AttributeValue { N = tableId.ToString(CultureInfo.InvariantCulture) }
            }
        });

        if (response.Item == null || response.Item.Count == 0)
        {
            return null;
        }

        return new TableRecord
        {
            TableId = tableId,
            LocationId = locationId,
            Capacity = GetInt(response.Item, "capacity"),
            WaiterId = GetString(response.Item, "waiter_id")
        };
    }

    private static ReservationView MapReservation(IReadOnlyDictionary<string, AttributeValue> item)
    {
        return new ReservationView
        {
            ReservationId = GetString(item, "reservation_id"),
            DateTimeStart = GetString(item, "date_time_start"),
            TableId = GetInt(item, "table_id"),
            WaiterId = GetString(item, "waiter_id"),
            CustomerId = GetString(item, "customer_id"),
            Status = GetString(item, "status"),
            Guests = GetInt(item, "guests")
        };
    }

    private static ReservationRecord ParseReservation(IReadOnlyDictionary<string, AttributeValue> item)
    {
        var reservationStart = DateTime.Parse(GetString(item, "reservation_start"), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

        return new ReservationRecord
        {
            ReservationId = GetString(item, "reservation_id"),
            ReservationIdSk = GetString(item, "reservation_id_sk"),
            LocationId = GetInt(item, "location_id"),
            TableId = GetInt(item, "table_id"),
            WaiterId = GetString(item, "waiter_id"),
            CustomerId = GetString(item, "customer_id"),
            Guests = GetInt(item, "guests"),
            ReservationStart = reservationStart,
            SlotId = ResolveSlotId(GetString(item, "reservation_id_sk"))
        };
    }

    private static string? ResolveWaiterEmail(APIGatewayProxyRequest request)
    {
        var claims = request?.RequestContext?.Authorizer?.Claims;
        if (claims != null)
        {
            if (claims.TryGetValue("email", out var email) && !string.IsNullOrWhiteSpace(email))
            {
                return email.Trim().ToLowerInvariant();
            }
        }

        var authHeader = request?.Headers?.FirstOrDefault(h =>
            string.Equals(h.Key, "Authorization", StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(authHeader)) return null;

        var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader["Bearer ".Length..].Trim()
            : authHeader.Trim();

        if (string.IsNullOrWhiteSpace(token)) return null;

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

    private async Task<bool> IsWaiterEmailAsync(string email)
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

    private static bool TryParseReservationStart(string? value, out DateTime reservationStart)
    {
        reservationStart = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (DateTime.TryParseExact(value, "yyyy-MM-dd#HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out reservationStart))
        {
            return true;
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out reservationStart);
    }

    private static List<string> BuildLockKeys(int tableId, DateTime reservationStart)
    {
        var reservationEndWithGap = reservationStart
            .AddMinutes(ReservationDurationMinutes)
            .AddMinutes(CleaningGapMinutes);

        var bucketStart = FloorToBucket(reservationStart, LockBucketMinutes);
        var bucketEnd = FloorToBucket(reservationEndWithGap, LockBucketMinutes);

        var keys = new List<string>();
        for (var current = bucketStart; current <= bucketEnd; current = current.AddMinutes(LockBucketMinutes))
        {
            keys.Add($"{tableId}#{current:yyyy-MM-dd#HH:mm}");
        }

        return keys;
    }

    private static DateTime FloorToBucket(DateTime value, int bucketMinutes)
    {
        var minutes = (value.Minute / bucketMinutes) * bucketMinutes;
        return new DateTime(value.Year, value.Month, value.Day, value.Hour, minutes, 0, DateTimeKind.Utc);
    }

    private static string GetString(IReadOnlyDictionary<string, AttributeValue> item, string key)
    {
        if (!item.TryGetValue(key, out var value)) return string.Empty;
        if (!string.IsNullOrWhiteSpace(value.S)) return value.S;
        if (!string.IsNullOrWhiteSpace(value.N)) return value.N;
        return string.Empty;
    }

    private static int GetInt(IReadOnlyDictionary<string, AttributeValue> item, string key)
    {
        if (!item.TryGetValue(key, out var value)) return 0;
        if (!string.IsNullOrWhiteSpace(value.N) &&
            int.TryParse(value.N, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numberFromN))
        {
            return numberFromN;
        }

        if (!string.IsNullOrWhiteSpace(value.S) &&
            int.TryParse(value.S, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numberFromS))
        {
            return numberFromS;
        }

        return 0;
    }

    private static int ResolveSlotId(string reservationIdSk)
    {
        if (string.IsNullOrWhiteSpace(reservationIdSk)) return 0;

        var parts = reservationIdSk.Split('#');
        if (parts.Length < 3) return 0;

        return int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slotId) ? slotId : 0;
    }

    private enum ManageAction
    {
        cancel,
        postpone,
        change_table
    }

    private sealed class ManageReservationRequest
    {
        public string ReservationId { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
        public string? NewReservationStart { get; init; }
        public int? NewTableId { get; init; }
    }

    private sealed class ReservationView
    {
        public string ReservationId { get; init; } = string.Empty;
        public string DateTimeStart { get; init; } = string.Empty;
        public int TableId { get; init; }
        public string WaiterId { get; init; } = string.Empty;
        public string CustomerId { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public int Guests { get; init; }
    }

    private sealed class ReservationRecord
    {
        public string ReservationId { get; init; } = string.Empty;
        public string ReservationIdSk { get; init; } = string.Empty;
        public int LocationId { get; init; }
        public int TableId { get; init; }
        public string WaiterId { get; init; } = string.Empty;
        public string CustomerId { get; init; } = string.Empty;
        public int Guests { get; init; }
        public DateTime ReservationStart { get; init; }
        public int SlotId { get; init; }
    }

    private sealed class TableRecord
    {
        public int TableId { get; init; }
        public int LocationId { get; init; }
        public int Capacity { get; init; }
        public string WaiterId { get; init; } = string.Empty;
    }
}

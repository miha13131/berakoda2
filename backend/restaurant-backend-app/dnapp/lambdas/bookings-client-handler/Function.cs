using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace BookingsClientHandler;

public class Function
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tablesTable;
    private readonly string _reservationsTable;
    private readonly string _bookingLocksTable;

    private const int ReservationDurationMinutes = 90;
    private const int CleaningGapMinutes = 15;
    private const int LockBucketMinutes = 15;

    public Function()
    {
        _dynamoDb = new AmazonDynamoDBClient();
        _tablesTable = Environment.GetEnvironmentVariable("TABLES_TABLE") ?? "Tables";
        _reservationsTable = Environment.GetEnvironmentVariable("RESERVATIONS_TABLE") ?? "Reservations";
        _bookingLocksTable = Environment.GetEnvironmentVariable("BOOKING_LOCKS_TABLE") ?? "BookingLocks";
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        try
        {
            if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                return ResponseCreator.CreateResponse(405, "Method Not Allowed", "Only POST is allowed.");
            }

            var customerId = ResolveCustomerId(request);
            if (string.IsNullOrWhiteSpace(customerId))
            {
                return ResponseCreator.CreateResponse(401, "Unauthorized", "Please log in to create a reservation.");
            }

            var payload = JsonSerializer.Deserialize<CreateBookingRequest>(request.Body ?? string.Empty,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (payload == null || string.IsNullOrWhiteSpace(payload.TableId) || payload.Guests <= 0)
            {
                return ResponseCreator.CreateResponse(400, "Bad Request", "tableId, guests and reservationStart are required.");
            }

            if (!DateTime.TryParse(payload.ReservationStart, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var reservationStart))
            {
                return ResponseCreator.CreateResponse(400, "Bad Request", "reservationStart must be a valid ISO-8601 UTC date-time.");
            }

            if (reservationStart < DateTime.UtcNow)
            {
                return ResponseCreator.CreateResponse(400, "Bad Request", "Reservation time cannot be in the past.");
            }

            var table = await GetTableAsync(payload.TableId);
            if (table == null)
            {
                return ResponseCreator.CreateResponse(404, "Not Found", "Table not found.");
            }

            if (table.Capacity < payload.Guests)
            {
                return ResponseCreator.CreateResponse(400, "Bad Request", "Selected table capacity is lower than guests count.");
            }

            if (string.IsNullOrWhiteSpace(table.WaiterId))
            {
                return ResponseCreator.CreateResponse(400, "Bad Request", "Selected table has no assigned waiter.");
            }

            var reservationId = Guid.NewGuid().ToString("N");
            var reservationEnd = reservationStart.AddMinutes(ReservationDurationMinutes);
            var reservationDate = reservationStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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
                            ["table_id"] = new AttributeValue { S = payload.TableId },
                            ["expires_at"] = new AttributeValue { N = new DateTimeOffset(reservationEnd.AddMinutes(CleaningGapMinutes)).ToUnixTimeSeconds().ToString() }
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
                        ["reservation_id"] = new AttributeValue { S = reservationId },
                        ["reservation_start"] = new AttributeValue { S = reservationStart.ToString("O", CultureInfo.InvariantCulture) },
                        ["reservation_end"] = new AttributeValue { S = reservationEnd.ToString("O", CultureInfo.InvariantCulture) },
                        ["reservation_date"] = new AttributeValue { S = reservationDate },
                        ["location_id"] = new AttributeValue { N = table.LocationId },
                        ["table_id"] = new AttributeValue { S = payload.TableId },
                        ["waiter_id"] = new AttributeValue { S = table.WaiterId },
                        ["customer_id"] = new AttributeValue { S = customerId },
                        ["status"] = new AttributeValue { S = "Reserved" },
                        ["guests"] = new AttributeValue { N = payload.Guests.ToString(CultureInfo.InvariantCulture) }
                    },
                    ConditionExpression = "attribute_not_exists(reservation_id) AND attribute_not_exists(reservation_start)"
                }
            });

            await _dynamoDb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems = transactItems
            });

            var responseBody = JsonSerializer.Serialize(new
            {
                reservationId,
                status = "Reserved",
                waiterId = table.WaiterId,
                message = "Reservation created successfully."
            });

            return ResponseCreator.CreateResponse(201, "Created", responseBody);
        }
        catch (TransactionCanceledException ex)
        {
            context.Logger.LogWarning($"Booking transaction cancelled: {ex.Message}");
            return ResponseCreator.CreateResponse(409, "Conflict", "The selected table is already booked for this timeslot.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error creating reservation: {ex.Message}");
            return ResponseCreator.CreateResponse(500, "Internal Server Error", "An error occurred.");
        }
    }

    private static string? ResolveCustomerId(APIGatewayProxyRequest request)
    {
        var claims = request?.RequestContext?.Authorizer?.Claims;
        if (claims == null) return null;

        if (claims.TryGetValue("sub", out var sub) && !string.IsNullOrWhiteSpace(sub)) return sub;
        if (claims.TryGetValue("email", out var email) && !string.IsNullOrWhiteSpace(email)) return email;

        return null;
    }

    private async Task<TableMetadata?> GetTableAsync(string tableId)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tablesTable,
            Key = new Dictionary<string, AttributeValue>
            {
                ["table_id"] = new AttributeValue { S = tableId }
            },
            ConsistentRead = true
        });

        if (response.Item == null || response.Item.Count == 0) return null;

        var item = response.Item;
        var waiterId = item.ContainsKey("waiter_id") ? item["waiter_id"].S : string.Empty;
        var locationId = item.ContainsKey("location_id") ? item["location_id"].N : "0";
        var capacityValue = item.ContainsKey("capacity") ? item["capacity"].N : "0";
        _ = int.TryParse(capacityValue, out var capacity);

        return new TableMetadata
        {
            WaiterId = waiterId,
            LocationId = locationId,
            Capacity = capacity
        };
    }

    private static List<string> BuildLockKeys(string tableId, DateTime reservationStartUtc)
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
        public string LocationId { get; init; } = "0";
        public int Capacity { get; init; }
    }

    private sealed class CreateBookingRequest
    {
        public string TableId { get; init; } = string.Empty;
        public int Guests { get; init; }
        public string ReservationStart { get; init; } = string.Empty;
    }
}

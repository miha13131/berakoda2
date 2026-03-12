using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SimpleLambdaFunction;

public class Function
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _reservationsTable;
    private readonly string _bookingLocksTable;

    private const int ReservationDurationMinutes = 90;
    private const int CleaningGapMinutes = 15;
    private const int LockBucketMinutes = 15;
    private const int CancellationDeadlineMinutes = 30;

    public Function()
    {
        _dynamoDb = new AmazonDynamoDBClient();
        _reservationsTable = Environment.GetEnvironmentVariable("RESERVATIONS_TABLE") ?? "Reservations";
        _bookingLocksTable = Environment.GetEnvironmentVariable("BOOKING_LOCKS_TABLE") ?? "BookingLocks";
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        try
        {
            if (!string.Equals(request.HttpMethod, "DELETE", StringComparison.OrdinalIgnoreCase))
            {
                return ResponseCreator.CreateResponse(405, "Method Not Allowed", "Only DELETE is allowed.");
            }

            var customerId = ResolveCustomerId(request);
            if (string.IsNullOrWhiteSpace(customerId))
            {
                return ResponseCreator.CreateResponse(401, "Unauthorized", "Please log in to cancel a reservation.");
            }

            var reservationId = ResolveReservationId(request);
            if (string.IsNullOrWhiteSpace(reservationId))
            {
                return ResponseCreator.CreateResponse(400, "Bad Request", "Reservation id is required in path parameter {id}.");
            }

            var reservation = await GetReservationAsync(reservationId);
            if (reservation == null)
            {
                return ResponseCreator.CreateResponse(404, "Not Found", "Reservation not found.");
            }

            if (!string.Equals(reservation.CustomerId, customerId, StringComparison.Ordinal))
            {
                return ResponseCreator.CreateResponse(403, "Forbidden", "You can only cancel your own reservations.");
            }

            var cancellationDeadline = reservation.ReservationStart.AddMinutes(-CancellationDeadlineMinutes);
            if (DateTime.UtcNow > cancellationDeadline)
            {
                return ResponseCreator.CreateResponse(400, "Bad Request",
                    "Cancellation is not allowed less than 30 minutes before reservation start time.");
            }

            var transactItems = new List<TransactWriteItem>
            {
                new()
                {
                    Delete = new Delete
                    {
                        TableName = _reservationsTable,
                        Key = new Dictionary<string, AttributeValue>
                        {
                            ["reservation_id"] = new AttributeValue { S = reservation.ReservationId }
                        },
                        ConditionExpression = "attribute_exists(reservation_id) AND customer_id = :customerId",
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [":customerId"] = new AttributeValue { S = customerId }
                        }
                    }
                }
            };

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

            await _dynamoDb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems = transactItems
            });

            return ResponseCreator.CreateResponse(200, "Success", "Reservation cancelled successfully.");
        }
        catch (TransactionCanceledException ex)
        {
            context.Logger.LogWarning($"Reservation cancellation transaction cancelled: {ex.Message}");
            return ResponseCreator.CreateResponse(409, "Conflict", "Reservation cannot be cancelled at this time.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error cancelling reservation: {ex.Message}");
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

    private static string? ResolveReservationId(APIGatewayProxyRequest request)
    {
        if (request.PathParameters == null) return null;

        if (request.PathParameters.TryGetValue("id", out var id) && !string.IsNullOrWhiteSpace(id)) return id;
        if (request.PathParameters.TryGetValue("reservationId", out var reservationId) && !string.IsNullOrWhiteSpace(reservationId)) return reservationId;

        return null;
    }

    private async Task<ReservationData?> GetReservationAsync(string reservationId)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _reservationsTable,
            Key = new Dictionary<string, AttributeValue>
            {
                ["reservation_id"] = new AttributeValue { S = reservationId }
            },
            ConsistentRead = true
        });

        if (response.Item == null || response.Item.Count == 0)
        {
            return null;
        }

        var item = response.Item;
        if (!item.TryGetValue("reservation_start", out var reservationStartValue) ||
            !DateTime.TryParse(reservationStartValue.S, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var reservationStart))
        {
            throw new InvalidOperationException("Reservation has invalid reservation_start value.");
        }

        return new ReservationData
        {
            ReservationId = reservationId,
            CustomerId = item.TryGetValue("customer_id", out var customerValue) ? customerValue.S : string.Empty,
            TableId = GetTableId(item),
            ReservationStart = reservationStart
        };
    }

    private static int GetTableId(IReadOnlyDictionary<string, AttributeValue> item)
    {
        if (!item.TryGetValue("table_id", out var tableValue)) return 0;

        if (!string.IsNullOrWhiteSpace(tableValue.N) &&
            int.TryParse(tableValue.N, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId))
        {
            return numericId;
        }

        if (!string.IsNullOrWhiteSpace(tableValue.S) &&
            int.TryParse(tableValue.S, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stringId))
        {
            return stringId;
        }

        return 0;
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

    private sealed class ReservationData
    {
        public string ReservationId { get; init; } = string.Empty;
        public string CustomerId { get; init; } = string.Empty;
        public int TableId { get; init; }
        public DateTime ReservationStart { get; init; }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ReservationHistory;

public class Function
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _reservationsTable;
    private readonly string _customerReservationsIndex;

    public Function()
    {
        _dynamoDb = new AmazonDynamoDBClient();
        _reservationsTable = Environment.GetEnvironmentVariable("RESERVATIONS_TABLE") ?? "Reservations";
        _customerReservationsIndex = Environment.GetEnvironmentVariable("CUSTOMER_RESERVATIONS_INDEX") ?? "CustomerReservationsIndex";
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        try
        {
            if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                return ResponseCreator.CreateResponse(405, "Method Not Allowed", "Only GET is allowed.");
            }

            var customerId = ResolveCustomerId(request);
            if (string.IsNullOrWhiteSpace(customerId))
            {
                return ResponseCreator.CreateResponse(401, "Unauthorized", "Missing or invalid JWT token.");
            }

            var reservations = await GetReservationsByCustomerAsync(customerId);
            return ResponseCreator.CreateResponse(200, "Success", reservations);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error getting reservation history: {ex.Message}");
            return ResponseCreator.CreateResponse(500, "Internal Server Error", "An error occurred.");
        }
    }

    private async Task<List<ReservationHistoryItem>> GetReservationsByCustomerAsync(string customerId)
    {
        try
        {
            var queryRequest = new QueryRequest
            {
                TableName = _reservationsTable,
                IndexName = _customerReservationsIndex,
                KeyConditionExpression = "customer_id = :customerId",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":customerId"] = new AttributeValue { S = customerId }
                },
                ScanIndexForward = false
            };

            var queryResponse = await _dynamoDb.QueryAsync(queryRequest);
            return queryResponse.Items.Select(MapReservation).ToList();
        }
        catch (AmazonDynamoDBException)
        {
            var scanRequest = new ScanRequest
            {
                TableName = _reservationsTable,
                FilterExpression = "customer_id = :customerId",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":customerId"] = new AttributeValue { S = customerId }
                }
            };

            var scanResponse = await _dynamoDb.ScanAsync(scanRequest);
            return scanResponse.Items.Select(MapReservation).OrderByDescending(item => item.ReservationStart).ToList();
        }
    }

    private static ReservationHistoryItem MapReservation(Dictionary<string, AttributeValue> item)
    {
        return new ReservationHistoryItem
        {
            ReservationId = GetString(item, "reservation_id"),
            ReservationDate = GetString(item, "reservation_date"),
            ReservationStart = GetString(item, "reservation_start"),
            ReservationEnd = GetString(item, "reservation_end"),
            TableId = GetIntFromAttribute(item, "table_id"),
            WaiterId = GetString(item, "waiter_id"),
            Status = GetString(item, "status"),
            Guests = GetInt(item, "guests"),
            LocationId = GetString(item, "location_id")
        };
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

    private static string GetString(IReadOnlyDictionary<string, AttributeValue> item, string key)
    {
        if (!item.TryGetValue(key, out var attribute)) return string.Empty;
        if (!string.IsNullOrWhiteSpace(attribute.S)) return attribute.S;
        if (!string.IsNullOrWhiteSpace(attribute.N)) return attribute.N;
        return string.Empty;
    }

    private static int GetIntFromAttribute(IReadOnlyDictionary<string, AttributeValue> item, string key)
    {
        if (!item.TryGetValue(key, out var attribute)) return 0;

        if (!string.IsNullOrWhiteSpace(attribute.N) &&
            int.TryParse(attribute.N, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericValue))
        {
            return numericValue;
        }

        if (!string.IsNullOrWhiteSpace(attribute.S) &&
            int.TryParse(attribute.S, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stringValue))
        {
            return stringValue;
        }

        return 0;
    }

    private static int GetInt(IReadOnlyDictionary<string, AttributeValue> item, string key)
    {
        if (!item.TryGetValue(key, out var attribute) || string.IsNullOrWhiteSpace(attribute.N)) return 0;
        return int.TryParse(attribute.N, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private sealed class ReservationHistoryItem
    {
        public string ReservationId { get; init; } = string.Empty;
        public string ReservationDate { get; init; } = string.Empty;
        public string ReservationStart { get; init; } = string.Empty;
        public string ReservationEnd { get; init; } = string.Empty;
        public int TableId { get; init; }
        public string WaiterId { get; init; } = string.Empty;
        public string LocationId { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public int Guests { get; init; }
    }
}

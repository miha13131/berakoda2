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

namespace CustomersSearch;

public class Function
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _reservationsTable;
    private readonly string _waitersTable;

    public Function()
    {
        _dynamoDb = new AmazonDynamoDBClient();
        _reservationsTable = Environment.GetEnvironmentVariable("RESERVATIONS_TABLE") ?? "Reservations";
        _waitersTable = Environment.GetEnvironmentVariable("WAITERS_TABLE") ?? "waiters-list";
    }

    public Function(IAmazonDynamoDB dynamoDb, string reservationsTable = "Reservations", string waitersTable = "waiters-list")
    {
        _dynamoDb = dynamoDb;
        _reservationsTable = reservationsTable;
        _waitersTable = waitersTable;
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        try
        {
            if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                return ResponseCreator.CreateResponse(405, "Method Not Allowed", "Only GET is allowed.");
            }

            var waiterEmail = ResolveActorEmail(request);
            if (string.IsNullOrWhiteSpace(waiterEmail))
            {
                return ResponseCreator.CreateResponse(401, "Unauthorized", "Missing waiter identity.");
            }

            var isWaiter = await IsWaiterEmailAsync(waiterEmail);
            if (!isWaiter)
            {
                return ResponseCreator.CreateResponse(403, "Forbidden", "Only waiter can search existing customers.");
            }

            var query = request.QueryStringParameters ?? new Dictionary<string, string>();
            query.TryGetValue("query", out var searchQuery);
            query.TryGetValue("id", out var customerId);

            if (string.IsNullOrWhiteSpace(searchQuery) && string.IsNullOrWhiteSpace(customerId))
            {
                return ResponseCreator.CreateResponse(400, "Bad Request", "Provide query or id parameter.");
            }

            var response = await _dynamoDb.ScanAsync(new ScanRequest
            {
                TableName = _reservationsTable,
                ProjectionExpression = "customer_id, customer_name",
            });

            var customers = response.Items
                .Select(MapCustomer)
                .Where(c => !string.IsNullOrWhiteSpace(c.Id))
                .Where(c => !string.Equals(c.Id, "guest-anonymous", StringComparison.OrdinalIgnoreCase))
                .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (!string.IsNullOrWhiteSpace(customerId))
            {
                customers = customers
                    .Where(c => string.Equals(c.Id, customerId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            else
            {
                var normalizedQuery = searchQuery!.Trim();
                customers = customers
                    .Where(c => ContainsIgnoreCase(c.Id, normalizedQuery) || ContainsIgnoreCase(c.Name, normalizedQuery))
                    .OrderBy(c => c.Name)
                    .ThenBy(c => c.Id)
                    .Take(20)
                    .ToList();
            }

            return ResponseCreator.CreateResponse(200, "Success", customers);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error searching customers: {ex.Message}");
            return ResponseCreator.CreateResponse(500, "Internal Server Error", "An error occurred.");
        }
    }

    private async Task<bool> IsWaiterEmailAsync(string email)
    {
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

    private static string? ResolveActorEmail(APIGatewayProxyRequest request)
    {
        var claims = request?.RequestContext?.Authorizer?.Claims;
        if (claims != null && claims.TryGetValue("email", out var email) && !string.IsNullOrWhiteSpace(email))
        {
            return email.Trim().ToLowerInvariant();
        }

        var authHeader = request?.Headers?.FirstOrDefault(h =>
            string.Equals(h.Key, "Authorization", StringComparison.OrdinalIgnoreCase)).Value;
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

    private static bool ContainsIgnoreCase(string source, string needle)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(needle))
        {
            return false;
        }

        return source.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static CustomerDto MapCustomer(IReadOnlyDictionary<string, AttributeValue> item)
    {
        var id = GetString(item, "customer_id");
        var name = GetString(item, "customer_name");

        if (string.IsNullOrWhiteSpace(name))
        {
            name = id;
        }

        return new CustomerDto
        {
            Id = id,
            Name = name
        };
    }

    private static string GetString(IReadOnlyDictionary<string, AttributeValue> item, string key)
    {
        if (!item.TryGetValue(key, out var value)) return string.Empty;
        if (!string.IsNullOrWhiteSpace(value.S)) return value.S;
        if (!string.IsNullOrWhiteSpace(value.N)) return value.N;
        return string.Empty;
    }

    private sealed class CustomerDto
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
    }
}

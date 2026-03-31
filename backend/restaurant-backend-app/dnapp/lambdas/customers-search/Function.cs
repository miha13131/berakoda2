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
    private readonly string _usersTable;
    private readonly string _waitersTable;

    public Function()
    {
        _dynamoDb = new AmazonDynamoDBClient();
        _usersTable = Environment.GetEnvironmentVariable("USERS_TABLE") ?? "Users";
        _waitersTable = Environment.GetEnvironmentVariable("WAITERS_TABLE") ?? "waiters-list";
    }

    public Function(IAmazonDynamoDB dynamoDb, string usersTable = "Users", string waitersTable = "waiters-list")
    {
        _dynamoDb = dynamoDb;
        _usersTable = usersTable;
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

            if (!string.IsNullOrWhiteSpace(customerId))
            {
                var userResponse = await _dynamoDb.GetItemAsync(new GetItemRequest
                {
                    TableName = _usersTable,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["user_id"] = new AttributeValue { S = customerId }
                    },
                    ConsistentRead = true
                });

                var user = userResponse.Item == null || userResponse.Item.Count == 0
                    ? null
                    : MapCustomer(userResponse.Item);

                return ResponseCreator.CreateResponse(200, "Success",
                    user == null ? new List<CustomerDto>() : new List<CustomerDto> { user });
            }

            var normalizedQuery = searchQuery!.Trim();
            var scanResponse = await _dynamoDb.ScanAsync(new ScanRequest
            {
                TableName = _usersTable,
                FilterExpression = "contains(lower_username, :q) OR contains(username, :q) OR contains(user_id, :q)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":q"] = new AttributeValue { S = normalizedQuery.ToLowerInvariant() }
                },
                ProjectionExpression = "user_id, username",
                Limit = 50
            });

            var customers = scanResponse.Items
                .Select(MapCustomer)
                .Where(c => !string.IsNullOrWhiteSpace(c.Id))
                .OrderBy(c => c.Name)
                .ThenBy(c => c.Id)
                .Take(20)
                .ToList();

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

    private static CustomerDto MapCustomer(IReadOnlyDictionary<string, AttributeValue> item)
    {
        var id = GetString(item, "user_id");
        var name = GetString(item, "username");

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

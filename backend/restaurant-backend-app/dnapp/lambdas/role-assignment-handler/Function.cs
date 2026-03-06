using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace RoleAssignmentHandler;

public class Function
{
    private const string WaitersTableNameEnv = "WAITERS_TABLE_NAME";
    private const string WaiterRoleEnv = "WAITER_ROLE_NAME";
    private const string CustomerRoleEnv = "CUSTOMER_ROLE_NAME";

    private const string DefaultWaiterRole = "Waiter";
    private const string DefaultCustomerRole = "Customer";
    private const string DefaultWaitersTableName = "waiters-list";

    private readonly IAmazonDynamoDB _dynamoDbClient;
    private readonly IAmazonCognitoIdentityProvider _cognitoClient;

    public Function()
    {
        _dynamoDbClient = new AmazonDynamoDBClient();
        _cognitoClient = new AmazonCognitoIdentityProviderClient();
    }

    public Function(IAmazonDynamoDB dynamoDbClient, IAmazonCognitoIdentityProvider cognitoClient)
    {
        _dynamoDbClient = dynamoDbClient;
        _cognitoClient = cognitoClient;
    }

    public async Task<PostConfirmationEvent> FunctionHandler(PostConfirmationEvent cognitoEvent, ILambdaContext context)
    {
        if (cognitoEvent == null)
        {
            throw new ArgumentNullException(nameof(cognitoEvent));
        }

        var email = cognitoEvent.Request?.UserAttributes?.GetValueOrDefault("email")?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email))
        {
            context.Logger.LogLine("Post confirmation event received without email. Role assignment skipped.");
            return cognitoEvent;
        }

        var tableName = GetSetting(WaitersTableNameEnv, DefaultWaitersTableName);
        var waiterRole = GetSetting(WaiterRoleEnv, DefaultWaiterRole);
        var customerRole = GetSetting(CustomerRoleEnv, DefaultCustomerRole);

        var roleToAssign = await IsWaiterEmailAsync(tableName, email) ? waiterRole : customerRole;

        await _cognitoClient.AdminAddUserToGroupAsync(new AdminAddUserToGroupRequest
        {
            GroupName = roleToAssign,
            Username = cognitoEvent.UserName,
            UserPoolId = cognitoEvent.UserPoolId
        });

        context.Logger.LogLine($"Assigned role '{roleToAssign}' to user '{email}'.");

        return cognitoEvent;
    }

    private async Task<bool> IsWaiterEmailAsync(string tableName, string email)
    {
        var response = await _dynamoDbClient.GetItemAsync(new GetItemRequest
        {
            TableName = tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["email"] = new AttributeValue { S = email }
            },
            ProjectionExpression = "email"
        });

        return response.Item is { Count: > 0 };
    }

    private static string GetSetting(string key, string defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }
}


public class PostConfirmationEvent
{
    public string UserName { get; set; } = string.Empty;

    public string UserPoolId { get; set; } = string.Empty;

    public PostConfirmationRequest? Request { get; set; }
}

public class PostConfirmationRequest
{
    public Dictionary<string, string>? UserAttributes { get; set; }
}
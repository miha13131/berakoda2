using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Amazon.CognitoIdentityProvider;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Function.Models;
using shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SignUp;

public class SignUpFunction
{
    private const string WaitersTableNameEnv = "WAITERS_TABLE_NAME";
    private const string WaiterRoleEnv = "WAITER_ROLE_NAME";
    private const string CustomerRoleEnv = "CUSTOMER_ROLE_NAME";

    private const string DefaultWaitersTableName = "waiters-list";
    private const string DefaultWaiterRole = "Waiter";
    private const string DefaultCustomerRole = "Customer";

    private readonly IAmazonCognitoIdentityProvider _cognitoClient;
    private readonly IAmazonSimpleSystemsManagement _ssmClient;
    private readonly IAmazonDynamoDB _dynamoDbClient;

    private ICognitoService _cognitoService;

    private string _clientId;
    private string _clientSecret;

    public SignUpFunction()
    {
        _cognitoClient = new AmazonCognitoIdentityProviderClient();
        _ssmClient = new AmazonSimpleSystemsManagementClient();
        _dynamoDbClient = new AmazonDynamoDBClient();
    }

    public SignUpFunction(IAmazonCognitoIdentityProvider cognitoClient, IAmazonSimpleSystemsManagement ssmClient, IAmazonDynamoDB dynamoDbClient, string clientId = null, string clientSecret = null)
    {
        _cognitoClient = cognitoClient;
        _ssmClient = ssmClient;
        _dynamoDbClient = dynamoDbClient;
        _clientId = clientId;
        _clientSecret = clientSecret;
        if (clientId != null && clientSecret != null)
            _cognitoService = new CognitoService(_cognitoClient, _clientSecret, _clientId);
    }

    private async Task LoadParametersAsync()
    {
        if (_clientId != null && _clientSecret != null) return;

        var clientIdResponse = await _ssmClient.GetParameterAsync(new GetParameterRequest
        {
            Name = "/dnapp/cognito/client_id"
        });
        _clientId = clientIdResponse.Parameter.Value.Trim();

        var clientSecretResponse = await _ssmClient.GetParameterAsync(new GetParameterRequest
        {
            Name = "/dnapp/cognito/client_secret",
            WithDecryption = true
        });
        _clientSecret = clientSecretResponse.Parameter.Value.Trim();

        _cognitoService = new CognitoService(_cognitoClient, _clientSecret, _clientId);
    }

    public async Task<APIGatewayProxyResponse> SignUp(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        try
        {
            await LoadParametersAsync();

            if (string.IsNullOrWhiteSpace(request.Body))
                return ResponseCreator.CreateResponse(400, "Validation Error", "Request body is empty.");

            UserRegistrationDto? signUpData;
            
            try
            {
                signUpData = JsonSerializer.Deserialize<UserRegistrationDto>(request.Body,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
                    });
            }
            catch (JsonException ex)
            {
                return ResponseCreator.CreateResponse(400, "Validation Error", $"Invalid payload: {ex.Message}");
            }

            if (signUpData == null)
                return ResponseCreator.CreateResponse(400, "Validation Error", "Invalid request body.");

            if (string.IsNullOrWhiteSpace(signUpData.Email) ||
                string.IsNullOrWhiteSpace(signUpData.Password) ||
                string.IsNullOrWhiteSpace(signUpData.FirstName) ||
                string.IsNullOrWhiteSpace(signUpData.LastName))
            {
                return ResponseCreator.CreateResponse(400, "Validation Error", "All fields are required.");
            }

            signUpData.Email = signUpData.Email.Trim().ToLowerInvariant();
            signUpData.FirstName = signUpData.FirstName.Trim();
            signUpData.LastName = signUpData.LastName.Trim();

            if (!Regex.IsMatch(signUpData.Email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
                return ResponseCreator.CreateResponse(400, "Validation Error", "Invalid email format.");

            if (!Regex.IsMatch(signUpData.Password, @"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[\^$*.\[\]{}()?\- !@#%&/\\,>< :;|_~+= ]).{8,16}$"))
                return ResponseCreator.CreateResponse(400, "Validation Error", "Password must be 8-16 characters long and include an uppercase letter, a lowercase letter, a number, and a special character.");

            if (signUpData.FirstName.Length > 50 || signUpData.LastName.Length > 50)
                return ResponseCreator.CreateResponse(400, "Validation Error", "Name or Last Name too long (max 50 symbols).");

            if (!Regex.IsMatch(signUpData.FirstName, @"^[a-zA-Z\-']+$") || !Regex.IsMatch(signUpData.LastName, @"^[a-zA-Z\-']+$"))
                return ResponseCreator.CreateResponse(400, "Validation Error", "Name can only contain Latin letters, hyphens, and apostrophes.");

            var tableName = GetSetting(WaitersTableNameEnv, DefaultWaitersTableName);
            var waiterRole = GetSetting(WaiterRoleEnv, DefaultWaiterRole);
            var customerRole = GetSetting(CustomerRoleEnv, DefaultCustomerRole);
            var role = await IsWaiterEmailAsync(tableName, signUpData.Email) ? waiterRole : customerRole;

            await _cognitoService.SignUpAsync(
                signUpData.FirstName,
                signUpData.LastName,
                signUpData.Email,
                signUpData.Password,
                role);

            return ResponseCreator.CreateResponse(201, "User created successfully. Please verify your email.", new { email = signUpData.Email });
        }
        catch (Amazon.CognitoIdentityProvider.Model.UsernameExistsException)
        {
            return ResponseCreator.CreateResponse(409, "Conflict", "A user with this email address already exists.");
        }
        catch (Amazon.CognitoIdentityProvider.Model.InvalidPasswordException ex)
        {
            return ResponseCreator.CreateResponse(400, "Invalid Password", ex.Message);
        }
        catch (Exception ex)
        {
            context.Logger.LogLine($"Error: {ex.Message}");
            return ResponseCreator.CreateResponse(500, "Internal Server Error", "An unexpected error occurred.");
        }
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
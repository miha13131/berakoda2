using System;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ForgotPasswordHandler;

public class Function
{
    private readonly IAmazonCognitoIdentityProvider _cognitoClient;
    private readonly IAmazonSimpleSystemsManagement _ssmClient;
    private string _clientId;
    private string _clientSecret;

    public Function()
    {
        _cognitoClient = new AmazonCognitoIdentityProviderClient();
        _ssmClient = new AmazonSimpleSystemsManagementClient();
    }

    public Function(IAmazonCognitoIdentityProvider cognitoClient, IAmazonSimpleSystemsManagement ssmClient, string clientId = null, string clientSecret = null)
    {
        _cognitoClient = cognitoClient;
        _ssmClient = ssmClient;
        _clientId = clientId;
        _clientSecret = clientSecret;
    }

    private async Task LoadParametersAsync()
    {
        if (_clientId != null && _clientSecret != null) return;
        _clientId = (await _ssmClient.GetParameterAsync(new GetParameterRequest { Name = "/dnapp/cognito/client_id"})).Parameter.Value;
        _clientSecret = (await _ssmClient.GetParameterAsync(new GetParameterRequest { Name = "/dnapp/cognito/client_secret", WithDecryption = true })).Parameter.Value;
    }

    private sealed record ForgotPasswordReq(string email);

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        await LoadParametersAsync();

        if (string.IsNullOrWhiteSpace(request.Body))
            return ResponseCreator.CreateResponse(400, "Validation Error", "Request body is empty.");

        var body = JsonSerializer.Deserialize<ForgotPasswordReq>(request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        
        if (body == null || string.IsNullOrWhiteSpace(body.email))
            return ResponseCreator.CreateResponse(400, "Validation Error", "Please enter a valid email address.");

        try
        {
            var secretHash = SecretHashGenerator.GenerateSecretHash(body.email, _clientId, _clientSecret);

            await _cognitoClient.ForgotPasswordAsync(new ForgotPasswordRequest
            {
                ClientId = _clientId,
                Username = body.email,
                SecretHash = secretHash
            });
            
            return ResponseCreator.CreateResponse(200, "Success", "Confirmation code sent to your email.");
        }
        catch (UserNotFoundException)
        {
            return ResponseCreator.CreateResponse(404, "Not Found", "No user with this email address is registered.");
        }
        catch (LimitExceededException)
        {
            return ResponseCreator.CreateResponse(429, "Too Many Requests", "Too many attempts. Please wait before trying again.");
        }
        catch (InvalidParameterException)
        {
            return ResponseCreator.CreateResponse(400, "Validation Error", 
                "Cannot reset password for unverified account. Please verify your email first.");
        }
        catch (TooManyRequestsException)
        {
            return ResponseCreator.CreateResponse(429, "Too Many Requests", "Too many requests. Please wait and try again.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error in ForgotPassword: {ex.Message}");
            return ResponseCreator.CreateResponse(500, "Internal Server Error", "An error occurred while processing your request.");
        }
    }
}
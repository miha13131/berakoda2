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

namespace ConfirmPasswordHandler;

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

    private sealed record ConfirmPasswordReq(string email, string verificationCode, string newPassword);

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        await LoadParametersAsync();

        if (string.IsNullOrWhiteSpace(request.Body))
            return ResponseCreator.CreateResponse(400, "Validation Error", "Request body is empty.");

        var body = JsonSerializer.Deserialize<ConfirmPasswordReq>(request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        
        if (body == null || string.IsNullOrWhiteSpace(body.email) || string.IsNullOrWhiteSpace(body.verificationCode) || string.IsNullOrWhiteSpace(body.newPassword))
            return ResponseCreator.CreateResponse(400, "Validation Error", "Email, verification code, and new password are required");

        try
        {
            var secretHash = SecretHashGenerator.GenerateSecretHash(body.email, _clientId, _clientSecret);

            await _cognitoClient.ConfirmForgotPasswordAsync(new ConfirmForgotPasswordRequest
            {
                ClientId = _clientId,
                Username = body.email,
                ConfirmationCode = body.verificationCode,
                Password = body.newPassword,
                SecretHash = secretHash
            });
            
            return ResponseCreator.CreateResponse(200, "Success", "Password has been successfully reset. You can now log in.");
        }
        catch (CodeMismatchException)
        {
            return ResponseCreator.CreateResponse(400, "Auth Error", "Invalid verification code. Please try again.");
        }
        catch (ExpiredCodeException)
        {
            return ResponseCreator.CreateResponse(400, "Auth Error", "Verification code has expired. Please request a new one.");
        }
        catch (InvalidPasswordException)
        {
            return ResponseCreator.CreateResponse(400, "Validation Error", "Password does not meet the security policy requirements.");
        }
        catch (UserNotFoundException)
        {
            return ResponseCreator.CreateResponse(404, "Not Found", "No user with this email address is registered.");
        }
        catch (UserNotConfirmedException)
        {
            return ResponseCreator.CreateResponse(400, "Validation Error", "Please verify your email before resetting password.");
        }
        catch (LimitExceededException)
        {
            return ResponseCreator.CreateResponse(429, "Too Many Requests", "Attempt limit exceeded. Please wait a while before trying again.");
        }
        catch (TooManyRequestsException)
        {
            return ResponseCreator.CreateResponse(429, "Too Many Requests", "Too many requests. Please wait and try again.");
        }
        catch (Exception ex) 
        {
            context.Logger.LogError($"Critical error: {ex.Message}");
            return ResponseCreator.CreateResponse(500, "Internal Server Error", "An internal server error occurred.");
        }
    }
}
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SendConfirmation;

public class Function
{
    private readonly IAmazonCognitoIdentityProvider _cognitoClient;
    private readonly IAmazonSimpleSystemsManagement _ssmClient;
    
    private ICognitoService _cognitoService;
    
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
    
    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        await LoadParametersAsync();

        if (string.IsNullOrWhiteSpace(request.Body))
            return ResponseCreator.CreateResponse(400, "Validation Error", "Request body is empty.");
    
        try
        {
            ConfirmEmailDto? dto;

            try
            {
                dto = JsonSerializer.Deserialize<ConfirmEmailDto>(request.Body, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true,
                    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
                });
            }
            catch (JsonException ex)
            {
                return ResponseCreator.CreateResponse(400, "Validation Error", $"Invalid payload: {ex.Message}");
            }

            if (dto == null || string.IsNullOrEmpty(dto.Email) || string.IsNullOrEmpty(dto.VerificationCode))
            {
                return ResponseCreator.CreateResponse(400, "Validation Error", "Email and VerificationCode are required.");
            }
            
            var secretHash = SecretHashGenerator.GenerateSecretHash(
                dto.Email, 
                _clientId, 
                _clientSecret
            );
            
            var confirmRequest = new ConfirmSignUpRequest
            {
                ClientId = _clientId,
                Username = dto.Email,
                ConfirmationCode = dto.VerificationCode,
                SecretHash = secretHash
            };

            await _cognitoClient.ConfirmSignUpAsync(confirmRequest);
        
            return ResponseCreator.CreateResponse(200, "Success", "Email successfully verified.");
        }
        catch (CodeMismatchException)
        {
                return ResponseCreator.CreateResponse(400, "Verification Error", "Invalid verification code.");
        }
        catch (ExpiredCodeException)
        {
                return ResponseCreator.CreateResponse(400, "Verification Error", "Verification code has expired. Please request a new one.");
        }
        catch (UserNotFoundException)
        {
            return ResponseCreator.CreateResponse(404, "Not Found", "User with this email does not exist.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error confirming email: {ex.Message}");
            return ResponseCreator.CreateResponse(500, "Internal Server Error", "An error occurred during verification.");
        }
    }
}

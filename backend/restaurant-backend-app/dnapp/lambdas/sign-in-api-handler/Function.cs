using System;
using System.Collections.Generic;
using System.Linq;
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

namespace SignInApiHandler;

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

    private bool IsValidEmail(string email)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(
            email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
    }

    private sealed record SignInRequest(string email, string password);

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        await LoadParametersAsync();

        if (string.IsNullOrWhiteSpace(request.Body))
            return ResponseCreator.CreateResponse(400, "Validation Error", "Request body is empty.");

        var body = JsonSerializer.Deserialize<SignInRequest>(request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        
        if (body == null || string.IsNullOrWhiteSpace(body.email) || string.IsNullOrWhiteSpace(body.password))
            return ResponseCreator.CreateResponse(400, "Validation Error", "Email and password are required.");

        if (!IsValidEmail(body.email))
            return ResponseCreator.CreateResponse(400, "Validation Error", "Please enter a valid email address.");

        try
        {
            var secretHash = SecretHashGenerator.GenerateSecretHash(body.email, _clientId, _clientSecret);

            var authRequest = new InitiateAuthRequest
            {
                ClientId = _clientId,
                AuthFlow = AuthFlowType.USER_PASSWORD_AUTH,
                AuthParameters = new Dictionary<string, string>
                {
                    { "USERNAME", body.email },
                    { "PASSWORD", body.password },
                    { "SECRET_HASH", secretHash }
                }
            };

            var authResponse = await _cognitoClient.InitiateAuthAsync(authRequest);

            if (authResponse.ChallengeName == ChallengeNameType.NEW_PASSWORD_REQUIRED)
            {
                return ResponseCreator.CreateResponse(200, "Login successful! (Password change required)", new { temp_session = authResponse.Session });
            }

            var accessToken = authResponse.AuthenticationResult?.AccessToken;
            var idToken = authResponse.AuthenticationResult?.IdToken;
            var refreshToken = authResponse.AuthenticationResult?.RefreshToken;

            var finalUsername = body.email;
            var finalRole = "Customer";
            if (!string.IsNullOrEmpty(idToken))
            {
                var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
                var jwtToken = handler.ReadJwtToken(idToken);

                var givenName = jwtToken.Claims.FirstOrDefault(c => c.Type == "given_name" || c.Type == "custom:firstName")?.Value;
                var familyName = jwtToken.Claims.FirstOrDefault(c => c.Type == "family_name" || c.Type == "custom:lastName")?.Value;
                
                if (!string.IsNullOrWhiteSpace(givenName) || !string.IsNullOrWhiteSpace(familyName))
                    finalUsername = $"{givenName} {familyName}".Trim();

                var groupClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == "cognito:groups");
                if (groupClaim != null) finalRole = groupClaim.Value; 
            }

            return ResponseCreator.CreateResponse(200, "Successful login", new 
            {
                accessToken, idToken, refreshToken, username = finalUsername, role = finalRole 
            });
        }
        catch (UserNotConfirmedException)
        {
            return ResponseCreator.CreateResponse(403, "Email Not Verified", 
                "Your email address has not been verified. Please check your inbox for a verification code.");
        }
        catch (PasswordResetRequiredException)
        {
            return ResponseCreator.CreateResponse(403, "Password Reset Required", 
                "You need to reset your password before logging in. Please use the forgot password flow.");
        }
        catch (NotAuthorizedException ex)
        {
            if (ex.Message.Contains("attempts exceeded", StringComparison.OrdinalIgnoreCase))
                return ResponseCreator.CreateResponse(429, "Too Many Attempts", 
                    "Your account is temporarily locked due to multiple failed login attempts. Please try again later.");
            return ResponseCreator.CreateResponse(401, "Auth Error", "Incorrect email or password.");
        }
        catch (UserNotFoundException)
        {
            return ResponseCreator.CreateResponse(401, "Auth Error", "Incorrect email or password.");
        }
        catch (TooManyRequestsException)
        {
            return ResponseCreator.CreateResponse(429, "Too Many Requests", 
                "Too many requests. Please wait a moment and try again.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Critical error: {ex.Message}");
            return ResponseCreator.CreateResponse(500, "Internal Server Error", "An internal server error occurred.");
        }
    }
}
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using shared; 

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace RefreshTokenHandler
{
    public class Function
    {
        private readonly AmazonCognitoIdentityProviderClient _cognitoClient;
        private readonly AmazonSimpleSystemsManagementClient _ssmClient;
        private string _clientId;
        private string _clientSecret;

        public Function()
        {
            _cognitoClient = new AmazonCognitoIdentityProviderClient();
            _ssmClient = new AmazonSimpleSystemsManagementClient();
        }

        private async Task LoadParametersAsync()
        {
            if (_clientId != null) return;
            
            _clientId = (await _ssmClient.GetParameterAsync(new GetParameterRequest { Name = "/dnapp/cognito/client_id" })).Parameter.Value;
            _clientSecret = (await _ssmClient.GetParameterAsync(new GetParameterRequest { Name = "/dnapp/cognito/client_secret", WithDecryption = true })).Parameter.Value;
        }

        private sealed record RefreshRequest(string accessToken, string refreshToken);

        public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.Body))
                    return ResponseCreator.CreateResponse(400, "Request body is empty.", null);

                string jsonBody = request.IsBase64Encoded 
                    ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(request.Body)) 
                    : request.Body;

                var body = JsonSerializer.Deserialize<RefreshRequest>(jsonBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (string.IsNullOrWhiteSpace(body?.accessToken) || string.IsNullOrWhiteSpace(body?.refreshToken))
                    return ResponseCreator.CreateResponse(400, "Access token and refresh token are required.", null);

                var handler = new JwtSecurityTokenHandler();
                var jwtToken = handler.ReadJwtToken(body.accessToken);
                var systemUsername = jwtToken.Claims.First(claim => claim.Type == "username").Value;

                await LoadParametersAsync();

                var authRequest = new InitiateAuthRequest
                {
                    ClientId = _clientId,
                    AuthFlow = AuthFlowType.REFRESH_TOKEN_AUTH,
                    AuthParameters = new Dictionary<string, string>
                    {
                        { "REFRESH_TOKEN", body.refreshToken },
                        { "SECRET_HASH", SecretHashGenerator.GenerateSecretHash(systemUsername, _clientId, _clientSecret) }
                    }
                };

                var authResponse = await _cognitoClient.InitiateAuthAsync(authRequest);

                return ResponseCreator.CreateResponse(200, "Token refreshed successfully", new
                {
                    accessToken = authResponse.AuthenticationResult.AccessToken,
                    idToken = authResponse.AuthenticationResult.IdToken,
                    expiresIn = authResponse.AuthenticationResult.ExpiresIn
                });
            }
            catch (NotAuthorizedException)
            {
                return ResponseCreator.CreateResponse(401, "Refresh token is invalid or expired. Please log in again.", null);
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Critical error: {ex.Message}");
                return ResponseCreator.CreateResponse(500, "An internal server error occurred.", null);
            }
        }
    }
}
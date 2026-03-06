using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using System.Security.Cryptography;
using System.Text;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

    namespace ResendConfirmation
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

        private string CalculateSecretHash(string clientId, string clientSecret, string userName)
        {
            var data = userName + clientId;
            byte[] keyBytes = Encoding.UTF8.GetBytes(clientSecret);
            byte[] messageBytes = Encoding.UTF8.GetBytes(data);
            using (var hmac = new HMACSHA256(keyBytes))
            {
                return Convert.ToBase64String(hmac.ComputeHash(messageBytes));
            }
        }

        private sealed record ResendConfirmationReq(string email);

        public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
        {
            try
            {
                var body = JsonSerializer.Deserialize<ResendConfirmationReq>(request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (string.IsNullOrWhiteSpace(body?.email))
                    return ErrorResponse(400, "Email is required.");

                await LoadParametersAsync();

                var resendRequest = new ResendConfirmationCodeRequest
                {
                    ClientId = _clientId,
                    Username = body.email,
                    SecretHash = CalculateSecretHash(_clientId, _clientSecret, body.email)
                };

                await _cognitoClient.ResendConfirmationCodeAsync(resendRequest);

                return SuccessResponse(200, "A new confirmation code has been sent to your email.");
            }
            catch (UserNotFoundException)
            {
                return ErrorResponse(404, "No user with this email address is registered.");
            }
            catch (InvalidParameterException ex) when (ex.Message.Contains("is already confirmed"))
            {
                return ErrorResponse(400, "This account is already confirmed. You can log in.");
            }
            catch (LimitExceededException)
            {
                return ErrorResponse(400, "Attempt limit exceeded. Please wait a while before requesting a new code.");
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Error: {ex.Message}");
                return ErrorResponse(500, "An internal server error occurred.");
            }
        }

        private static APIGatewayProxyResponse SuccessResponse(int statusCode, string description)
        {
            return new APIGatewayProxyResponse
            {
                StatusCode = statusCode,
                Body = JsonSerializer.Serialize(new { status = "success", description, value = (object)null }),
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" }, { "Access-Control-Allow-Origin", "*" } }
            };
        }

        private static APIGatewayProxyResponse ErrorResponse(int statusCode, string description)
        {
            return new APIGatewayProxyResponse
            {
                StatusCode = statusCode,
                Body = JsonSerializer.Serialize(new { status = "error", description, value = (object)null }),
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" }, { "Access-Control-Allow-Origin", "*" } }
            };
        }
    }
}
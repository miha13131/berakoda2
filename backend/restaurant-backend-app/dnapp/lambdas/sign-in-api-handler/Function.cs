using System;
using System.Collections.Generic;
using System.Linq;
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

namespace SignInApiHandler
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

            _clientId = (await _ssmClient.GetParameterAsync(new GetParameterRequest { Name = "/dnapp/cognito/client_id"})).Parameter.Value;

            _clientSecret = (await _ssmClient.GetParameterAsync(new GetParameterRequest { Name = "/dnapp/cognito/client_secret", WithDecryption = true })).Parameter.Value;
        }
        private string CalculateSecretHash(string clientId, string clientSecret, string userName)
        {
            var data = userName + clientId;
            byte[] keyBytes = Encoding.UTF8.GetBytes(clientSecret);
            byte[] messageBytes = Encoding.UTF8.GetBytes(data);

            using (var hmac = new HMACSHA256(keyBytes))
            {
                byte[] hashBytes = hmac.ComputeHash(messageBytes);
                return Convert.ToBase64String(hashBytes);
            }
        }

        private bool IsValidEmail(string email)
        {
            try {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            } catch {
                return false;
            }
        }

        private sealed record SignOutRequest(string accessToken);

        private async Task<APIGatewayProxyResponse> HandleSignOut(APIGatewayProxyRequest request, ILambdaContext context)
        {
            var body = JsonSerializer.Deserialize<SignOutRequest>(request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            

            if (string.IsNullOrWhiteSpace(body?.accessToken))
            {
                return ErrorResponse(400, "Access token is required for sign out.");
            }

            try
            {
                await _cognitoClient.GlobalSignOutAsync(new GlobalSignOutRequest
                {
                    AccessToken = body.accessToken
                });

                return SuccessResponse(200, "Successfully signed out from all devices.", null);
            }
            catch (NotAuthorizedException)
            {
                return ErrorResponse(401, "Invalid or expired access token.");
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"SignOut Error: {ex.Message}");
                return ErrorResponse(500, "An internal server error occurred during sign out.");
            }
        }

        public async Task<APIGatewayProxyResponse> SignInApi(APIGatewayProxyRequest request, ILambdaContext context)
        {
            try
            {
                var path = request.Resource;

                return path switch
                {
                    "/auth/sign-in" => await HandleSignIn(request, context),
                    "/auth/sign-out" => await HandleSignOut(request, context),
                    "/auth/forgot-password" => await HandleForgotPassword(request, context),
                    "/auth/confirm-password" => await HandleConfirmPassword(request, context),
                    _ => ErrorResponse(404, $"Path {path} not found")
                };
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Critical error: {ex.Message}");
                return ErrorResponse(500, "An internal server error occurred.");
            }
        }

        // --- 1. SIGN IN ---
        private sealed record SignInRequest(string email, string password);

        private async Task<APIGatewayProxyResponse> HandleSignIn(APIGatewayProxyRequest request, ILambdaContext context)
        {
            await LoadParametersAsync();

            var body = JsonSerializer.Deserialize<SignInRequest>(request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
            if (!IsValidEmail(body.email))
                return ErrorResponse(400, "Please enter a valid email address.");
            
            if (string.IsNullOrWhiteSpace(body?.email) || string.IsNullOrWhiteSpace(body?.password))
                return ErrorResponse(400, "Email and password are required");

            try
            {
                var authRequest = new InitiateAuthRequest
                {
                    ClientId = _clientId,
                    AuthFlow = AuthFlowType.USER_PASSWORD_AUTH,
                    AuthParameters = new Dictionary<string, string>
                    {
                        { "USERNAME", body.email },
                        { "PASSWORD", body.password },
                        { "SECRET_HASH", CalculateSecretHash(_clientId, _clientSecret, body.email) }
                    }
                };

                var authResponse = await _cognitoClient.InitiateAuthAsync(authRequest);

                if (authResponse.ChallengeName == ChallengeNameType.NEW_PASSWORD_REQUIRED)
                {
                    return SuccessResponse(200, "Login successful! (Password change required)", new { temp_session = authResponse.Session });
                }

                var accessToken = authResponse.AuthenticationResult?.AccessToken;
                var idToken = authResponse.AuthenticationResult?.IdToken;
                var refreshToken = authResponse.AuthenticationResult?.RefreshToken;

                var finalUsername = body.email;
                var finalRole = "CUSTOMER";
                if (!string.IsNullOrEmpty(idToken))
                {
                    var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
                    var jwtToken = handler.ReadJwtToken(idToken);

                    var givenName = jwtToken.Claims.FirstOrDefault(c => c.Type == "given_name" || c.Type == "custom:firstName")?.Value;
                    var familyName = jwtToken.Claims.FirstOrDefault(c => c.Type == "family_name" || c.Type == "custom:lastName")?.Value;
                    
                    if (!string.IsNullOrWhiteSpace(givenName) || !string.IsNullOrWhiteSpace(familyName))
                    {
                        finalUsername = $"{givenName} {familyName}".Trim();
                    }

                    var groupClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == "cognito:groups");
                    if (groupClaim != null) finalRole = groupClaim.Value; 
                }

                return SuccessResponse(200, "Successful login", new 
                {
                    accessToken = accessToken,
                    idToken = idToken,
                    refreshToken = refreshToken,
                    username = finalUsername,
                    role = finalRole 
                });
            }
            catch (NotAuthorizedException ex)
            {
                if (ex.Message.Contains("attempts exceeded", StringComparison.OrdinalIgnoreCase))
                    return ErrorResponse(400, "Your account is temporarily locked due to multiple failed login attempts. Please try again later.");

                return ErrorResponse(400, "Incorrect email or password. Try again or create an account.");
            }
            catch (UserNotFoundException)
            {
                return ErrorResponse(400, "Incorrect email or password. Try again or create an account.");
            }
            catch (TooManyRequestsException)
            {
                return ErrorResponse(400, "Your account is temporarily locked due to multiple failed login attempts. Please try again later.");
            }
        }

        // --- 2. FORGOT PASSWORD ---
        private sealed record ForgotPasswordReq(string email);

        private async Task<APIGatewayProxyResponse> HandleForgotPassword(APIGatewayProxyRequest request, ILambdaContext context)
        {
            await LoadParametersAsync();

            var body = JsonSerializer.Deserialize<ForgotPasswordReq>(request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
            if (string.IsNullOrWhiteSpace(body?.email) || !IsValidEmail(body.email))
                return ErrorResponse(400, "Please enter a valid email address.");

            try
            {
                await _cognitoClient.ForgotPasswordAsync(new ForgotPasswordRequest
                {
                    ClientId = _clientId,
                    Username = body.email,
                    SecretHash = CalculateSecretHash(_clientId, _clientSecret, body.email)
                });
                
                return SuccessResponse(200, "Confirmation code sent to your email.", null);
            }
            catch (UserNotFoundException)
            {
                return ErrorResponse(404, "No user with this email address is registered.");
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Error in ForgotPassword: {ex.Message}");
                return ErrorResponse(500, "An error occurred while processing your request.");
            }
        }

        // --- 3. CONFIRM PASSWORD ---
        private sealed record ConfirmPasswordReq(string email, string verificationCode, string newPassword);

        private async Task<APIGatewayProxyResponse> HandleConfirmPassword(APIGatewayProxyRequest request, ILambdaContext context)
        {
            await LoadParametersAsync();

            var body = JsonSerializer.Deserialize<ConfirmPasswordReq>(request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
            if (string.IsNullOrWhiteSpace(body?.email) || string.IsNullOrWhiteSpace(body?.verificationCode) || string.IsNullOrWhiteSpace(body?.newPassword))
                return ErrorResponse(400, "Email, verification code, and new password are required");

            try
            {
                await _cognitoClient.ConfirmForgotPasswordAsync(new ConfirmForgotPasswordRequest
                {
                    ClientId = _clientId,
                    Username = body.email,
                    ConfirmationCode = body.verificationCode,
                    Password = body.newPassword,
                    SecretHash = CalculateSecretHash(_clientId, _clientSecret, body.email) 
                });
                
                return SuccessResponse(200, "Password has been successfully reset. You can now log in.", null);
            }
            catch (CodeMismatchException)
            {
                return ErrorResponse(400, "Invalid verification code. Please try again.");
            }
            catch (ExpiredCodeException)
            {
                return ErrorResponse(400, "Verification code has expired. Please request a new one.");
            }
            catch (InvalidPasswordException)
            {
                return ErrorResponse(400, "Password does not meet the security policy requirements.");
            }
            catch (LimitExceededException)
            {
                return ErrorResponse(400, "Attempt limit exceeded. Please wait a while before trying again.");
            }
            catch (Exception ex) 
            {
                context.Logger.LogError($"Critical error: {ex.Message}");
                return ErrorResponse(500, "An internal server error occurred.");
            }
        }

        private static APIGatewayProxyResponse SuccessResponse(int statusCode, string description, object value)
        {
            return new APIGatewayProxyResponse
            {
                StatusCode = statusCode,
                Body = JsonSerializer.Serialize(new { status = "success", description, value }),
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
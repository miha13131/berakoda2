using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Amazon.CognitoIdentityProvider;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Function.Models;
using Function.Services;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace Function;

public class SignUpFunction
{
    private readonly AmazonCognitoIdentityProviderClient _cognitoClient;
    private readonly AmazonSimpleSystemsManagementClient _ssmClient;
    
    private ICognitoService _cognitoService;
    
    private string _clientId;
    private string _clientSecret;

    public SignUpFunction()
    {
        _cognitoClient = new AmazonCognitoIdentityProviderClient();
        _ssmClient = new AmazonSimpleSystemsManagementClient();
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

            var signUpData = JsonSerializer.Deserialize<UserRegistrationDto>(request.Body, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (signUpData == null) 
                return ResponseCreator.CreateResponse(400, "Validation Error", "Invalid request body.");
            
            if (string.IsNullOrWhiteSpace(signUpData.Email) || 
                string.IsNullOrWhiteSpace(signUpData.Password) ||
                string.IsNullOrWhiteSpace(signUpData.FirstName) || 
                string.IsNullOrWhiteSpace(signUpData.LastName))
            {
                return ResponseCreator.CreateResponse(400, "Validation Error", "All fields are required.");
            }
            
            signUpData.Email = signUpData.Email.Trim().ToLowerInvariant();;
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
            
            await _cognitoService.SignUpAsync(
                signUpData.FirstName,
                signUpData.LastName,
                signUpData.Email,
                signUpData.Password);

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
}

using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;

namespace shared;

public class CognitoService : ICognitoService
{
    private readonly IAmazonCognitoIdentityProvider _cognitoClient;
    private readonly string _clientId;
    private readonly string _clientSecret;

    public CognitoService(IAmazonCognitoIdentityProvider cognitoClient, string clientSecret, string clientId)
    {
        _cognitoClient = cognitoClient;
        _clientId = clientId;
        _clientSecret = clientSecret;
    }

    public async Task SignUpAsync(string firstName, string lastName, string email, string password)
    {
        var secretHash = SecretHashGenerator.GenerateSecretHash(
            email,
            _clientId,
            _clientSecret);
        
        var request = new SignUpRequest
        {
            ClientId = _clientId,
            SecretHash = secretHash,
            Username = email,
            Password = password,
            
            UserAttributes = new List<AttributeType>
            {
                new AttributeType { Name = "email", Value = email },
                new AttributeType { Name = "given_name", Value = firstName },
                new AttributeType { Name = "family_name", Value = lastName }
            }
        };

        await _cognitoClient.SignUpAsync(request);
    }
}
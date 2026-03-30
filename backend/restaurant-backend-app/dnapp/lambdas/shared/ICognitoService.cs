namespace shared;

public interface ICognitoService
{
    Task SignUpAsync(string firstName, string lastName, string email, string password, string role);
}
using System.Threading.Tasks;

namespace Function.Services;

public interface ICognitoService
{
    Task SignUpAsync(string firstName, string lastName, string email, string password);
}
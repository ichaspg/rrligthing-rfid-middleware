using System.Security.Cryptography;
using System.Text;

namespace RfidMiddleware.Host;

public static class PublicApiSecurity
{
    public static bool HasValidBearerToken(string authorization, string configuredToken)
    {
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || configuredToken.Length == 0)
            return false;
        var suppliedToken = authorization[prefix.Length..].Trim();
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedToken);
        var configuredBytes = Encoding.UTF8.GetBytes(configuredToken);
        return suppliedBytes.Length == configuredBytes.Length &&
            CryptographicOperations.FixedTimeEquals(suppliedBytes, configuredBytes);
    }
}

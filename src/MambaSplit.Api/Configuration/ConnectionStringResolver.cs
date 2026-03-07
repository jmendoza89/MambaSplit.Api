namespace MambaSplit.Api.Configuration;

public static class ConnectionStringResolver
{
    public static string Resolve(IConfiguration configuration)
    {
        var explicitConnection = configuration.GetConnectionString("Default");
        if (!string.IsNullOrWhiteSpace(explicitConnection))
        {
            return explicitConnection;
        }
        return string.Empty;
    }
}

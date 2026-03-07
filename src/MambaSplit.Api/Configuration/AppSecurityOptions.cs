namespace MambaSplit.Api.Configuration;

public class AppSecurityOptions
{
    public JwtOptions Jwt { get; set; } = new();
    public GoogleOptions Google { get; set; } = new();
}

public class JwtOptions
{
    public string Issuer { get; set; } = "mambasplit-api";
    public string Secret { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
}

public class GoogleOptions
{
    public string ClientId { get; set; } = string.Empty;
}

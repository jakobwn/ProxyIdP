namespace ProxyIdP.Options;

public class ProxyOptions
{
    public string Issuer { get; set; } = "";
    public int AuthorizationCodeLifetimeSeconds { get; set; } = 300;
    public int PendingAuthorizationLifetimeMinutes { get; set; } = 10;
    public int TokenLifetimeMinutes { get; set; } = 30;
}

public class SigningKeyOptions
{
    public int KeyLifetimeDays { get; set; } = 30;
    public int RotationIntervalDays { get; set; } = 14;
    public int MinimumValidKeys { get; set; } = 2;
    public int ExpiredKeyGraceDays { get; set; } = 1;
}

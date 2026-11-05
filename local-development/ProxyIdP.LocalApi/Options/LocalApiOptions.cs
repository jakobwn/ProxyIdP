namespace ProxyIdP.LocalApi.Options;

public class LocalApiOptions
{
    public const string SectionName = "LocalApi";

    public Dictionary<string, string> DomainMappings { get; set; } = [];
    public string? DefaultIdpId { get; set; }
    }

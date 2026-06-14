namespace MkGreens.IdentitySync.Configuration;

public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    public int IntervalMinutes { get; set; } = 15;
    public string StateStorePath { get; set; } = "data/identity-sync.db";
    public bool DryRun { get; set; }
    public bool DisableUsersMissingFromSource { get; set; }
    public bool RunImmediatelyOnStart { get; set; } = true;
    public List<TagMappingOptions> TagMappings { get; set; } = [];
}

public sealed class TagMappingOptions
{
    public string Name { get; set; } = string.Empty;
    public string TagId { get; set; } = string.Empty;
    public string KeycloakGroupPath { get; set; } = string.Empty;
}

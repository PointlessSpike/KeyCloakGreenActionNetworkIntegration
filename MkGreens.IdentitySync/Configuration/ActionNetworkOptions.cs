namespace MkGreens.IdentitySync.Configuration;

public sealed class ActionNetworkOptions
{
    public const string SectionName = "ActionNetwork";

    public string BaseUrl { get; set; } = "https://actionnetwork.org/api/v2/";
    public string? ApiToken { get; set; }
    public int PeoplePageSize { get; set; } = 100;
    public int TaggingsPageSize { get; set; } = 100;
}

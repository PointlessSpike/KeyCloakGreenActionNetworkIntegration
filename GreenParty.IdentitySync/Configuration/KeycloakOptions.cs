namespace GreenParty.IdentitySync.Configuration;

public sealed class KeycloakOptions
{
    public const string SectionName = "Keycloak";

    public string BaseUrl { get; set; } = "http://localhost:8080/";
    public string Realm { get; set; } = "greenparty-local";
    public string AuthenticationRealm { get; set; } = "master";
    public string AdminClientId { get; set; } = "admin-cli";
    public string? ClientSecret { get; set; }
    public string? AdminUsername { get; set; }
    public string? AdminPassword { get; set; }
    public bool VerifySsl { get; set; } = true;
    public bool MarkEmailVerified { get; set; } = true;
}

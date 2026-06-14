namespace GreenParty.IdentitySync.Models;

public sealed record ActionNetworkPerson(
    string PersonId,
    string? GivenName,
    string? FamilyName,
    string? Email,
    DateTimeOffset? ModifiedDate,
    string? Source)
{
    public string DisplayName =>
        string.Join(" ", new[] { GivenName, FamilyName }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();

    public string StateHash =>
        string.Join("|", PersonId, Email ?? string.Empty, GivenName ?? string.Empty, FamilyName ?? string.Empty, ModifiedDate?.ToString("O") ?? string.Empty, Source ?? string.Empty);
}

namespace MkGreens.IdentitySync.Models;

public sealed record KeycloakUserSummary(
    string Id,
    string Username,
    string? Email,
    bool Enabled,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Attributes);

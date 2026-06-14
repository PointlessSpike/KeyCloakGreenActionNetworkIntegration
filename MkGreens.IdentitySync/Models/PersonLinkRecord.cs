namespace MkGreens.IdentitySync.Models;

public sealed record PersonLinkRecord(
    string ActionNetworkPersonId,
    string KeycloakUserId,
    string? Email,
    string? DisplayName,
    string? LastSyncedHash,
    DateTimeOffset LastSeenAt,
    DateTimeOffset LastSyncedAt);

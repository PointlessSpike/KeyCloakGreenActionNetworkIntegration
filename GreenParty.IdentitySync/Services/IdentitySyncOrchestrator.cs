using Microsoft.Extensions.Options;
using GreenParty.IdentitySync.Configuration;
using GreenParty.IdentitySync.Models;

namespace GreenParty.IdentitySync.Services;

public sealed class IdentitySyncOrchestrator
{
    private readonly ActionNetworkClient _actionNetworkClient;
    private readonly KeycloakAdminClient _keycloakAdminClient;
    private readonly ISyncStateStore _stateStore;
    private readonly SyncOptions _syncOptions;
    private readonly ILogger<IdentitySyncOrchestrator> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public IdentitySyncOrchestrator(
        ActionNetworkClient actionNetworkClient,
        KeycloakAdminClient keycloakAdminClient,
        ISyncStateStore stateStore,
        IOptions<SyncOptions> syncOptions,
        ILogger<IdentitySyncOrchestrator> logger)
    {
        _actionNetworkClient = actionNetworkClient;
        _keycloakAdminClient = keycloakAdminClient;
        _stateStore = stateStore;
        _syncOptions = syncOptions.Value;
        _logger = logger;
    }

    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        if (!await _runLock.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            _logger.LogWarning("A sync run is already in progress; skipping overlapping request.");
            return false;
        }

        try
        {
            await _stateStore.InitializeAsync(cancellationToken);
            var runId = await _stateStore.StartRunAsync(DateTimeOffset.UtcNow, cancellationToken);
            var stats = new SyncRunStats();

            try
            {
                var people = await _actionNetworkClient.GetPeopleAsync(cancellationToken);
                stats.PeopleSeen = people.Count;

                var managedGroupPaths = _syncOptions.TagMappings
                    .Where(mapping => !string.IsNullOrWhiteSpace(mapping.TagId) && !string.IsNullOrWhiteSpace(mapping.KeycloakGroupPath))
                    .Select(mapping => NormaliseGroupPath(mapping.KeycloakGroupPath))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var groupIdsByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var groupPath in managedGroupPaths)
                {
                    if (_syncOptions.DryRun)
                    {
                        groupIdsByPath[groupPath] = groupPath;
                        continue;
                    }

                    var group = await _keycloakAdminClient.EnsureGroupPathAsync(groupPath, cancellationToken);
                    groupIdsByPath[groupPath] = group.Id;
                }

                var tagMemberships = await LoadTagMembershipsAsync(cancellationToken);
                var existingLinks = await _stateStore.GetAllLinksAsync(cancellationToken);
                var seenPersonIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var person in people)
                {
                    seenPersonIds.Add(person.PersonId);

                    if (string.IsNullOrWhiteSpace(person.Email))
                    {
                        stats.PeopleSkippedMissingEmail++;
                        continue;
                    }

                    try
                    {
                        var desiredGroups = tagMemberships.TryGetValue(person.PersonId, out var groups)
                            ? groups
                            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        var user = await ResolveUserAsync(person, existingLinks, cancellationToken);
                        var currentUserId = user?.Id;

                        if (_syncOptions.DryRun)
                        {
                            if (currentUserId is null)
                            {
                                stats.UsersCreated++;
                            }
                            else
                            {
                                stats.UsersUpdated++;
                            }

                            continue;
                        }

                        if (currentUserId is null)
                        {
                            currentUserId = await _keycloakAdminClient.CreateUserAsync(person, cancellationToken);
                            stats.UsersCreated++;
                        }
                        else
                        {
                            await _keycloakAdminClient.UpdateUserAsync(currentUserId, person, cancellationToken);
                            stats.UsersUpdated++;
                        }

                        var currentGroups = await _keycloakAdminClient.GetUserGroupsAsync(currentUserId, cancellationToken);
                        var currentManagedGroups = currentGroups
                            .Where(group => managedGroupPaths.Contains(group.Path, StringComparer.OrdinalIgnoreCase))
                            .ToDictionary(group => group.Path, group => group, StringComparer.OrdinalIgnoreCase);

                        foreach (var desiredGroupPath in desiredGroups)
                        {
                            var groupId = groupIdsByPath[desiredGroupPath];
                            if (currentManagedGroups.ContainsKey(desiredGroupPath))
                            {
                                continue;
                            }

                            await _keycloakAdminClient.AddUserToGroupAsync(currentUserId, groupId, cancellationToken);
                            stats.GroupMembershipsAdded++;
                        }

                        foreach (var managedGroup in currentManagedGroups.Values)
                        {
                            if (desiredGroups.Contains(managedGroup.Path))
                            {
                                continue;
                            }

                            await _keycloakAdminClient.RemoveUserFromGroupAsync(currentUserId, managedGroup.Id, cancellationToken);
                            stats.GroupMembershipsRemoved++;
                        }

                        var link = new PersonLinkRecord(
                            person.PersonId,
                            currentUserId,
                            person.Email,
                            string.IsNullOrWhiteSpace(person.DisplayName) ? person.Email : person.DisplayName,
                            person.StateHash,
                            DateTimeOffset.UtcNow,
                            DateTimeOffset.UtcNow);

                        await _stateStore.UpsertPersonLinkAsync(link, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        var message = $"Failed to sync Action Network person {person.PersonId} ({person.Email}): {ex.Message}";
                        stats.Errors.Add(message);
                        _logger.LogError(ex, "Failed to sync Action Network person {PersonId}.", person.PersonId);
                    }
                }

                if (!_syncOptions.DryRun && _syncOptions.DisableUsersMissingFromSource)
                {
                    foreach (var link in existingLinks.Values.Where(link => !seenPersonIds.Contains(link.ActionNetworkPersonId)))
                    {
                        await _keycloakAdminClient.DisableUserAsync(link.KeycloakUserId, cancellationToken);
                        stats.UsersDisabled++;
                    }
                }

                await _stateStore.CompleteRunAsync(runId, stats, cancellationToken);
                _logger.LogInformation(
                    "Identity sync complete. Seen: {Seen}, created: {Created}, updated: {Updated}, disabled: {Disabled}, group adds: {Added}, group removals: {Removed}, errors: {Errors}.",
                    stats.PeopleSeen,
                    stats.UsersCreated,
                    stats.UsersUpdated,
                    stats.UsersDisabled,
                    stats.GroupMembershipsAdded,
                    stats.GroupMembershipsRemoved,
                    stats.Errors.Count);

                return stats.Errors.Count == 0;
            }
            catch (Exception ex)
            {
                stats.Errors.Add(ex.Message);
                await _stateStore.FailRunAsync(runId, stats, ex, cancellationToken);
                _logger.LogError(ex, "Identity sync failed.");
                return false;
            }
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<KeycloakUserSummary?> ResolveUserAsync(
        ActionNetworkPerson person,
        IReadOnlyDictionary<string, PersonLinkRecord> existingLinks,
        CancellationToken cancellationToken)
    {
        if (existingLinks.TryGetValue(person.PersonId, out var link))
        {
            var linkedUser = await _keycloakAdminClient.GetUserByIdAsync(link.KeycloakUserId, cancellationToken);
            if (linkedUser is not null)
            {
                return linkedUser;
            }
        }

        return await _keycloakAdminClient.FindUserByEmailAsync(person.Email!, cancellationToken);
    }

    private async Task<Dictionary<string, HashSet<string>>> LoadTagMembershipsAsync(CancellationToken cancellationToken)
    {
        var memberships = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in _syncOptions.TagMappings.Where(mapping =>
                     !string.IsNullOrWhiteSpace(mapping.TagId) &&
                     !string.IsNullOrWhiteSpace(mapping.KeycloakGroupPath)))
        {
            var groupPath = NormaliseGroupPath(mapping.KeycloakGroupPath);
            var personIds = await _actionNetworkClient.GetPeopleIdsForTagAsync(mapping.TagId, cancellationToken);

            foreach (var personId in personIds)
            {
                if (!memberships.TryGetValue(personId, out var groups))
                {
                    groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    memberships[personId] = groups;
                }

                groups.Add(groupPath);
            }
        }

        return memberships;
    }

    private static string NormaliseGroupPath(string groupPath)
    {
        return "/" + string.Join('/', groupPath.Split('/', StringSplitOptions.RemoveEmptyEntries));
    }
}

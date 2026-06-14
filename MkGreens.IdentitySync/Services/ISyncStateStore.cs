using MkGreens.IdentitySync.Models;

namespace MkGreens.IdentitySync.Services;

public interface ISyncStateStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<long> StartRunAsync(DateTimeOffset startedAt, CancellationToken cancellationToken);
    Task CompleteRunAsync(long runId, SyncRunStats stats, CancellationToken cancellationToken);
    Task FailRunAsync(long runId, SyncRunStats stats, Exception exception, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, PersonLinkRecord>> GetAllLinksAsync(CancellationToken cancellationToken);
    Task UpsertPersonLinkAsync(PersonLinkRecord link, CancellationToken cancellationToken);
}

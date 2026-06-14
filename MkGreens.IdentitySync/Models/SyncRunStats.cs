namespace MkGreens.IdentitySync.Models;

public sealed class SyncRunStats
{
    public int PeopleSeen { get; set; }
    public int PeopleSkippedMissingEmail { get; set; }
    public int UsersCreated { get; set; }
    public int UsersUpdated { get; set; }
    public int UsersDisabled { get; set; }
    public int GroupsCreated { get; set; }
    public int GroupMembershipsAdded { get; set; }
    public int GroupMembershipsRemoved { get; set; }
    public List<string> Errors { get; } = [];

    public string ErrorSummary => Errors.Count == 0 ? string.Empty : string.Join(Environment.NewLine, Errors);
}

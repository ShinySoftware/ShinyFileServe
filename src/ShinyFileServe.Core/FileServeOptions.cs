namespace ShinyFileServe.Core;

public class FileServeOptions
{
    /// <summary>Full path to the SQLite file holding users, roles, folders, quotas and the activity log.</summary>
    public string DatabasePath { get; set; } = "fileserve.db";

    /// <summary>Activity entries older than this are pruned at startup. Null keeps everything.</summary>
    public TimeSpan? ActivityRetention { get; set; } = TimeSpan.FromDays(365);
}

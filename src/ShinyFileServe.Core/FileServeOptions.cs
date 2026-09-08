namespace ShinyFileServe.Core;

public class FileServeOptions
{
    /// <summary>Full path to the SQLite file holding users, roles, folders, quotas and the activity log.</summary>
    public string DatabasePath { get; set; } = "fileserve.db";

    /// <summary>Activity entries older than this are pruned at startup. Null keeps everything.</summary>
    public TimeSpan? ActivityRetention { get; set; } = TimeSpan.FromDays(365);

    /// <summary>
    /// Bandwidth rows older than this are pruned at startup. Null keeps everything. Longer than the
    /// log by default because it is smaller by orders of magnitude - one row an hour per account -
    /// and because "how much did we move last year" is a question with a longer memory than "who
    /// deleted that file".
    /// </summary>
    public TimeSpan? BandwidthRetention { get; set; } = TimeSpan.FromDays(730);
}

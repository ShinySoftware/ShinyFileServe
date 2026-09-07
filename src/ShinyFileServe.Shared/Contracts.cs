namespace ShinyFileServe.Shared;

// ---------------------------------------------------------------------------
// Auth
// ---------------------------------------------------------------------------

public record LoginRequest(string Username, string Password, bool RememberMe = false);

/// <summary>
/// What the sign-in page and the running session need to know about session rules. Served without
/// authentication, because the sign-in page has to render the "keep me signed in" box - or not -
/// before anyone has signed in.
/// </summary>
public record SessionPolicy(
    /// <summary>
    /// True until the first administrator exists. The frontend sends the visitor to setup rather
    /// than sign-in, because there is nothing yet to sign in to.
    /// </summary>
    bool NeedsSetup,

    bool AllowRememberMe,
    int RememberMeDays,

    /// <summary>Minutes of inactivity before the browser signs itself out. 0 turns it off.</summary>
    int IdleLogoffMinutes
);

public record CurrentUser(
    string UserId,
    string Username,
    string DisplayName,
    bool IsAdmin,
    IReadOnlyList<string> Roles,

    /// <summary>
    /// True when this session was granted because the request came from the machine running the
    /// server. Signing out of one is meaningless - the next request signs straight back in - so the
    /// frontend hides the button rather than offering something that does nothing.
    /// </summary>
    bool IsLocalSession = false
);

/// <summary>Creates the first administrator. Only accepted while <c>NeedsSetup</c> is true.</summary>
public record SetupRequest(string Username, string Password, string DisplayName);

// ---------------------------------------------------------------------------
// Quotas
// ---------------------------------------------------------------------------

/// <summary>
/// A quota as the frontend needs to render it: the cap, how much of it is spent, and when it rolls over.
/// A null <see cref="LimitBytes"/> means unlimited and the UI should not draw a meter.
/// </summary>
public record QuotaState(
    QuotaWindow Window,
    long? LimitBytes,
    long UsedBytes,
    DateTimeOffset? ResetsUtc
)
{
    public bool IsUnlimited => this.LimitBytes is null;
    public long RemainingBytes => this.LimitBytes is null ? long.MaxValue : Math.Max(0, this.LimitBytes.Value - this.UsedBytes);
    public double Fraction => this.LimitBytes is null or 0 ? 0 : Math.Clamp((double)this.UsedBytes / this.LimitBytes.Value, 0, 1);
}

// ---------------------------------------------------------------------------
// Folders & files
// ---------------------------------------------------------------------------

/// <summary>A share folder as seen by one particular user, with their effective rights folded in.</summary>
public record FolderInfo(
    string Id,
    string Name,
    string? Description,
    FolderPermissions Permissions,
    QuotaState Download,
    QuotaState Upload,
    long? MaxUploadFileSizeBytes
);

public record FileEntryInfo(
    string Name,
    string Path,
    bool IsDirectory,
    long Size,
    DateTimeOffset ModifiedUtc,

    /// <summary>A friendly name, when an administrator has given this path one.</summary>
    string? DisplayName = null,

    /// <summary>A note explaining what this file or folder is for.</summary>
    string? Description = null
)
{
    /// <summary>What to put on screen: the alias when there is one, otherwise the name on disk.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Label => this.DisplayName ?? this.Name;

    /// <summary>Derived, so it is not put on the wire - a big listing repeats it once per row.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasAlias => this.DisplayName is { Length: > 0 } && this.DisplayName != this.Name;
}

/// <summary>
/// Names or annotates one path inside a share - a file or a folder. Blank name and note together
/// remove the annotation, so there is no separate delete to get wrong.
/// </summary>
public record SetAnnotationRequest(string FolderId, string Path, string? DisplayName, string? Description);

public record DirectoryListing(
    string FolderId,
    string Path,
    IReadOnlyList<FileEntryInfo> Entries
);

public record CreateDirectoryRequest(string FolderId, string Path, string Name);

public record DeleteRequest(string FolderId, string Path);

public record OperationResult(bool Success, string? Error = null)
{
    public static OperationResult Ok() => new(true);
    public static OperationResult Fail(string error) => new(false, error);
}

// ---------------------------------------------------------------------------
// Activity log
// ---------------------------------------------------------------------------

public enum ActivityAction
{
    Login,
    LoginFailed,
    Logout,
    List,
    Download,
    Upload,
    Delete,
    CreateDirectory,
    Denied
}

public record ActivityEntryInfo(
    string Id,
    DateTimeOffset TimestampUtc,
    string? UserId,
    string Username,
    ActivityAction Action,
    string? FolderId,
    string? FolderName,
    string? Path,
    long Bytes,
    bool Success,
    string? Detail,
    string? IpAddress
);

public record PagedResponse<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

/// <summary>Just enough of a user to name them in a filter. No credentials, ever.</summary>
public record UserSummary(string Id, string Username, string DisplayName, bool IsActive, bool IsAdmin, bool IsProtected);

public record ActivityQuery(
    string? UserId = null,
    string? FolderId = null,
    ActivityAction? Action = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    int Page = 1,
    int PageSize = 50
);

// ---------------------------------------------------------------------------
// Administration (server, tunnel, settings) - all admin-only
// ---------------------------------------------------------------------------

public enum TunnelStatus
{
    Off,
    Connecting,
    Open,
    Reconnecting,
    Failed
}

/// <summary>Everything the settings screen renders, in one round trip.</summary>
public record ServerStatus(
    bool IsServing,
    string StateText,
    IReadOnlyList<string> LocalUrls,
    int ActiveConnections,
    TunnelStatus Tunnel,
    string? TunnelUrl,
    string? TunnelError,
    ServerConfig Config
)
{
    /// <summary>The address to hand out: the tunnel when it is up, otherwise the LAN address.</summary>
    public string? PreferredUrl => this.TunnelUrl ?? this.LocalUrls.FirstOrDefault();
}

public record ServerConfig(
    int Port,
    bool AutoStartServer,
    bool AutoStartTunnel,

    /// <summary>Never carries the token itself - only whether one is stored.</summary>
    bool HasPinggyToken,

    bool NotificationsEnabled,
    IReadOnlyList<ActivityAction> NotifyOn,
    bool AllowRememberMe,
    int RememberMeDays,
    int IdleLogoffMinutes
);

/// <summary>
/// A settings write. A null <see cref="PinggyToken"/> leaves the stored token alone; an empty
/// string clears it. That distinction is why it is nullable rather than defaulted.
/// </summary>
public record UpdateServerConfig(
    int Port,
    bool AutoStartServer,
    bool AutoStartTunnel,
    string? PinggyToken,
    bool NotificationsEnabled = false,

    /// <summary>Null leaves the stored selection alone, the same way a null token does.</summary>
    IReadOnlyList<ActivityAction>? NotifyOn = null,

    bool AllowRememberMe = true,
    int RememberMeDays = 30,
    int IdleLogoffMinutes = 0
);

public static class ActivityActions
{
    /// <summary>The actions worth offering as notification triggers, in the order a person reads them.</summary>
    public static IReadOnlyList<ActivityAction> Notifiable { get; } =
    [
        ActivityAction.Login,
        ActivityAction.LoginFailed,
        ActivityAction.Logout,
        ActivityAction.Download,
        ActivityAction.Upload,
        ActivityAction.Delete,
        ActivityAction.CreateDirectory,
        ActivityAction.Denied
    ];

    public static string ToDisplay(this ActivityAction action) => action switch
    {
        ActivityAction.Login => "Someone signs in",
        ActivityAction.LoginFailed => "A sign-in is refused",
        ActivityAction.Logout => "Someone signs out",
        ActivityAction.List => "A folder is opened",
        ActivityAction.Download => "A file is downloaded",
        ActivityAction.Upload => "A file is uploaded",
        ActivityAction.Delete => "A file is deleted",
        ActivityAction.CreateDirectory => "A folder is created",
        ActivityAction.Denied => "An action is refused",
        _ => action.ToString()
    };
}


// ---------------------------------------------------------------------------
// Administration: users, groups and shares
// ---------------------------------------------------------------------------

public record UserDetail(
    string Id,
    string Username,
    string DisplayName,
    string? Email,
    bool IsAdmin,
    bool IsActive,

    /// <summary>The account created at setup. Renameable and re-passwordable, nothing else.</summary>
    bool IsProtected,

    IReadOnlyList<string> RoleIds,
    IReadOnlyList<string> RoleNames,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastLoginUtc
);

/// <summary>A blank <see cref="Password"/> keeps the stored one. Required when <see cref="Id"/> is null.</summary>
public record SaveUserRequest(
    string? Id,
    string Username,
    string DisplayName,
    string? Email,
    string? Password,
    bool IsAdmin,
    bool IsActive,
    IReadOnlyList<string> RoleIds
);

public record RoleInfo(
    string Id,
    string Name,
    string? Description,
    bool IsAdmin,
    bool IsProtected,
    int MemberCount
);

public record SaveRoleRequest(string? Id, string Name, string? Description, bool IsAdmin);

public record GrantInfo(
    string RoleId,
    string RoleName,
    FolderPermissions Permissions,
    QuotaWindow DownloadWindow,
    long? DownloadBytes,
    QuotaWindow UploadWindow,
    long? UploadBytes,
    long? MaxUploadFileSizeBytes
);

public record ShareInfo(
    string Id,
    string Name,
    string PhysicalPath,
    string? Description,
    bool IsEnabled,

    /// <summary>Whether the directory is actually there. A share pointing at nothing is a common mistake.</summary>
    bool PathExists,

    int GrantCount
);

public record ShareDetail(ShareInfo Share, IReadOnlyList<GrantInfo> Grants);

public record SaveShareRequest(
    string? Id,
    string Name,
    string PhysicalPath,
    string? Description,
    bool IsEnabled,

    /// <summary>Create the directory if it is not there yet, rather than refusing the save.</summary>
    bool CreateDirectory,

    IReadOnlyList<GrantInfo> Grants
);

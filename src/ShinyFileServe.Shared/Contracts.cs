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

    /// <summary>
    /// Whether setup can be completed from this connection. Only the machine running the server
    /// can claim it, so a visitor over the network is told where to go rather than shown a form
    /// the server is going to refuse.
    /// </summary>
    bool CanCompleteSetup,

    bool AllowRememberMe,
    int RememberMeDays,

    /// <summary>Minutes of inactivity before the browser signs itself out. 0 turns it off.</summary>
    int IdleLogoffMinutes,

    /// <summary>What a password has to look like here. Sent unauthenticated so the setup and
    /// sign-in pages can show the rules while somebody is typing.</summary>
    PasswordPolicyInfo Passwords,

    /// <summary>
    /// The accounts anybody may sign in as without a password. The sign-in page has to render a
    /// button per account before anyone is signed in, so it arrives with the rest of the policy.
    /// Only anonymous accounts are ever listed here - this is served to the whole network.
    /// </summary>
    IReadOnlyList<AnonymousAccount> AnonymousAccounts
);

/// <summary>
/// An account with no password, named on the sign-in page. Carries nothing but what a button needs -
/// this list is public, so anything else on it would be a leak.
/// </summary>
public record AnonymousAccount(string Id, string DisplayName);

/// <summary>Signs in as an anonymous account. There is no password to send.</summary>
public record AnonymousSignInRequest(string UserId);

/// <summary>
/// The password rules, in the shape the strength meter wants. Server-enforced regardless - this is
/// what the browser shows, not what decides.
/// </summary>
public record PasswordPolicyInfo(
    bool Enabled,
    int MinimumLength,
    bool RequireUppercase,
    bool RequireLowercase,
    bool RequireNumber,
    bool RequireSpecialCharacter,
    bool BlockCommonPasswords,

    /// <summary>
    /// How many days a password lasts before it has to be replaced. 0 never expires one. Sent with
    /// the rest of the rules because it is a fact about passwords here rather than about anybody's
    /// account, and the sign-in page may as well be able to say so.
    /// </summary>
    int MaximumAgeDays = 0
)
{
    public static PasswordPolicyInfo Off { get; } = new(false, 1, false, false, false, false, false);
}

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
    bool IsLocalSession = false,

    /// <summary>
    /// The password has outlived the server's maximum age. The session is real, but the API refuses
    /// everything except changing the password and signing out, so the shell puts the change-password
    /// screen up and leaves it there.
    /// </summary>
    bool MustChangePassword = false
);

/// <summary>
/// Somebody changing their own password. There is no account on it: the server acts on whoever the
/// cookie belongs to and nobody else, so there is nothing here to point at another account.
/// </summary>
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>What a person may see about their own account, and what the password box needs to judge one.</summary>
public record AccountProfile(
    string UserId,
    string Username,
    string DisplayName,
    IReadOnlyList<string> Roles,

    /// <summary>False for an account that signs in without a password - there is nothing to change.</summary>
    bool CanChangePassword,

    /// <summary>When the password was last set. Null when the account has none.</summary>
    DateTimeOffset? PasswordSetUtc,

    /// <summary>When it stops being accepted, or null when this server expires nothing.</summary>
    DateTimeOffset? PasswordExpiresUtc,

    bool MustChangePassword,

    /// <summary>The rules, so the strength meter here reads the same ones every other screen does.</summary>
    PasswordPolicyInfo Passwords
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

/// <summary>
/// What a staged backup turns out to contain. <see cref="IsValid"/> false means the restore button
/// stays off and <see cref="Problem"/> says why.
/// </summary>
public record BackupSummary(
    bool IsValid,
    string? Problem,
    int Users,
    int Administrators,
    int Shares,
    int ActivityEntries,
    long SizeBytes,

    /// <summary>When the settings in that backup were last written - the closest thing to its age.</summary>
    DateTimeOffset? TakenUtc
);

/// <summary>
/// A folder on the machine running the server, offered to an administrator who is choosing one to
/// publish. Nothing about the files inside it is reported - a picker needs the shape of the tree,
/// not its contents.
/// </summary>
public record MachineFolder(
    string Path,
    string Name,

    /// <summary>Whether it holds sub-folders. A folder the server cannot read reports false rather than failing the whole listing.</summary>
    bool HasChildren,

    /// <summary>Whether a share already points at exactly this folder. Publishing one twice is allowed but seldom meant.</summary>
    bool IsShared
);

/// <summary>
/// One level of the server machine's folders. An empty <see cref="Path"/> is the starting list -
/// home, the usual places and the volumes - which is a set of starting points rather than a
/// directory, and so has no parent.
/// </summary>
public record MachineFolderListing(
    string Path,
    string? Parent,
    IReadOnlyList<MachineFolder> Folders
);

/// <summary>Makes one new folder directly inside <see cref="Parent"/>. The name is a single segment.</summary>
public record CreateMachineFolderRequest(string Parent, string Name);

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
    string? IpAddress,

    /// <summary>
    /// The name of the machine at <see cref="IpAddress"/>, when the network will say. Filled in as
    /// the log is read rather than as it is written, so it is absent whenever the lookup failed, ran
    /// out of time, or was never asked - never stale, and never worth waiting for.
    /// </summary>
    string? HostName = null
);

public record PagedResponse<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

/// <summary>Just enough of a user to name them in a filter. No credentials, ever.</summary>
public record UserSummary(string Id, string Username, string DisplayName, DateTimeOffset? ExpiresAtUtc, bool IsAdmin, bool IsProtected);

public record ActivityQuery(
    string? UserId = null,
    string? FolderId = null,
    ActivityAction? Action = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    int Page = 1,
    int PageSize = 50,

    /// <summary>
    /// Which column orders the result: when, who, what, address or size. Sorted on the server
    /// because the page is fetched from the server - a column that reordered the sixty rows in
    /// hand would be claiming to have sorted forty thousand.
    /// </summary>
    ActivitySort SortBy = ActivitySort.When,

    bool Descending = true
);

/// <summary>The columns of the log worth ordering by. Anything else is a detail, not a key.</summary>
public enum ActivitySort
{
    When,
    Who,
    What,
    Address,
    Size
}

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
    int IdleLogoffMinutes,
    PasswordPolicyInfo Passwords,

    /// <summary>Consecutive failed sign-ins before an account locks itself. 0 never locks one.</summary>
    int MaxFailedSignIns
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
    int IdleLogoffMinutes = 0,

    /// <summary>Null leaves the stored rules alone.</summary>
    PasswordPolicyInfo? Passwords = null,

    /// <summary>
    /// Consecutive failed sign-ins before an account locks itself, or null to leave the stored
    /// number alone. 0 is a real value here and means "never lock anybody".
    /// </summary>
    int? MaxFailedSignIns = null
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

    /// <summary>When the account stops working. Null means it never does.</summary>
    DateTimeOffset? ExpiresAtUtc,

    /// <summary>The account created at setup. Renameable and re-passwordable, nothing else.</summary>
    bool IsProtected,

    /// <summary>No password, and anybody who reaches the server may sign in as it.</summary>
    bool IsAnonymous,

    /// <summary>The cap on downloads running at once, and how many are running right now.</summary>
    TransferCapState Downloads,
    TransferCapState Uploads,

    IReadOnlyList<string> RoleIds,
    IReadOnlyList<string> RoleNames,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastLoginUtc,

    /// <summary>When the account locked itself after too many failed sign-ins. Null when it has not.</summary>
    DateTimeOffset? LockedOutUtc = null,

    /// <summary>
    /// The run of failed sign-ins behind that lock, or the run so far on an account still open.
    /// Shown beside the lock so an administrator can tell a fat-fingered person from an attack.
    /// </summary>
    int FailedSignInCount = 0
)
{
    /// <summary>Whether the expiry has already passed. Derived, so the list and the editor agree.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsExpired => this.ExpiresAtUtc is { } expiry && expiry <= DateTimeOffset.UtcNow;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsLockedOut => this.LockedOutUtc is not null;
}

/// <summary>
/// A cap on how many transfers an account may have running at the same time, and how many it has
/// running right now. A null <see cref="Limit"/> means no cap.
/// </summary>
public record TransferCapState(int? Limit, int Active)
{
    public static TransferCapState Uncapped { get; } = new(null, 0);

    public bool IsUncapped => this.Limit is null;

    /// <summary>How many more this account could start this instant. Meaningless when uncapped.</summary>
    public int Available => this.Limit is null ? Int32.MaxValue : Math.Max(0, this.Limit.Value - this.Active);
}

/// <summary>A blank <see cref="Password"/> keeps the stored one. Required when <see cref="Id"/> is null.</summary>
public record SaveUserRequest(
    string? Id,
    string Username,
    string DisplayName,
    string? Email,
    string? Password,
    bool IsAdmin,

    /// <summary>Null leaves the account with no expiry at all.</summary>
    DateTimeOffset? ExpiresAtUtc,

    /// <summary>
    /// Turning this on clears any stored password; sending a password turns it back off. The two are
    /// mutually exclusive by definition, so the server settles it rather than trusting the order.
    /// </summary>
    bool IsAnonymous,

    /// <summary>How many downloads this account may run at once. Null, or anything below one, is no cap.</summary>
    int? MaxParallelDownloads,
    int? MaxParallelUploads,

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

// ---------------------------------------------------------------------------
// Administration: what is happening right now - all admin-only
// ---------------------------------------------------------------------------

public enum TransferDirection
{
    Download,
    Upload
}

public enum TransferOutcome
{
    /// <summary>Every byte arrived.</summary>
    Completed,

    /// <summary>The server refused it or the disk did.</summary>
    Failed,

    /// <summary>The other end stopped listening part way through. Common, and not an error.</summary>
    Abandoned
}

/// <summary>
/// One account that has been seen inside the tracking window. Accounts, not connections - a browser
/// opens and drops several sockets a minute and none of them mean anything on their own.
/// </summary>
public record LiveSessionInfo(
    string UserId,
    string Username,
    string DisplayName,

    /// <summary>Where the last request came from. Null when the connection had no address to report.</summary>
    string? Address,

    /// <summary>First seen in this stretch of activity, not when the cookie was issued.</summary>
    DateTimeOffset SignedInUtc,

    DateTimeOffset LastSeenUtc,

    /// <summary>The window on the machine running the server. It is always there, so it is never a visitor.</summary>
    bool IsLocalSession
);

/// <summary>A transfer that is still moving. <see cref="TotalBytes"/> is null when nobody declared one.</summary>
public record LiveTransferInfo(
    string Id,
    string UserId,
    string Username,
    string DisplayName,
    string FolderId,
    string FolderName,
    string Path,
    TransferDirection Direction,
    long Bytes,
    long? TotalBytes,
    DateTimeOffset StartedUtc,

    /// <summary>Measured over the last few seconds, so a stall reads as slow rather than as the long-run average.</summary>
    double BytesPerSecond
)
{
    /// <summary>How far along, 0 to 1, or null when the size is unknown and a bar would be a guess.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double? Fraction => this.TotalBytes is null or <= 0
        ? null
        : Math.Clamp((double)this.Bytes / this.TotalBytes.Value, 0, 1);

    /// <summary>Null when there is no total to run out of, or nothing is moving to run out of it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan? Remaining
    {
        get
        {
            if (this.TotalBytes is not { } total || this.BytesPerSecond < 1)
                return null;

            var left = total - this.Bytes;
            return left <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(left / this.BytesPerSecond);
        }
    }
}

/// <summary>A transfer that is over, however it ended.</summary>
public record FinishedTransferInfo(
    string Id,
    string UserId,
    string Username,
    string DisplayName,
    string FolderId,
    string FolderName,
    string Path,
    TransferDirection Direction,

    /// <summary>What actually moved. Less than <see cref="TotalBytes"/> when it was cut short.</summary>
    long Bytes,

    long? TotalBytes,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    TransferOutcome Outcome,

    /// <summary>Why it ended badly, when there is something worth saying.</summary>
    string? Problem
)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan Duration => this.FinishedUtc > this.StartedUtc ? this.FinishedUtc - this.StartedUtc : TimeSpan.Zero;

    [System.Text.Json.Serialization.JsonIgnore]
    public double AverageBytesPerSecond
    {
        get
        {
            var seconds = this.Duration.TotalSeconds;
            return seconds <= 0 ? 0 : this.Bytes / seconds;
        }
    }
}

/// <summary>
/// Everything the live screen draws, in one round trip. Small enough to re-fetch every second,
/// which is why it is one shape rather than three endpoints.
/// </summary>
public record LiveActivitySnapshot(
    IReadOnlyList<LiveSessionInfo> Sessions,
    IReadOnlyList<LiveTransferInfo> Transfers,
    IReadOnlyList<FinishedTransferInfo> Finished
);

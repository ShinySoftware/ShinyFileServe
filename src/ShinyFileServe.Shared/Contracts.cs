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
    bool MustChangePassword = false,

    /// <summary>Signed in as an account with no password. Such an account cannot make share links.</summary>
    bool IsAnonymous = false
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
    PasswordPolicyInfo Passwords,

    /// <summary>
    /// This account's own ceiling on bytes across every share, and what it has spent this period.
    /// Unlimited on most accounts. Shown here rather than on the share bar because it is a fact
    /// about the person, not about where they happen to be standing.
    /// </summary>
    QuotaState? AccountDownload = null,
    QuotaState? AccountUpload = null,

    /// <summary>What this account may do with the package feeds, and whether they are switched on.</summary>
    PackageAccessInfo? Packages = null
);

// ---------------------------------------------------------------------------
// Package feeds
// ---------------------------------------------------------------------------

/// <summary>
/// The feeds as one account sees them. The paths are relative to the server - the browser knows
/// which address it reached the server by, and that is the one to hand out.
/// </summary>
public record PackageAccessInfo(
    bool NuGetEnabled,
    bool NpmEnabled,

    /// <summary>What this account may do, folded across its groups.</summary>
    PackagePermissions Permissions,

    /// <summary>What anybody may do with no token at all.</summary>
    PackagePermissions Anonymous,

    /// <summary>Whether this account may hold tokens. An account anybody can sign in as may not.</summary>
    bool CanCreateTokens
)
{
    public const string NuGetPath = "nuget/v3/index.json";
    public const string NpmPath = "npm/";
}

/// <summary>
/// A personal access token, as its owner sees it after the fact. The secret itself is shown once,
/// in <see cref="CreatedAccessToken"/>, and never again - the server keeps only a hash.
/// </summary>
public record AccessTokenInfo(
    string Id,
    string Name,

    /// <summary>The last few characters, so a person can tell which token is in which config file.</summary>
    string Hint,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastUsedUtc,
    DateTimeOffset? ExpiresUtc
);

/// <summary>A new token. <see cref="ExpiresInDays"/> null or 0 never expires it.</summary>
public record CreateAccessTokenRequest(string Name, int? ExpiresInDays = null);

/// <summary>The one response that carries a token's secret.</summary>
public record CreatedAccessToken(AccessTokenInfo Token, string Secret);

/// <summary>
/// A token as an administrator sees it: whose it is as well as what it is. Never the secret - the
/// server does not have it.
/// </summary>
public record AdminAccessTokenInfo(
    AccessTokenInfo Token,
    string UserId,
    string Username,
    string DisplayName,

    /// <summary>What the token's owner may do with the feeds right now - which is what the token may do.</summary>
    PackagePermissions Packages
);

/// <summary>One request to a feed, as the traffic log keeps it.</summary>
public record PackageRequestInfo(
    string Id,
    DateTimeOffset TimestampUtc,
    PackageFeedKind Feed,
    PackageOperation Operation,
    string Method,
    string Path,

    /// <summary>The package the request was about, when the path names one.</summary>
    string? PackageId,
    string? Version,
    int StatusCode,
    long BytesIn,
    long BytesOut,
    long DurationMs,

    /// <summary>Null for a request that carried no token, or one that was refused.</summary>
    string? UserId,
    string? Username,
    string? TokenId,
    string? TokenName,
    string? IpAddress,
    string? UserAgent,
    string? HostName = null
)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Succeeded => this.StatusCode is >= 200 and < 400;
}

public record PackageTrafficQuery(
    string? UserId = null,
    string? TokenId = null,
    PackageFeedKind? Feed = null,
    PackageOperation? Operation = null,

    /// <summary>One package id, ignoring case.</summary>
    string? PackageId = null,

    /// <summary>Only refused requests - 401 and 403 - when true.</summary>
    bool RefusedOnly = false,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    int Page = 1,
    int PageSize = 50
);

/// <summary>How the feeds are run. Part of the settings screen.</summary>
public record PackageFeedConfig(
    bool NuGetEnabled,
    bool NpmEnabled,

    /// <summary>What a request with no token may do. Only <see cref="PackagePermissionsExtensions.Reads"/> are accepted.</summary>
    PackagePermissions Anonymous,

    /// <summary>
    /// Answer from registry.npmjs.org for anything not published here, so one registry address
    /// serves both. Packages published here always win over a public one of the same name.
    /// </summary>
    bool NpmUseUpstream,

    /// <summary>Allow <c>npm unpublish</c> on packages the caller maintains.</summary>
    bool NpmAllowUnpublish,

    /// <summary>Make <c>dotnet nuget delete</c> remove a version for good, rather than unlist it.</summary>
    bool NuGetPermanentDelete
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
    /// <summary>No ceiling at all. The shape every unmetered quota takes, so callers do not each invent one.</summary>
    public static QuotaState Unlimited { get; } = new(QuotaWindow.None, null, 0, null);

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
    Denied,

    /// <summary>A NuGet push, or an npm publish or package update.</summary>
    PackagePublish,

    /// <summary>A NuGet unlist, relist or delete, or an npm unpublish.</summary>
    PackageRemove,

    /// <summary>Somebody made a share link to a file or a folder.</summary>
    ShareLinkCreated,

    /// <summary>A share link was taken back, by whoever made it or by an administrator.</summary>
    ShareLinkRevoked
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

/// <summary>
/// Whether the machine starts this app at login, and if not, why not. Mirrors Shiny's
/// <c>AppStartupState</c> member for member, the same way <see cref="TunnelStatus"/> mirrors the
/// tunnel's own enum: the contract stays free of Shiny.Core and the server casts across.
/// </summary>
/// <remarks>
/// There is deliberately no copy of this in the settings document. The registry value or the file on
/// disk <i>is</i> the setting - a person can delete it from Task Manager, from GNOME Tweaks, or with
/// <c>rm</c>, and a remembered flag would be a second answer that disagrees with the machine and
/// wins the argument it should lose.
/// </remarks>
public enum RunAtStartupState
{
    /// <summary>No such concept here, or the app cannot work out where it is installed.</summary>
    NotSupported,

    /// <summary>Supported, and switched off.</summary>
    NotRegistered,

    /// <summary>Registered, and the machine will honour it at the next sign-in.</summary>
    Enabled,

    /// <summary>Registered, then switched off from outside this app - Task Manager, or an autostart editor.</summary>
    DisabledByUser,

    /// <summary>Registered, and blocked by a policy on this machine that this app cannot overrule.</summary>
    DisabledByPolicy,

    /// <summary>Registered, and waiting for the person to allow it in the operating system's own settings.</summary>
    RequiresApproval
}

/// <summary>
/// One sentence for each answer, kept beside the contract rather than at either end of it: the
/// browser needs it for the hint under the switch and the server needs it for the message when a
/// change did not take, and the two must not drift into telling different stories.
/// </summary>
public static class RunAtStartupText
{
    public static string Explain(RunAtStartupState state) => state switch
    {
        RunAtStartupState.Enabled =>
            "This machine starts the app when you sign in. The entry belongs to your account, not to the machine, and you can remove it from the operating system's own startup list without coming back here.",

        RunAtStartupState.NotRegistered =>
            "Registers with this machine's own startup list, under the account running the server - the Run key on Windows, a login item on macOS, an autostart entry on Linux.",

        RunAtStartupState.RequiresApproval =>
            "The app is registered, but the operating system is waiting for you to allow it - on macOS that is System Settings > General > Login Items. Until you do, it will not start at sign-in.",

        RunAtStartupState.DisabledByUser =>
            "The entry is there but switched off from outside this app - Task Manager's Startup tab on Windows, an autostart editor on Linux. Turn it back on where it was turned off.",

        RunAtStartupState.DisabledByPolicy =>
            "A policy on this machine blocks apps from starting at sign-in. That is an administrator's decision and this app cannot overrule it.",

        _ => "This machine has no way to start an app at login."
    };
}

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
    int MaxFailedSignIns,

    /// <summary>
    /// What this machine says about starting the app when its user signs in - asked of the
    /// operating system on every look, never read back from the settings document. Anything but
    /// <see cref="RunAtStartupState.Enabled"/> means it will not start, and each of the other
    /// answers is a different thing for the operator to do about it.
    /// </summary>
    RunAtStartupState RunAtStartup = RunAtStartupState.NotSupported,

    PackageFeedConfig? Packages = null
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
    int? MaxFailedSignIns = null,

    /// <summary>
    /// Start the app when this machine's user signs in, or null to leave it as it is. Nullable
    /// because it is the one setting on this record that is not stored with the others - a caller
    /// that predates it, or one that has no opinion, must not switch it off by omission.
    /// </summary>
    bool? RunAtStartup = null,

    /// <summary>Null leaves the feed settings alone.</summary>
    PackageFeedConfig? Packages = null
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
        ActivityAction.Denied,
        ActivityAction.PackagePublish,
        ActivityAction.PackageRemove,
        ActivityAction.ShareLinkCreated
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
        ActivityAction.PackagePublish => "A package is published",
        ActivityAction.PackageRemove => "A package is unlisted or removed",
        ActivityAction.ShareLinkCreated => "A share link is made",
        ActivityAction.ShareLinkRevoked => "A share link is revoked",
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

    /// <summary>
    /// The account-wide byte quotas, with what has been spent against them this period. Unlimited
    /// when the account has none, which is the ordinary case - these are a ceiling on the person
    /// across every share, beside the per-share quota each group grant carries.
    /// </summary>
    QuotaState AccountDownload,
    QuotaState AccountUpload,

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
    int FailedSignInCount = 0,

    /// <summary>What this account may do with the feeds once its groups and its own overrides are folded.</summary>
    PackagePermissions Packages = PackagePermissions.None,

    /// <summary>The account's own say over each feed. <see cref="PackageAccessLevel.Inherit"/> leaves it to the groups.</summary>
    PackageAccessLevel NuGetAccess = PackageAccessLevel.Inherit,
    PackageAccessLevel NpmAccess = PackageAccessLevel.Inherit,

    /// <summary>What the groups alone would give, so the editor can say what "from groups" currently means.</summary>
    PackagePermissions GroupPackages = PackagePermissions.None,

    /// <summary>How many access tokens the account holds, expired ones included.</summary>
    int TokenCount = 0
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

    IReadOnlyList<string> RoleIds,

    /// <summary>
    /// The account-wide byte ceilings. A window of <see cref="QuotaWindow.None"/>, or a byte count
    /// that is null or not positive, leaves that direction unmetered - the server settles the two
    /// against each other rather than storing a window with nothing under it.
    /// </summary>
    QuotaWindow DownloadQuotaWindow = QuotaWindow.None,
    long? DownloadQuotaBytes = null,
    QuotaWindow UploadQuotaWindow = QuotaWindow.None,
    long? UploadQuotaBytes = null,

    /// <summary>The account's own NuGet and npm access. Null leaves what is stored alone.</summary>
    PackageAccessLevel? NuGetAccess = null,
    PackageAccessLevel? NpmAccess = null
);

public record RoleInfo(
    string Id,
    string Name,
    string? Description,
    bool IsAdmin,
    bool IsProtected,
    int MemberCount,
    PackagePermissions Packages = PackagePermissions.None
);

/// <summary><see cref="Packages"/> null leaves the group's package rights as they are.</summary>
public record SaveRoleRequest(string? Id, string Name, string? Description, bool IsAdmin, PackagePermissions? Packages = null);

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
    IReadOnlyList<FinishedTransferInfo> Finished,

    /// <summary>
    /// Every byte through the listener, not only the ones belonging to a file. A page of JSON and a
    /// browser fetching the app itself are bandwidth too, and a rate that left them out would
    /// disagree with whatever the operator is measuring the link with.
    /// </summary>
    BandwidthNow Bandwidth
);

// ---------------------------------------------------------------------------
// Administration: bandwidth - all admin-only
// ---------------------------------------------------------------------------

/// <summary>Accounts that are not accounts.</summary>
public static class BandwidthUsers
{
    /// <summary>
    /// Traffic with nobody signed in behind it - the application itself, the sign-in page, the 401
    /// that sent somebody there. Real bandwidth, so it is counted; not a person, so it is one row.
    /// </summary>
    public const string Anonymous = "-";

    public const string AnonymousDisplayName = "Not signed in";
}

/// <summary>One second of the server's throughput, for drawing a line rather than reading a number.</summary>
public record BandwidthSample(DateTimeOffset AtUtc, double InBytesPerSecond, double OutBytesPerSecond);

/// <summary>
/// What is going through the server right now. In is what clients sent it, out is what it sent
/// back - measured on the bodies that actually crossed the socket rather than on what any header
/// claimed, so a client that hung up half way through a download is counted for the half it took.
/// </summary>
public record BandwidthNow(
    double InBytesPerSecond,
    double OutBytesPerSecond,

    /// <summary>Since the app started. The ledger is what remembers across restarts; this does not.</summary>
    long BytesInSinceStart,
    long BytesOutSinceStart,
    DateTimeOffset SinceUtc,

    /// <summary>One sample a second, oldest first. Long enough to see a burst, short enough to send every second.</summary>
    IReadOnlyList<BandwidthSample> History
)
{
    public static BandwidthNow Empty(DateTimeOffset sinceUtc) => new(0, 0, 0, 0, sinceUtc, []);
}

/// <summary>How the report's rows are cut up. A day of traffic is read by the hour; a year, by the day.</summary>
public enum BandwidthBucket
{
    Hour,
    Day
}

/// <summary>
/// What a report asks for. Both ends of the window are optional - no window at all is everything
/// that has been kept - and a null user is every account plus whatever moved before anyone signed in.
/// </summary>
public record BandwidthQuery(
    string? UserId = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    BandwidthBucket Bucket = BandwidthBucket.Day
);

/// <summary>One row of the chart: a period, and what moved during it.</summary>
public record BandwidthPeriod(DateTimeOffset StartUtc, long BytesIn, long BytesOut)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public long Total => this.BytesIn + this.BytesOut;
}

/// <summary>One account's share of the window. Present even when the report was filtered to one person.</summary>
public record BandwidthByUser(string UserId, string Username, string DisplayName, long BytesIn, long BytesOut)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public long Total => this.BytesIn + this.BytesOut;
}

/// <summary>
/// Bandwidth over a window, totalled and cut two ways: by period, which is the shape of the traffic,
/// and by account, which is who caused it.
/// <para>
/// Periods with nothing in them are included, so a chart shows a quiet Sunday as a gap rather than
/// closing it up and making the week look continuous.
/// </para>
/// </summary>
public record BandwidthReport(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    BandwidthBucket Bucket,
    long BytesIn,
    long BytesOut,
    IReadOnlyList<BandwidthPeriod> Periods,
    IReadOnlyList<BandwidthByUser> Users
)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public long Total => this.BytesIn + this.BytesOut;

    public static BandwidthReport Empty(DateTimeOffset from, DateTimeOffset to, BandwidthBucket bucket)
        => new(from, to, bucket, 0, 0, [], []);
}


// ---------------------------------------------------------------------------
// Share links
// ---------------------------------------------------------------------------

/// <summary>Who may open a share link.</summary>
public enum ShareLinkAudience
{
    /// <summary>Anybody holding the link, signed in or not.</summary>
    Anyone = 0,

    /// <summary>Only the accounts it names, once they have signed in.</summary>
    Accounts = 1
}

/// <summary>An account a link can be shared with - enough to pick one from a list, and nothing more.</summary>
public record ShareRecipient(string Id, string Username, string DisplayName);

public record CreateShareLinkRequest(
    string FolderId,

    /// <summary>The file or folder inside the share, "" for the whole share.</summary>
    string Path,
    string? Title,
    ShareLinkAudience Audience,
    List<string> UserIds,

    /// <summary>Null never expires.</summary>
    DateTimeOffset? ExpiresAtUtc
);

public record ShareLinkInfo(
    string Id,

    /// <summary>
    /// The whole address to hand out: the public tunnel when it is open, otherwise the LAN address.
    /// Worked out when the link is read rather than stored, so a link made before the tunnel opened
    /// copies the tunnel's address afterwards.
    /// </summary>
    string Url,
    string FolderId,
    string FolderName,
    string Path,
    bool IsDirectory,
    string Title,
    ShareLinkAudience Audience,
    List<ShareRecipient> Recipients,
    string CreatedByUserId,
    string CreatedBy,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? ExpiresAtUtc,
    int OpenCount,
    DateTimeOffset? LastOpenedUtc,
    bool IsExpired
);

/// <summary>What the person following a link is shown before anything inside it.</summary>
public record SharedItem(
    string Title,
    string Name,
    bool IsDirectory,
    string SharedBy,
    DateTimeOffset? ExpiresAtUtc,

    /// <summary>The file's size; 0 for a folder.</summary>
    long Size,
    DateTimeOffset ModifiedUtc
);

/// <summary>One directory inside a shared folder. Paths are relative to the link, never to the share.</summary>
public record SharedListing(string Path, List<FileEntryInfo> Entries);

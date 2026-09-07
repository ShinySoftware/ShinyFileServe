using System.Text.Json.Serialization;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Core.Documents;

/// <summary>A named group. Permissions are never granted to a user directly - only to a role.</summary>
public class Role
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>
    /// Members of this group administer the server: every share regardless of grant, the activity
    /// log, and the settings - from the desktop app or the web UI. Distinct from
    /// <see cref="User.IsAdmin"/>, which pins the same power to one person rather than a group.
    /// </summary>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// The administrators group created during first-run setup. It cannot be deleted and cannot
    /// stop being an admin group - a server with no way back into its own settings is a server
    /// nobody can fix.
    /// </summary>
    public bool IsProtected { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
}

public class User
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }

    /// <summary>PBKDF2-SHA256 hash, base64.</summary>
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public int PasswordIterations { get; set; }

    public List<string> RoleIds { get; set; } = [];

    /// <summary>
    /// An admin bypasses folder grants entirely and can reach the management API. A user is also an
    /// admin when any group they belong to is marked as one - see <see cref="Role.IsAdmin"/>.
    /// </summary>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// When the account stops working - it can no longer sign in, and any cookie it still holds is
    /// refused on the next request. Null means it lives forever, which is what most accounts are.
    /// </summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    /// <summary>
    /// Read only by the one-time migration in <c>FileServeInitializer</c>. Accounts used to be
    /// switched off with an <c>isActive</c> flag; a database written then still carries it, and a
    /// disabled account coming back usable because the field is no longer read would be the worst
    /// possible reading of it. The migration turns a stored false into an expiry of "now" and clears
    /// this, after which nothing writes the field again.
    /// </summary>
    [JsonPropertyName("isActive")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyIsActive { get; set; }

    /// <summary>
    /// No password, and anybody who can reach the server may sign in as it. Its rights still come
    /// from its groups, so an anonymous account is only as open as an administrator made it.
    /// </summary>
    public bool IsAnonymous { get; set; }

    /// <summary>How many files this account may download per period. None, or a null cap, is unmetered.</summary>
    public QuotaWindow DownloadCapWindow { get; set; } = QuotaWindow.None;
    public int? MaxDownloads { get; set; }

    public QuotaWindow UploadCapWindow { get; set; } = QuotaWindow.None;
    public int? MaxUploads { get; set; }

    /// <summary>
    /// The administrator created during first-run setup. It can be renamed and have its password
    /// changed, but it cannot be deleted, expired, made anonymous, or stripped of admin - the same
    /// reason the administrators group is protected.
    /// </summary>
    public bool IsProtected { get; set; }

    /// <summary>
    /// When this account's password was last set - at creation, by the person themselves, or by an
    /// administrator. Null on an account written before the field existed.
    /// </summary>
    public DateTimeOffset? PasswordChangedUtc { get; set; }

    /// <summary>
    /// Consecutive failed sign-ins. Any successful one puts it back to zero, so this is a run of
    /// failures rather than a lifetime total.
    /// </summary>
    public int FailedSignInCount { get; set; }

    /// <summary>
    /// When the account locked itself after too many failed sign-ins. Null is the normal state, and
    /// only an administrator clears it once it is set.
    /// </summary>
    public DateTimeOffset? LockedOutUtc { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? LastLoginUtc { get; set; }

    /// <summary>True once the expiry has passed. An account with no expiry never is.</summary>
    public bool IsExpired(DateTimeOffset now) => this.ExpiresAtUtc is { } expiry && expiry <= now;

    /// <summary>
    /// The instant this account's password dates from. An account that has never recorded a change
    /// ages from the day it was made rather than from the epoch: the alternative would declare every
    /// password on an upgraded database infinitely old the moment an expiry was configured, and lock
    /// everybody out at once over a setting that was meant to take effect gradually.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset PasswordSetUtc => this.PasswordChangedUtc ?? this.CreatedUtc;

    /// <summary>Derived from the timestamp, so the two can never disagree about whether it is locked.</summary>
    [JsonIgnore]
    public bool IsLockedOut => this.LockedOutUtc is not null;
}

/// <summary>A directory on the host machine published under a friendly name. The "setup" in the UI.</summary>
public class ShareFolder
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string PhysicalPath { get; set; } = "";
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; }
}

/// <summary>
/// What one role may do inside one folder, plus the quotas that apply to members of that role.
/// A user with several roles gets the union of the permissions and the most generous quota.
/// </summary>
public class FolderGrant
{
    public string Id { get; set; } = "";
    public string FolderId { get; set; } = "";
    public string RoleId { get; set; } = "";
    public FolderPermissions Permissions { get; set; } = FolderPermissions.List;

    public QuotaWindow DownloadQuotaWindow { get; set; } = QuotaWindow.None;
    public long? DownloadQuotaBytes { get; set; }

    public QuotaWindow UploadQuotaWindow { get; set; } = QuotaWindow.None;
    public long? UploadQuotaBytes { get; set; }

    /// <summary>Hard cap on a single upload, independent of the rolling quota. Null = server limit only.</summary>
    public long? MaxUploadFileSizeBytes { get; set; }
}

/// <summary>Bytes moved by one user in one folder during one quota period.</summary>
public class QuotaUsage
{
    /// <summary>Composite: <c>{userId}|{folderId}|{window}|{periodKey}</c>.</summary>
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string FolderId { get; set; } = "";
    public QuotaWindow Window { get; set; }
    public string PeriodKey { get; set; } = "";
    public long DownloadedBytes { get; set; }
    public long UploadedBytes { get; set; }
    public DateTimeOffset PeriodStartUtc { get; set; }
    public DateTimeOffset? PeriodEndUtc { get; set; }

    public static string MakeId(string userId, string folderId, QuotaWindow window, string periodKey)
        => $"{userId}|{folderId}|{window}|{periodKey}";
}

/// <summary>
/// How many files one account moved during one period. Separate from <see cref="QuotaUsage"/>
/// because it counts transfers rather than bytes and it is per account rather than per folder - a
/// cap of "twenty downloads a day" is about the person, not about where they got the files from.
/// </summary>
public class TransferCount
{
    /// <summary>Composite: <c>{userId}|{window}|{periodKey}</c>.</summary>
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public QuotaWindow Window { get; set; }
    public string PeriodKey { get; set; } = "";
    public int Downloads { get; set; }
    public int Uploads { get; set; }
    public DateTimeOffset PeriodStartUtc { get; set; }
    public DateTimeOffset? PeriodEndUtc { get; set; }

    public static string MakeId(string userId, QuotaWindow window, string periodKey)
        => $"{userId}|{window}|{periodKey}";
}

/// <summary>One line in the audit trail. Written for every action a user takes, allowed or denied.</summary>
public class ActivityLogEntry
{
    public string Id { get; set; } = "";
    public DateTimeOffset TimestampUtc { get; set; }
    public string? UserId { get; set; }
    public string Username { get; set; } = "";
    public ActivityAction Action { get; set; }
    public string? FolderId { get; set; }
    public string? FolderName { get; set; }
    public string? Path { get; set; }
    public long Bytes { get; set; }
    public bool Success { get; set; }
    public string? Detail { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}

/// <summary>
/// The server's own configuration. A single document (<see cref="SingletonId"/>) so the desktop app
/// and the web UI read and write the same values rather than each keeping their own copy.
/// </summary>
public class ServerSettings
{
    public const string SingletonId = "server";

    public string Id { get; set; } = SingletonId;

    /// <summary>The port the listener binds. It always binds every interface - this is a LAN server.</summary>
    public int Port { get; set; } = 8080;

    /// <summary>
    /// Start serving as soon as the desktop app opens. On by default: the app has no UI of its own
    /// beyond a window onto its own server, so an app that opened without serving would open onto
    /// nothing.
    /// </summary>
    public bool AutoStartServer { get; set; } = true;

    /// <summary>
    /// A pinggy access token. Without one the tunnel still works, but anonymous pinggy tunnels
    /// expire after 60 minutes and take a new address each time they reconnect.
    /// </summary>
    public string? PinggyToken { get; set; }

    /// <summary>Open the public tunnel as soon as the server starts.</summary>
    public bool AutoStartTunnel { get; set; }

    /// <summary>
    /// Whether the sign-in page offers "keep me signed in". With it off, every session is a browser
    /// session and closing the tab ends it.
    /// </summary>
    public bool AllowRememberMe { get; set; } = true;

    /// <summary>How long a remembered session lasts. Ignored when <see cref="AllowRememberMe"/> is off.</summary>
    public int RememberMeDays { get; set; } = 30;

    /// <summary>
    /// Minutes of no keyboard or mouse before a signed-in browser signs itself out. 0 turns it off.
    /// Enforced in the browser, which is the only place that knows the screen has gone idle.
    /// </summary>
    public int IdleLogoffMinutes { get; set; }

    /// <summary>What a password has to look like. Replaced wholesale rather than merged on save.</summary>
    public PasswordRules PasswordRules { get; set; } = new();

    /// <summary>
    /// How many consecutive failed sign-ins an account tolerates before it locks itself. 0 never
    /// locks anybody. Ten by default: high enough that somebody working through the three passwords
    /// they use is not punished for it, low enough that guessing over the network is pointless -
    /// ten tries against any password worth the name is nothing.
    /// </summary>
    public int MaxFailedSignIns { get; set; } = 10;

    /// <summary>Show a desktop notification when one of <see cref="NotifyOn"/> happens.</summary>
    public bool NotificationsEnabled { get; set; }

    /// <summary>
    /// Which activities are worth interrupting someone for. Empty with notifications on means
    /// nothing is announced, which is a deliberate state rather than a broken one.
    /// </summary>
    public List<ActivityAction> NotifyOn { get; set; } = [ActivityAction.Login, ActivityAction.Denied];

    public DateTimeOffset? UpdatedUtc { get; set; }
}

/// <summary>
/// A friendly name and a note for one path inside a share - a directory or a file, the model does
/// not care. What is on disk keeps its real name; this is what people see, so a folder can read as
/// "Q3 Campaign" while living at <c>2026/q3-cmp-final</c>, and a file can carry a line explaining
/// what it is for.
/// </summary>
public class PathAnnotation
{
    public string Id { get; set; } = "";
    public string FolderId { get; set; } = "";

    /// <summary>Path inside the share, "" for the share root. Normalised to forward slashes.</summary>
    public string Path { get; set; } = "";

    public string? DisplayName { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// What makes an acceptable password on this server. Every rule is optional and the whole set can
/// be switched off - some deployments are a cupboard on a home network, and a password policy there
/// is friction with nothing behind it.
/// </summary>
public class PasswordRules
{
    /// <summary>
    /// False accepts any password at all. A password still cannot be empty: that is not a policy
    /// choice, it is what the word means.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public int MinimumLength { get; set; } = 8;
    public bool RequireUppercase { get; set; }
    public bool RequireLowercase { get; set; }
    public bool RequireNumber { get; set; }
    public bool RequireSpecialCharacter { get; set; }

    /// <summary>Refuse the handful of passwords that turn up first in every credential-stuffing list.</summary>
    public bool BlockCommonPasswords { get; set; } = true;

    /// <summary>
    /// How many days a password may be used before the person is made to set a new one. 0 - the
    /// default - never expires anything, and is the only value most of these servers should ever
    /// hold: forced rotation costs somebody an interruption every time it fires, and only earns that
    /// where an operator decided it does.
    /// </summary>
    /// <remarks>
    /// Read whether or not <see cref="Enabled"/> is on. That switch governs what a password has to
    /// look like; age is a different question, and an operator who turned the shape rules off has
    /// not said anything about how long a password should last.
    /// </remarks>
    public int MaximumAgeDays { get; set; }
}

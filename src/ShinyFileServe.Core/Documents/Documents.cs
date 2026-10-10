using System.Text.Json.Serialization;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Core.Documents;

/// <summary>
/// A named group. Folder permissions are never granted to a user directly - only to a role. Package
/// feed rights are the one exception: a group grants them, and an account may override them per feed
/// (see <see cref="User.NuGetAccess"/>).
/// </summary>
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

    /// <summary>
    /// What members may do with the package feeds. Folded across every group a person is in, the
    /// same way folder grants are, so joining a group can only ever add to it.
    /// </summary>
    public PackagePermissions PackagePermissions { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
}

/// <summary>
/// A personal access token - what <c>nuget.config</c> and <c>.npmrc</c> carry instead of a password.
/// Only a SHA-256 of the secret is kept: a token is 256 random bits, so there is nothing for a salt
/// or a slow hash to protect, and a plain hash is what lets a request find its token in one lookup.
/// </summary>
public class AccessToken
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Lower-case hex SHA-256 of the whole secret, prefix included.</summary>
    public string TokenHash { get; set; } = "";

    /// <summary>The secret's last four characters, for telling tokens apart in a list.</summary>
    public string Hint { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? LastUsedUtc { get; set; }

    /// <summary>Null never expires.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    public bool IsExpired(DateTimeOffset now) => this.ExpiresAtUtc is { } expiry && expiry <= now;
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

    /// <summary>
    /// How many downloads this account may have running at the same time. Null is no limit. This is
    /// about how much of the server one person may occupy at once, not how much they may move in
    /// total - the byte quotas on the folder grant are what meter that.
    /// </summary>
    public int? MaxParallelDownloads { get; set; }

    /// <summary>How many uploads this account may have running at the same time. Null is no limit.</summary>
    public int? MaxParallelUploads { get; set; }

    /// <summary>
    /// A ceiling on the bytes this account may download across every share, measured over
    /// <see cref="DownloadQuotaWindow"/>. It sits beside the quota on the folder grant rather than
    /// replacing it: the grant answers "how much may this group take out of this share", and this
    /// answers "how much may this person take off this server at all". Both are checked, and the
    /// tighter one is what a transfer meets first.
    /// </summary>
    /// <remarks>
    /// This is the one limit that is a fact about the person rather than about a group they happen
    /// to be in - which is why it lives here. Adding somebody to a second group can only ever widen
    /// what the grants allow (see <c>AccessResolver</c>); nothing on a group can widen this.
    /// </remarks>
    public QuotaWindow DownloadQuotaWindow { get; set; } = QuotaWindow.None;

    /// <summary>The ceiling itself. Null - or a window of <see cref="QuotaWindow.None"/> - is unmetered.</summary>
    public long? DownloadQuotaBytes { get; set; }

    /// <summary>The same, for what this account may put onto the server. <see cref="DownloadQuotaWindow"/>.</summary>
    public QuotaWindow UploadQuotaWindow { get; set; } = QuotaWindow.None;

    public long? UploadQuotaBytes { get; set; }

    /// <summary>
    /// This account's own NuGet access, laid over what its groups grant. The one package right that
    /// is a fact about the person - see <see cref="PackageAccessLevelExtensions.Apply"/>.
    /// </summary>
    public PackageAccessLevel NuGetAccess { get; set; }

    /// <summary>The same, for the npm registry.</summary>
    public PackageAccessLevel NpmAccess { get; set; }

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
    /// <summary>
    /// The <see cref="FolderId"/> of a row that counts an account rather than a share - the whole
    /// server's worth of one person's traffic, against <see cref="User.DownloadQuotaBytes"/> and
    /// <see cref="User.UploadQuotaBytes"/>. A sentinel rather than a second document type because
    /// the period arithmetic, the ids and the rollover are identical; only the scope differs. No
    /// real folder id can collide with it - they are GUIDs.
    /// </summary>
    public const string AccountScope = "*";

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
/// One request to the NuGet feed or the npm registry. Kept apart from <see cref="ActivityLogEntry"/>
/// because a single restore is dozens of these: the activity log answers "who changed what", and this
/// answers "who pulled what, with which token, and how often".
/// </summary>
public class PackageRequestLogEntry
{
    public string Id { get; set; } = "";
    public DateTimeOffset TimestampUtc { get; set; }
    public PackageFeedKind Feed { get; set; }
    public PackageOperation Operation { get; set; }
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string? PackageId { get; set; }

    /// <summary>Lower-cased <see cref="PackageId"/>, so a filter can match without caring how it was typed.</summary>
    public string? PackageKey { get; set; }
    public string? Version { get; set; }
    public int StatusCode { get; set; }
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
    public long DurationMs { get; set; }
    public string? UserId { get; set; }
    public string? Username { get; set; }
    public string? TokenId { get; set; }
    public string? TokenName { get; set; }
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

    /// <summary>The NuGet feed and npm registry. Both off until an administrator turns them on.</summary>
    public PackageFeedSettings PackageFeeds { get; set; } = new();

    public DateTimeOffset? UpdatedUtc { get; set; }
}

/// <summary>How the package feeds run. Who may use them is on <see cref="Role.PackagePermissions"/>, overridden per account by <see cref="User.NuGetAccess"/> and <see cref="User.NpmAccess"/>.</summary>
public class PackageFeedSettings
{
    public bool NuGetEnabled { get; set; }
    public bool NpmEnabled { get; set; }

    /// <summary>
    /// What a request carrying no token may do. Reads only - the save path strips anything else,
    /// because a package nobody can be held to account for is how a supply chain gets poisoned.
    /// </summary>
    public PackagePermissions Anonymous { get; set; }

    /// <summary>Fall through to registry.npmjs.org for anything not published here.</summary>
    public bool NpmUseUpstream { get; set; }

    public bool NpmAllowUnpublish { get; set; } = true;

    /// <summary>
    /// <c>dotnet nuget delete</c> removes the version for good. Off by default, which unlists it the
    /// way nuget.org does - anything already pinned to that version keeps restoring.
    /// </summary>
    public bool NuGetPermanentDelete { get; set; }
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

/// <summary>
/// Bytes one account moved through the server during one hour, both ways.
/// <para>
/// An hour rather than a request: a file server hands out tens of thousands of responses a day and
/// a row for each would be a second activity log, three orders of magnitude larger, kept for a
/// question nobody asks at that resolution. An hour is the finest cut any report offers, so it is
/// the finest cut worth storing - a day is summed from twenty-four of these.
/// </para>
/// </summary>
public class BandwidthUsage
{
    /// <summary>Composite: <c>{userId}|{yyyyMMddHH}</c>, so the hour a byte belongs to is the row it lands on.</summary>
    public string Id { get; set; } = "";

    public string UserId { get; set; } = "";

    /// <summary>
    /// Stamped as the bytes are counted rather than looked up as the report is read. An account that
    /// has since been deleted still has to be nameable, or a month's traffic loses its owner the
    /// day somebody leaves.
    /// </summary>
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>The top of the hour, UTC. Reports bucket from this, so it is never a local instant.</summary>
    public DateTimeOffset HourUtc { get; set; }

    /// <summary>Request bodies: what clients sent the server.</summary>
    public long BytesIn { get; set; }

    /// <summary>Response bodies: what the server sent back.</summary>
    public long BytesOut { get; set; }

    public static string MakeId(string userId, DateTimeOffset hourUtc)
        => $"{userId}|{hourUtc.UtcDateTime:yyyyMMddHH}";

    /// <summary>The top of the hour an instant falls in. One function, so a write and a read agree.</summary>
    public static DateTimeOffset Hour(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }
}

/// <summary>
/// One file or folder inside a share, handed out as an address. The link carries none of its own
/// rights: it lends the rights of the person who made it, checked every time it is opened, so a
/// creator who loses their grant - or their account - takes every link they made down with it.
/// </summary>
public class ShareLink
{
    public string Id { get; set; } = "";

    /// <summary>
    /// The secret in the address. Kept as it was issued rather than hashed, unlike an
    /// <see cref="AccessToken"/>: the person who made a link has to be able to copy it again, and
    /// anybody who can read this database can read the files it points at anyway.
    /// </summary>
    public string Token { get; set; } = "";

    public string FolderId { get; set; } = "";

    /// <summary>Path inside the share, "" for the share root. Normalised to forward slashes.</summary>
    public string Path { get; set; } = "";

    /// <summary>What was there when the link was made. A link to a file never starts serving a folder.</summary>
    public bool IsDirectory { get; set; }

    /// <summary>What the person opening it sees as the heading. Defaults to the name on disk.</summary>
    public string Title { get; set; } = "";

    public ShareLinkAudience Audience { get; set; }

    /// <summary>The accounts that may open it when <see cref="Audience"/> is <see cref="ShareLinkAudience.Accounts"/>.</summary>
    public List<string> UserIds { get; set; } = [];

    public string CreatedByUserId { get; set; } = "";

    /// <summary>Stamped at creation so the list can still say who made a link after they are gone.</summary>
    public string CreatedByUsername { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>Null never expires.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    /// <summary>How many times the link itself was opened - not every photo fetched behind it.</summary>
    public int OpenCount { get; set; }
    public DateTimeOffset? LastOpenedUtc { get; set; }

    public bool IsExpired(DateTimeOffset now) => this.ExpiresAtUtc is { } expiry && expiry <= now;
}

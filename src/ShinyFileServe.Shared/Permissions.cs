namespace ShinyFileServe.Shared;

/// <summary>
/// The set of operations a role may be granted against a share folder.
/// These are the five operations the product exposes; anything not listed is never permitted.
/// </summary>
[Flags]
public enum FolderPermissions
{
    None = 0,

    /// <summary>Enumerate directory contents. Without this the folder is invisible to the user.</summary>
    List = 1,

    /// <summary>Read/download a file's bytes.</summary>
    DownloadFile = 2,

    /// <summary>Upload a new file, or overwrite an existing one.</summary>
    CreateFile = 4,

    /// <summary>Delete a file.</summary>
    DeleteFile = 8,

    /// <summary>Create a sub-directory.</summary>
    CreateDirectory = 16
}

public static class FolderPermissionsExtensions
{
    public static bool Has(this FolderPermissions value, FolderPermissions flag) => (value & flag) == flag;

    public static IReadOnlyList<FolderPermissions> All { get; } =
    [
        FolderPermissions.List,
        FolderPermissions.DownloadFile,
        FolderPermissions.CreateFile,
        FolderPermissions.DeleteFile,
        FolderPermissions.CreateDirectory
    ];

    public static string ToDisplay(this FolderPermissions value) => value switch
    {
        FolderPermissions.List => "List",
        FolderPermissions.DownloadFile => "Download",
        FolderPermissions.CreateFile => "Upload",
        FolderPermissions.DeleteFile => "Delete",
        FolderPermissions.CreateDirectory => "New Folder",
        _ => value.ToString()
    };
}

/// <summary>
/// The rolling window a quota is measured over. <see cref="Total"/> never resets.
/// </summary>
public enum QuotaWindow
{
    None = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3,
    Total = 4
}

/// <summary>
/// What a group may do with the NuGet feed and the npm registry. Granted to a role, like folder
/// permissions, and folded across every group a person is in, then overridden per feed by the
/// account's own <see cref="PackageAccessLevel"/>; an administrator holds all of it.
/// The anonymous set in the server settings uses the same flags but is limited to the reads.
/// </summary>
/// <remarks>
/// npm has no separate unpublish flag. npm removes a single version by sending the package document
/// back without it - the same request that deprecates one - so a flag the server could not tell
/// apart from <see cref="NpmPublish"/> would be a promise it cannot keep. Unpublishing is a server
/// setting instead, and only ever touches packages the caller maintains.
/// </remarks>
[Flags]
public enum PackagePermissions
{
    None = 0,

    /// <summary>Restore and search the NuGet feed.</summary>
    NuGetRead = 1,

    /// <summary><c>dotnet nuget push</c> a new package version.</summary>
    NuGetPush = 2,

    /// <summary><c>dotnet nuget delete</c> - unlist (or delete, per settings) and relist a version.</summary>
    NuGetDelete = 4,

    /// <summary>Install from and search the npm registry.</summary>
    NpmRead = 8,

    /// <summary>Publish, deprecate, dist-tag and manage owners of the packages you maintain.</summary>
    NpmPublish = 16
}

public static class PackagePermissionsExtensions
{
    public const PackagePermissions All = PackagePermissions.NuGetRead | PackagePermissions.NuGetPush
        | PackagePermissions.NuGetDelete | PackagePermissions.NpmRead | PackagePermissions.NpmPublish;

    /// <summary>The only flags anonymous access may carry - nobody publishes without a name attached.</summary>
    public const PackagePermissions Reads = PackagePermissions.NuGetRead | PackagePermissions.NpmRead;

    public const PackagePermissions NuGet = PackagePermissions.NuGetRead | PackagePermissions.NuGetPush | PackagePermissions.NuGetDelete;
    public const PackagePermissions Npm = PackagePermissions.NpmRead | PackagePermissions.NpmPublish;

    public static bool Has(this PackagePermissions value, PackagePermissions flag) => (value & flag) == flag;

    public static bool Any(this PackagePermissions value, PackagePermissions flags) => (value & flags) != 0;

    public static IReadOnlyList<PackagePermissions> NuGetFlags { get; } =
    [
        PackagePermissions.NuGetRead,
        PackagePermissions.NuGetPush,
        PackagePermissions.NuGetDelete
    ];

    public static IReadOnlyList<PackagePermissions> NpmFlags { get; } =
    [
        PackagePermissions.NpmRead,
        PackagePermissions.NpmPublish
    ];

    public static string ToDisplay(this PackagePermissions value) => value switch
    {
        PackagePermissions.NuGetRead => "Restore",
        PackagePermissions.NuGetPush => "Push",
        PackagePermissions.NuGetDelete => "Unlist",
        PackagePermissions.NpmRead => "Install",
        PackagePermissions.NpmPublish => "Publish",
        _ => value.ToString()
    };
}

/// <summary>
/// One account's own say over one feed, set on the account rather than a group. Anything but
/// <see cref="Inherit"/> replaces what the groups grant for that feed - in either direction - so a
/// person can be held to reads in a group that publishes, or let publish from a group that only reads.
/// </summary>
/// <remarks>
/// Inherit is zero so an account stored before this existed reads back exactly as it behaved.
/// An administrator still holds every right, and an account with no password still holds none:
/// neither is a fact about the feed, and this cannot change either.
/// </remarks>
public enum PackageAccessLevel
{
    Inherit = 0,
    None = 1,
    Read = 2,
    ReadWrite = 3
}

/// <summary>Which of the two feeds a request went to.</summary>
public enum PackageFeedKind
{
    NuGet = 1,
    Npm = 2
}

/// <summary>What a request to a feed was for, as far as the traffic log is concerned.</summary>
public enum PackageOperation
{
    /// <summary>The service index, version lists, registration pages, npm package documents.</summary>
    Metadata = 0,
    Search = 1,

    /// <summary>A <c>.nupkg</c> or <c>.tgz</c> - an actual restore or install of one version.</summary>
    Download = 2,
    Publish = 3,

    /// <summary>A NuGet unlist, relist or delete, or an npm unpublish.</summary>
    Remove = 4,

    /// <summary>npm's own housekeeping: login, whoami, ping, audit.</summary>
    Account = 5
}

public static class PackageAccessLevelExtensions
{
    public static IReadOnlyList<PackageAccessLevel> All { get; } =
    [
        PackageAccessLevel.Inherit,
        PackageAccessLevel.None,
        PackageAccessLevel.Read,
        PackageAccessLevel.ReadWrite
    ];

    /// <summary>
    /// The groups' set with each feed's override laid over it. A feed left on <see cref="PackageAccessLevel.Inherit"/>
    /// keeps whatever the groups gave it; any other level wipes that feed's flags and puts its own in.
    /// </summary>
    public static PackagePermissions Apply(this PackagePermissions fromGroups, PackageAccessLevel nuget, PackageAccessLevel npm)
    {
        var result = fromGroups;
        result = Override(result, nuget, PackagePermissionsExtensions.NuGet, PackagePermissions.NuGetRead);
        result = Override(result, npm, PackagePermissionsExtensions.Npm, PackagePermissions.NpmRead);
        return result;
    }

    static PackagePermissions Override(PackagePermissions current, PackageAccessLevel level, PackagePermissions feed, PackagePermissions read) => level switch
    {
        PackageAccessLevel.None => current & ~feed,
        PackageAccessLevel.Read => (current & ~feed) | read,
        PackageAccessLevel.ReadWrite => current | feed,
        _ => current
    };

    /// <summary>How much of one feed a permission set amounts to, in the same three words the override uses.</summary>
    public static PackageAccessLevel LevelFor(this PackagePermissions value, PackageFeedKind feed)
    {
        var (all, read) = feed == PackageFeedKind.NuGet
            ? (PackagePermissionsExtensions.NuGet, PackagePermissions.NuGetRead)
            : (PackagePermissionsExtensions.Npm, PackagePermissions.NpmRead);

        if ((value & all) == PackagePermissions.None)
            return PackageAccessLevel.None;

        return (value & all & ~read) != PackagePermissions.None ? PackageAccessLevel.ReadWrite : PackageAccessLevel.Read;
    }

    public static string ToDisplay(this PackageAccessLevel level) => level switch
    {
        PackageAccessLevel.Inherit => "From groups",
        PackageAccessLevel.None => "No access",
        PackageAccessLevel.Read => "Read",
        PackageAccessLevel.ReadWrite => "Read & write",
        _ => level.ToString()
    };

    public static string ToDisplay(this PackageFeedKind feed) => feed == PackageFeedKind.NuGet ? "NuGet" : "npm";

    public static string ToDisplay(this PackageOperation operation) => operation switch
    {
        PackageOperation.Metadata => "Metadata",
        PackageOperation.Search => "Search",
        PackageOperation.Download => "Download",
        PackageOperation.Publish => "Publish",
        PackageOperation.Remove => "Unlist / remove",
        PackageOperation.Account => "Account",
        _ => operation.ToString()
    };
}

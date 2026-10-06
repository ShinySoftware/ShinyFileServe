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
/// permissions, and folded across every group a person is in; an administrator holds all of it.
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

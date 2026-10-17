namespace ShinyFileServe.App;

/// <summary>
/// One place that decides where this app keeps its state, so the three heads cannot drift apart.
/// Everything else - port, tunnel token, auto-start - lives in the database, because the web UI
/// edits the same values and a second copy on disk would only be a second source of truth.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// FILESERVE_DATA points a run at a different directory - a demo, a second instance, a test -
    /// without touching the real one. It has to be an environment variable: on macOS the default
    /// comes from the OS rather than from HOME, so there is no other way to move it.
    /// </summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("FILESERVE_DATA") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
                "ShinyFileServe"
            );

    public static string DatabaseFile => Path.Combine(DataDirectory, "fileserve.db");
    public static string TicketKeyFile => Path.Combine(DataDirectory, "cookie.key");
    public static string WebAppDirectory => Path.Combine(DataDirectory, "webapp");

    /// <summary>The NuGet feed's and npm registry's packages. Not in the database backup - they are files.</summary>
    public static string PackagesDirectory => Path.Combine(DataDirectory, "packages");
}

namespace ShinyFileServe.Headless;

/// <summary>
/// Where the headless server keeps its state. The same layout as the desktop app's AppPaths, so a
/// data folder can move between the two. In the container FILESERVE_DATA is /data, the volume.
/// </summary>
static class HeadlessPaths
{
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
    public static string PackagesDirectory => Path.Combine(DataDirectory, "packages");
    public static string UploadsDirectory => Path.Combine(DataDirectory, "uploads");
}

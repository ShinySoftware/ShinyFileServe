namespace ShinyFileServe.Web.Services;

/// <summary>
/// Which files a browser can show by itself, judged by name. HEIC is left out on purpose: only
/// Safari draws it, so in a gallery it would be a broken tile everywhere else - it goes in the
/// file list instead, where a download always works.
/// </summary>
public static class Media
{
    static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif", ".bmp", ".svg"
    };

    static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".webm", ".mov"
    };

    public static bool IsImage(string name) => Images.Contains(Path.GetExtension(name));

    public static bool IsVideo(string name) => Videos.Contains(Path.GetExtension(name));

    public static bool IsViewable(string name) => IsImage(name) || IsVideo(name);
}

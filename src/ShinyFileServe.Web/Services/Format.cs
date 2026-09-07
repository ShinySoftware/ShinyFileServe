namespace ShinyFileServe.Web.Services;

public static class Format
{
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// Sizes are read at a glance and compared down a column, so they get one decimal from MB up and
    /// none below - enough precision to be useful, few enough digits to scan.
    /// </summary>
    public static string Bytes(long value)
    {
        if (value <= 0)
            return "0 B";

        var order = 0;
        double size = value;
        while (size >= 1024 && order < Units.Length - 1)
        {
            size /= 1024;
            order++;
        }
        return order <= 1 ? $"{size:0} {Units[order]}" : $"{size:0.#} {Units[order]}";
    }

    public static string When(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var now = DateTimeOffset.Now;
        var age = now - local;

        if (age < TimeSpan.FromDays(1) && local.Date == now.Date)
            return local.ToString("HH:mm");

        return age < TimeSpan.FromDays(300)
            ? local.ToString("d MMM HH:mm")
            : local.ToString("d MMM yyyy");
    }

    /// <summary>"resets in 3 days" / "resets Monday" - a quota is only meaningful with its horizon.</summary>
    public static string? ResetHint(DateTimeOffset? resetsUtc)
    {
        if (resetsUtc is not { } resets)
            return null;

        var remaining = resets - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            return "resets now";

        if (remaining < TimeSpan.FromHours(1))
            return $"resets in {Math.Max(1, (int)remaining.TotalMinutes)} min";

        if (remaining < TimeSpan.FromDays(1))
            return $"resets in {(int)remaining.TotalHours} h";

        var days = (int)Math.Ceiling(remaining.TotalDays);
        return days == 1 ? "resets tomorrow" : $"resets in {days} days";
    }

    public static string Initials(string displayName)
    {
        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant()
        };
    }
}

namespace ShinyFileServe.Shared;

/// <summary>
/// Groups small files into the multipart requests that carry them.
/// </summary>
/// <remarks>
/// They used to go up all in one request. A folder of photos then became one request of several
/// hundred megabytes with no retry, so a single dropped connection lost every file in it. Bounded
/// batches keep each request short, so a failure costs one batch, and the files in batches that
/// already landed stay landed.
/// </remarks>
public static class UploadBatches
{
    /// <summary>The most bytes one batch carries. A file at least this large still gets a batch of its own.</summary>
    public const long MaxBytes = 32L * 1024 * 1024;

    /// <summary>The most files one batch carries, however small they are.</summary>
    public const int MaxFiles = 50;

    /// <summary>
    /// Splits <paramref name="indexes"/> into batches in their given order, each holding at most
    /// <paramref name="maxFiles"/> files and at most <paramref name="maxBytes"/> bytes (except a
    /// single file larger than that, which goes alone).
    /// </summary>
    public static List<List<int>> Split(IReadOnlyList<int> indexes, Func<int, long> sizeOf, long maxBytes = MaxBytes, int maxFiles = MaxFiles)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFiles, 1);

        var batches = new List<List<int>>();
        var current = new List<int>();
        long bytes = 0;

        foreach (var index in indexes)
        {
            var size = Math.Max(0, sizeOf(index));
            if (current.Count > 0 && (current.Count >= maxFiles || bytes + size > maxBytes))
            {
                batches.Add(current);
                current = [];
                bytes = 0;
            }

            current.Add(index);
            bytes += size;
        }

        if (current.Count > 0)
            batches.Add(current);

        return batches;
    }
}

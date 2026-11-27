using ShinyFileServe.Core.Documents;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Transfers;
using ShinyFileServe.Shared;
using ShinyFileServe.Tests.Infrastructure;

namespace ShinyFileServe.Tests;

public class UploadBatchesTests
{
    [Fact]
    public void Small_files_share_batches_up_to_the_byte_limit()
    {
        long[] sizes = [10, 10, 10, 10, 10];
        var batches = UploadBatches.Split([0, 1, 2, 3, 4], i => sizes[i], maxBytes: 25, maxFiles: 50);

        Assert.Equal([[0, 1], [2, 3], [4]], batches);
    }

    [Fact]
    public void Batches_stop_at_the_file_limit_however_small_the_files()
    {
        var batches = UploadBatches.Split(Enumerable.Range(0, 7).ToList(), _ => 1, maxBytes: 1000, maxFiles: 3);

        Assert.Equal([[0, 1, 2], [3, 4, 5], [6]], batches);
    }

    [Fact]
    public void A_file_larger_than_the_byte_limit_goes_alone()
    {
        long[] sizes = [5, 100, 5];
        var batches = UploadBatches.Split([0, 1, 2], i => sizes[i], maxBytes: 20, maxFiles: 50);

        Assert.Equal([[0], [1], [2]], batches);
    }

    [Fact]
    public void Order_is_kept_and_nothing_is_lost()
    {
        var indexes = new[] { 4, 2, 9, 0, 7 };
        var batches = UploadBatches.Split(indexes, i => i * 10L, maxBytes: 50, maxFiles: 2);

        Assert.Equal(indexes, batches.SelectMany(b => b));
    }

    [Fact]
    public void Nothing_to_send_is_no_batches()
        => Assert.Empty(UploadBatches.Split([], _ => 1));
}

public class LimitedReadStreamTests
{
    [Fact]
    public async Task Reads_everything_up_to_the_limit()
    {
        var bytes = Data.Random(1000);
        await using var limited = new LimitedReadStream(new MemoryStream(bytes), 1000);
        using var copy = new MemoryStream();

        await limited.CopyToAsync(copy, TestContext.Current.CancellationToken);

        Assert.Equal(bytes, copy.ToArray());
    }

    [Fact]
    public async Task Throws_as_soon_as_the_limit_is_passed()
    {
        await using var limited = new LimitedReadStream(new MemoryStream(Data.Random(1001)), 1000);

        var error = await Assert.ThrowsAsync<UploadLimitExceededException>(
            () => limited.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken));
        Assert.Equal(1000, error.Limit);
    }

    [Fact]
    public void Leaves_the_inner_stream_open()
    {
        var inner = new MemoryStream(Data.Random(10));
        new LimitedReadStream(inner, 100).Dispose();

        Assert.True(inner.CanRead);
    }
}

/// <summary>
/// Saving a finished resumable upload moves its file into the share. A failure after the move used
/// to delete the only copy; it must give the bytes back instead.
/// </summary>
public sealed class FileServiceSaveFileTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "fileserve-tests", Guid.NewGuid().ToString("N")[..12]);
    readonly FileService files = new();
    readonly FolderAccess access;

    public FileServiceSaveFileTests()
    {
        Directory.CreateDirectory(Path.Combine(this.root, "share"));
        Directory.CreateDirectory(Path.Combine(this.root, "uploads"));
        var folder = new ShareFolder { Id = "f", Name = "Share", PhysicalPath = Path.Combine(this.root, "share") };
        this.access = new FolderAccess(folder, FolderPermissions.CreateFile | FolderPermissions.DeleteFile, QuotaWindow.None, null, QuotaWindow.None, null, null);
    }

    string Source(byte[] bytes)
    {
        var path = Path.Combine(this.root, "uploads", Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, bytes);
        return path;
    }

    string[] ShareContents() => Directory.GetFiles(Path.Combine(this.root, "share"), "*", SearchOption.AllDirectories);

    [Fact]
    public async Task A_successful_save_moves_the_file_into_place()
    {
        var bytes = Data.Random(5000);
        var source = this.Source(bytes);

        var saved = await this.files.SaveFile(this.access, "a.bin", source, SaveConflict.Overwrite, ct: TestContext.Current.CancellationToken);

        Assert.Equal("a.bin", saved.VirtualPath);
        Assert.Equal(bytes.Length, saved.Bytes);
        Assert.False(File.Exists(source));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(this.root, "share", "a.bin")));
    }

    [Fact]
    public async Task An_unexpected_failure_after_the_move_gives_the_bytes_back()
    {
        var bytes = Data.Random(5000);
        var source = this.Source(bytes);

        await Assert.ThrowsAsync<IOException>(() => this.files.SaveFile(
            this.access, "a.bin", source, SaveConflict.Overwrite,
            inspect: (_, _) => throw new IOException("Disk full (simulated)"),
            ct: TestContext.Current.CancellationToken));

        Assert.Equal(bytes, File.ReadAllBytes(source));
        Assert.Empty(this.ShareContents());
    }

    [Fact]
    public async Task A_refusal_by_the_inspection_gives_the_bytes_back_and_publishes_nothing()
    {
        var bytes = Data.Random(5000);
        var source = this.Source(bytes);
        var decision = new ScanDecision(false, ScanVerdict.Infected, "test", "Test-Signature");

        await Assert.ThrowsAsync<FileRejectedException>(() => this.files.SaveFile(
            this.access, "a.bin", source, SaveConflict.Overwrite,
            inspect: (temp, _) => throw new FileRejectedException(decision, new FileInfo(temp).Length),
            ct: TestContext.Current.CancellationToken));

        Assert.True(File.Exists(source));
        Assert.Empty(this.ShareContents());
    }

    [Fact]
    public async Task A_refusal_before_anything_moves_leaves_the_source_alone()
    {
        var source = this.Source(Data.Random(10));
        File.WriteAllBytes(Path.Combine(this.root, "share", "taken.bin"), [1]);

        await Assert.ThrowsAsync<FileAccessException>(() => this.files.SaveFile(
            this.access, "taken.bin", source, SaveConflict.Fail, ct: TestContext.Current.CancellationToken));

        Assert.True(File.Exists(source));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

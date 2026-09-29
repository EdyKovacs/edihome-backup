using EdiHome.Backup;

namespace EdiHome.Backup.Tests;

public class RecentFilesTests
{
    [Fact]
    public async Task IncludesNextcloudFilesButNotPreviewNoise()
    {
        var changes = new RecentFiles();
        using var output = new StringWriter();
        var writer = changes.CaptureNextcloud(output);

        await writer.WriteLineAsync(">f+++++++++ admin/files/photo.jpg".AsMemory());
        await writer.WriteLineAsync(">f+++++++++ admin/files/preview/image.jpg".AsMemory());
        await writer.WriteLineAsync("*deleting admin/files/old.jpg".AsMemory());

        Assert.Equal(["NEXTCLOUD ADDED: admin/files/photo.jpg", "NEXTCLOUD DELETED: admin/files/old.jpg"], changes.Entries);
        Assert.Contains("preview/image.jpg", output.ToString());
    }

    [Fact]
    public async Task ReceivesLinesThroughSynchronizedProcessOutput()
    {
        var changes = new RecentFiles();
        var writer = TextWriter.Synchronized(changes.CaptureNextcloud(TextWriter.Null));

        await writer.WriteLineAsync(">f+++++++++ admin/files/from-rsync.jpg".AsMemory());

        Assert.Equal(["NEXTCLOUD ADDED: admin/files/from-rsync.jpg"], changes.Entries);
    }

    [Fact]
    public async Task CountsImmichChangesWithoutNoisyRegeneratedFiles()
    {
        var changes = new RecentFiles();
        var counts = new Dictionary<string, int> { ["added"] = 0, ["changed"] = 0, ["deleted"] = 0 };
        using var output = new StringWriter();
        var writer = changes.CaptureImmich(output, counts);

        await writer.WriteLineAsync(">f+++++++++ upload/new.jpg".AsMemory());
        await writer.WriteLineAsync(">f..t...... thumbs/regenerated.jpg".AsMemory());
        await writer.WriteLineAsync("*deleting library/deleted.jpg".AsMemory());

        Assert.Equal(1, counts["added"]);
        Assert.Equal(0, counts["changed"]);
        Assert.Equal(1, counts["deleted"]);
        Assert.Empty(changes.Entries);
    }
}
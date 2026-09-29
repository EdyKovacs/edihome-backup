using System.Text.Json;
using EdiHome.Backup;

namespace EdiHome.Backup.Tests;

public class BackupStatusTests
{
    [Fact]
    public async Task PartialRunDoesNotClaimCompleteBackup()
    {
        var root = Path.Combine(Path.GetTempPath(), "edihome-status-tests-" + Guid.NewGuid().ToString("N"));
        var oldMount = Path.Combine(root, "old").Replace('\\', '/');
        var newMount = Path.Combine(root, "new").Replace('\\', '/');
        var oldBase = oldMount + "/backup-box";
        Directory.CreateDirectory(oldBase);
        Directory.CreateDirectory(newMount);
        try
        {
            var mounts = new[] { new MountRequirement("1 TB", oldMount, "uuid-one"),
                new MountRequirement("4 TB", newMount, "uuid-four") };
            var settings = new BackupSettings(mounts,
                new BackupPlan(oldBase, newMount + "/backup-box", "/private/pass", "/private/excludes",
                    new RemoteHost("cloudapps", "/private/cloud-key"), new RemoteHost("proxmox", "/private/pve-key"),
                    "/docker", "/dump-nc", "/dump-im", "nfs-nc", "nfs-im", "/pve", [100],
                    "/frigate", "nfs-frigate", "/private/mqtt"));
            var status = new BackupStatus(settings, new FakeReader(newMount), new RecentFiles());
            status.Completed("nextcloud");
            status.Finish(success: true, partial: true);

            using var payload = JsonDocument.Parse(await status.SaveAsync(CancellationToken.None));

            Assert.Equal("partial", payload.RootElement.GetProperty("last_result").GetString());
            Assert.Equal("ok", payload.RootElement.GetProperty("nextcloud_result").GetString());
            Assert.Equal("pending", payload.RootElement.GetProperty("immich_result").GetString());
            Assert.Equal("pending", payload.RootElement.GetProperty("proxmox_result").GetString());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FakeReader(string newMount) : IMountUuidReader
    {
        public Task<string?> ReadUuidAsync(string mountPoint, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(mountPoint == newMount ? "uuid-four" : "uuid-one");
    }
}
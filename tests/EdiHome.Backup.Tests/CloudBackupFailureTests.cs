using EdiHome.Backup;

namespace EdiHome.Backup.Tests;

public class CloudBackupFailureTests
{
    [Fact]
    public async Task FailedNextcloudSnapshotDoesNotRunRetentionOrOtherComponents()
    {
        var root = Path.Combine(Path.GetTempPath(), "edihome-backup-tests-" + Guid.NewGuid().ToString("N"));
        var oldMount = Path.Combine(root, "old").Replace('\\', '/');
        var newMount = Path.Combine(root, "new").Replace('\\', '/');
        var oldBase = oldMount + "/backup-box";
        var newBase = newMount + "/backup-box";
        Directory.CreateDirectory(oldBase);
        Directory.CreateDirectory(newBase + "/restic-nextcloud");
        File.WriteAllText(newBase + "/restic-nextcloud/config", "test-repo");
        try
        {
            var mounts = new[] { new MountRequirement("1 TB", oldMount, "old"), new MountRequirement("4 TB", newMount, "new") };
            var plan = new BackupPlan(oldBase, newBase, "/private/passphrase", "/private/excludes",
                new RemoteHost("cloudapps", "/private/cloud-key"), new RemoteHost("proxmox", "/private/pve-key"),
                "/docker", "/dump-nc", "/dump-im", "nfs-nextcloud", "nfs-immich",
                "/mnt/pve/nas-backup/dump", [100], "/mnt/cloud/frigate/recordings", "nfs-frigate", "/private/mqtt");
            var runner = new FailedSnapshotRunner(newBase);
            var cycle = new BackupCycle(new BackupSettings(mounts, plan), runner, new MountedReader(oldMount, newMount));

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                cycle.RunAsync(true, TextWriter.Null, onlyComponent: "nextcloud"));

            Assert.Contains("nextcloud snapshot failed", failure.Message);
            Assert.Contains(runner.Executed, command => command.Executable == "restic" && command.Arguments.Contains("backup"));
            Assert.DoesNotContain(runner.Executed, command => command.Executable == "restic" && command.Arguments.Contains("forget"));
            Assert.DoesNotContain(runner.Executed, command => command.Executable == "rsync" && command.Arguments.Any(argument => argument.Contains("/immich/")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulManualNextcloudTestUsesConfiguredRetention(bool allowPrune)
    {
        var root = Path.Combine(Path.GetTempPath(), "edihome-backup-tests-" + Guid.NewGuid().ToString("N"));
        var oldMount = Path.Combine(root, "old").Replace('\\', '/');
        var newMount = Path.Combine(root, "new").Replace('\\', '/');
        var oldBase = oldMount + "/backup-box";
        var newBase = newMount + "/backup-box";
        Directory.CreateDirectory(oldBase);
        Directory.CreateDirectory(newBase + "/restic-nextcloud");
        File.WriteAllText(newBase + "/restic-nextcloud/config", "test-repo");
        try
        {
            var mounts = new[] { new MountRequirement("1 TB", oldMount, "old"), new MountRequirement("4 TB", newMount, "new") };
            var plan = new BackupPlan(oldBase, newBase, "/private/passphrase", "/private/excludes",
                new RemoteHost("cloudapps", "/private/cloud-key"), new RemoteHost("proxmox", "/private/pve-key"),
                "/docker", "/dump-nc", "/dump-im", "nfs-nextcloud", "nfs-immich",
                "/mnt/pve/nas-backup/dump", [100], "/mnt/cloud/frigate/recordings", "nfs-frigate", "/private/mqtt");
            var runner = new FailedSnapshotRunner(newBase, failSnapshot: false);
            var cycle = new BackupCycle(new BackupSettings(mounts, plan), runner, new MountedReader(oldMount, newMount));

            await cycle.RunAsync(true, TextWriter.Null, onlyComponent: "nextcloud", allowPrune: allowPrune);

            Assert.Contains(runner.Executed, command => command.Executable == "restic" && command.Arguments.Contains("backup"));
            Assert.Contains(runner.Executed, command => command.Executable == "restic" && command.Arguments.Contains("check"));
            if (allowPrune)
            {
                var retention = Assert.Single(runner.Executed, command => command.Executable == "restic" && command.Arguments.Contains("forget"));
                Assert.Equal(["forget", "--keep-weekly", "8", "--group-by", "host,tags", "--prune"], retention.Arguments);
            }
            else
            {
                Assert.DoesNotContain(runner.Executed, command => command.Executable == "restic" && command.Arguments.Contains("forget"));
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class MountedReader(string oldMount, string newMount) : IMountUuidReader
    {
        public Task<string?> ReadUuidAsync(string mountPoint, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(mountPoint == oldMount ? "old" : mountPoint == newMount ? "new" : null);
    }

    private sealed class FailedSnapshotRunner(string newBase, bool failSnapshot = true) : IExternalCommandRunner
    {
        public List<ExternalCommand> Executed { get; } = [];

        public async Task<int> RunAsync(ExternalCommand command, TextWriter output, CancellationToken cancellationToken = default)
        {
            Executed.Add(command);
            if (command.Executable == "ssh" && command.Arguments.Last().Contains("findmnt"))
                await output.WriteLineAsync("nfs-nextcloud");
            if (command.Executable == "rsync" && command.Arguments.Last().Contains("dbdump"))
                File.WriteAllBytes(Path.Combine(newBase, "staging-nextcloud", "dbdump", "dump.gz"), [1]);
            if (failSnapshot && command.Executable == "restic" && command.Arguments.Contains("backup")) return 23;
            return 0;
        }
    }
}
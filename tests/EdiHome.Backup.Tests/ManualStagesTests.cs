using EdiHome.Backup;

namespace EdiHome.Backup.Tests;

public class ManualStagesTests
{
    [Fact]
    public async Task SurveillanceOnlyCopiesClosedFilesAndSkipsRetention()
    {
        await WithTemporaryCycle(async (cycle, commands) =>
        {
            await cycle.RunAsync(true, TextWriter.Null, onlyComponent: "surveillance", allowPrune: false);

            var transfer = Assert.Single(commands.Executed, command => command.Executable == "rsync");
            Assert.Contains("--from0", transfer.Arguments);
            Assert.Contains("--files-from=-", transfer.Arguments);
            Assert.DoesNotContain("--delete-delay", transfer.Arguments);
            Assert.Equal("2026-09-28/09/balcon/closed.mp4\0", transfer.StandardInput);
            Assert.Contains(commands.Executed, command => command.Executable == "restic" && command.Arguments.Contains("backup"));
            Assert.Contains(commands.Executed, command => command.Executable == "restic" && command.Arguments.Contains("check"));
            Assert.DoesNotContain(commands.Executed, command => command.Executable == "restic" && command.Arguments.Contains("forget"));
        });
    }

    [Fact]
    public async Task ProxmoxOnlyCopiesSelectedFilesWithoutDeletingSource()
    {
        await WithTemporaryCycle(async (cycle, commands) =>
        {
            await cycle.RunAsync(true, TextWriter.Null, onlyComponent: "proxmox", allowPrune: false);

            var transfers = commands.Executed.Where(command => command.Executable == "rsync").ToArray();
            Assert.Equal(2, transfers.Length);
            Assert.All(transfers, transfer => Assert.DoesNotContain("--delete-delay", transfer.Arguments));
            Assert.Contains(transfers, transfer => transfer.Arguments.Any(argument => argument.EndsWith(".vma.zst", StringComparison.Ordinal)));
            Assert.Contains(transfers, transfer => transfer.Arguments.Any(argument => argument.EndsWith(".tar.gz", StringComparison.Ordinal)));
            Assert.Contains(commands.Executed, command => command.Executable == "restic" && command.Arguments.Contains("backup"));
            Assert.DoesNotContain(commands.Executed, command => command.Executable == "restic" && command.Arguments.Contains("forget"));
        });
    }

    [Fact]
    public async Task EqualSizeButOlderProxmoxFilesStillNeedTransferSpace()
    {
        const long reserve = 10L * 1024 * 1024 * 1024;
        await WithTemporaryCycle(async (cycle, commands) =>
        {
            var error = await Assert.ThrowsAsync<IOException>(() =>
                cycle.RunAsync(false, TextWriter.Null, onlyComponent: "proxmox"));

            Assert.Contains("shortfall 100", error.Message);
            Assert.DoesNotContain(commands.Executed, command => command.Executable is "rsync" or "restic");
        }, reserve + 300, createOldCopies: true);
    }

    private static async Task WithTemporaryCycle(Func<BackupCycle, FakeCommands, Task> check,
        long availableBytes = long.MaxValue, bool createOldCopies = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "edihome-stage-tests-" + Guid.NewGuid().ToString("N"));
        var oldMount = Path.Combine(root, "old").Replace('\\', '/');
        var newMount = Path.Combine(root, "new").Replace('\\', '/');
        var oldBase = oldMount + "/backup-box";
        var newBase = newMount + "/backup-box";
        Directory.CreateDirectory(oldBase + "/restic-proxmox-vm");
        Directory.CreateDirectory(newBase);
        File.WriteAllText(oldBase + "/restic-proxmox-vm/config", "test-repo");
        if (createOldCopies)
        {
            var vmDirectory = oldBase + "/staging-proxmox/vm-backups";
            var hostDirectory = oldBase + "/staging-proxmox/host-config";
            Directory.CreateDirectory(vmDirectory);
            Directory.CreateDirectory(hostDirectory);
            foreach (var file in new[] { vmDirectory + "/vzdump-qemu-100-2026.vma.zst",
                hostDirectory + "/proxmox-host-config-2026.tar.gz" })
            {
                File.WriteAllBytes(file, new byte[100]);
                File.SetLastWriteTimeUtc(file, DateTime.UnixEpoch.AddYears(1));
            }
        }
        try
        {
            var mounts = new[] { new MountRequirement("1 TB", oldMount, "old"), new MountRequirement("4 TB", newMount, "new") };
            var plan = new BackupPlan(oldBase, newBase, "/private/restic", "/private/excludes",
                new RemoteHost("cloudapps", "/private/cloud-key"), new RemoteHost("proxmox", "/private/pve-key"),
                "/docker", "/dump-nc", "/dump-im", "nfs-nextcloud", "nfs-immich",
                "/mnt/pve/nas-backup/dump", [100], "/mnt/cloud/frigate/recordings", "nfs-frigate", "/private/mqtt");
            var commands = new FakeCommands();
            var cycle = new BackupCycle(new BackupSettings(mounts, plan), commands,
                new MountedReader(oldMount, newMount), new PlentyOfSpace(availableBytes));
            await check(cycle, commands);
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

    private sealed class PlentyOfSpace(long availableBytes) : IFreeSpaceReader
    {
        public long AvailableBytes(string path) => availableBytes;
    }

    private sealed class FakeCommands : IExternalCommandRunner
    {
        public List<ExternalCommand> Executed { get; } = [];

        public async Task<int> RunAsync(ExternalCommand command, TextWriter output, CancellationToken cancellationToken = default)
        {
            Executed.Add(command);
            if (command.Executable == "ssh" && command.Arguments.Last().Contains("findmnt"))
                await output.WriteLineAsync("nfs-frigate");
            if (command.Executable == "ssh" && command.Arguments.Last().Contains("find '/mnt/pve"))
                await output.WriteLineAsync("vzdump-qemu-100-2026.vma.zst\t100\t1790580000.0\nproxmox-host-config-2026.tar.gz\t100\t1790580000.0");
            if (command.Executable == "ssh" && command.Arguments.Last().Contains("find '/mnt/cloud/frigate/recordings'"))
                await output.WriteLineAsync("2026-09-28/09/balcon/closed.mp4");
            return 0;
        }
    }
}
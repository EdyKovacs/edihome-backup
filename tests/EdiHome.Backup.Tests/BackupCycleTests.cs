using EdiHome.Backup;

namespace EdiHome.Backup.Tests;

public class BackupCycleTests
{
    [Fact]
    public async Task DryRunListsAllSourcesWithoutWriting()
    {
        var commands = new FakeCommands();
        using var output = new StringWriter();

        await CreateCycle(commands).RunAsync(false, output);

        Assert.Contains("6 Proxmox files", output.ToString());
        Assert.Contains("1 closed surveillance videos", output.ToString());
        Assert.DoesNotContain(commands.Executed, command => command.Executable is "rsync" or "restic");
    }

    [Fact]
    public async Task MissingVmBackupAbortsBeforeAnyCopy()
    {
        var commands = new FakeCommands { ProxmoxFiles = "proxmox-host-config-2026.tar.gz\t100\t1790580000.0" };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateCycle(commands).RunAsync(false, TextWriter.Null));

        Assert.Contains("VM 100", error.Message);
        Assert.DoesNotContain(commands.Executed, command => command.Executable is "rsync" or "restic");
    }

    [Fact]
    public async Task MissingVideosAbortsBeforeAnyCopy()
    {
        var commands = new FakeCommands { Videos = "" };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateCycle(commands).RunAsync(false, TextWriter.Null));

        Assert.Contains("No closed surveillance videos", error.Message);
        Assert.DoesNotContain(commands.Executed, command => command.Executable is "rsync" or "restic");
    }

    [Fact]
    public async Task WrongNfsMountAbortsBeforeAnyCopy()
    {
        var commands = new FakeCommands { NextcloudMount = "wrong-disk" };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateCycle(commands).RunAsync(false, TextWriter.Null));

        Assert.Contains("Expected NFS source", error.Message);
        Assert.DoesNotContain(commands.Executed, command => command.Executable is "rsync" or "restic");
    }

    [Fact]
    public async Task InsufficientProxmoxSpaceAbortsBeforeAnyCopy()
    {
        var commands = new FakeCommands();

        var error = await Assert.ThrowsAsync<IOException>(() =>
            CreateCycle(commands, 0).RunAsync(false, TextWriter.Null));

        Assert.Contains("Proxmox needs", error.Message);
        Assert.Contains("available 0, shortfall ", error.Message);
        Assert.DoesNotContain(commands.Executed, command => command.Executable is "rsync" or "restic");
    }

    [Fact]
    public async Task OnlyNextcloudSkipsProxmoxAndSurveillanceInventory()
    {
        var commands = new FakeCommands { ProxmoxFiles = "", Videos = "" };
        using var output = new StringWriter();

        await CreateCycle(commands).RunAsync(false, output, onlyComponent: "nextcloud");

        Assert.Contains("Planned: nextcloud", output.ToString());
        Assert.DoesNotContain(commands.Executed, command => command.Arguments.Last().Contains("find '", StringComparison.Ordinal));
        Assert.DoesNotContain(commands.Executed, command => command.Executable is "rsync" or "restic");
    }

    [Fact]
    public async Task OnlySurveillanceSkipsCloudAndProxmoxPreflight()
    {
        var commands = new FakeCommands { ProxmoxFiles = "", NextcloudMount = "wrong-disk" };
        using var output = new StringWriter();

        await CreateCycle(commands).RunAsync(false, output, onlyComponent: "surveillance");

        Assert.Contains("1 closed surveillance videos", output.ToString());
        Assert.DoesNotContain(commands.Executed, command => command.Arguments.Last().Contains("nextcloud", StringComparison.Ordinal));
        Assert.DoesNotContain(commands.Executed, command => command.Arguments.Last().Contains("find '/mnt/pve", StringComparison.Ordinal));
    }

    private static BackupCycle CreateCycle(FakeCommands commands, long freeBytes = long.MaxValue)
    {
        var mounts = new[] { new MountRequirement("1 TB", "/media/edi/data", "uuid-one"),
            new MountRequirement("4 TB", "/mnt/backup4tb", "uuid-four") };
        var plan = new BackupPlan("/media/edi/data/backup-box", "/mnt/backup4tb/backup-box",
            "/private/restic", "/private/excludes", new RemoteHost("cloudapps", "/private/cloud-key"),
            new RemoteHost("proxmox", "/private/pve-key"), "/docker", "/dump-nc", "/dump-im",
            "nfs-nextcloud", "nfs-immich", "/mnt/pve/nas-backup/dump", [100],
            "/mnt/cloud/frigate/recordings", "nfs-frigate", "/private/mqtt");
        return new BackupCycle(new BackupSettings(mounts, plan), commands, new FakeReader(), new FakeSpace(freeBytes));
    }

    private sealed class FakeSpace(long freeBytes) : IFreeSpaceReader
    {
        public long AvailableBytes(string path) => freeBytes;
    }

    private sealed class FakeReader : IMountUuidReader
    {
        public Task<string?> ReadUuidAsync(string mountPoint, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(mountPoint == "/mnt/backup4tb" ? "uuid-four" : "uuid-one");
    }

    private sealed class FakeCommands : IExternalCommandRunner
    {
        public string NextcloudMount { get; init; } = "nfs-nextcloud";
        public string ProxmoxFiles { get; init; } =
            "vzdump-qemu-100-2026.vma.zst\t100\t1790580000.0\n" +
            "vzdump-qemu-100-2026.vma.zst.notes\t20\t1790580000.0\n" +
            "vzdump-qemu-100-2026.log\t10\t1790580000.0\n" +
            "proxmox-host-config-2026.tar.gz\t100\t1790580000.0\n" +
            "lsblk-2026.txt\t20\t1790580000.0\n" +
            "pveversion-2026.txt\t20\t1790580000.0";
        public string Videos { get; init; } = "2026-09-28/09/balcon/17.58.mp4";
        public List<ExternalCommand> Executed { get; } = [];

        public async Task<int> RunAsync(ExternalCommand command, TextWriter output, CancellationToken cancellationToken = default)
        {
            Executed.Add(command);
            var text = command.Arguments.Last();
            if (text.Contains("findmnt -T '/mnt/cloud/nextcloud'")) await output.WriteLineAsync(NextcloudMount);
            if (text.Contains("findmnt -T '/mnt/cloud/immich'")) await output.WriteLineAsync("nfs-immich");
            if (text.Contains("findmnt -T '/mnt/cloud/frigate'")) await output.WriteLineAsync("nfs-frigate");
            if (text.Contains("find '/mnt/pve/nas-backup/dump'")) await output.WriteLineAsync(ProxmoxFiles);
            if (text.Contains("find '/mnt/cloud/frigate/recordings'")) await output.WriteLineAsync(Videos);
            return 0;
        }
    }
}
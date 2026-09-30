using System.Globalization;

namespace EdiHome.Backup;

public interface IFreeSpaceReader
{
    long AvailableBytes(string path);
}

public sealed class DriveFreeSpaceReader : IFreeSpaceReader
{
    public long AvailableBytes(string path) => new DriveInfo(path).AvailableFreeSpace;
}

public sealed class BackupCycle(BackupSettings settings, IExternalCommandRunner runner, IMountUuidReader mountReader,
    IFreeSpaceReader? freeSpaceReader = null, RecentFiles? recentFiles = null)
{
    private sealed record ProxmoxFile(string Name, long Size, long ModifiedSeconds);

    private readonly MountVerifier verifier = new(mountReader);
    private readonly IFreeSpaceReader freeSpace = freeSpaceReader ?? new DriveFreeSpaceReader();
    private readonly RecentFiles changes = recentFiles ?? new RecentFiles();
    private BackupPlan Plan => settings.RequireBackupPlan();

    public async Task RunAsync(bool apply, TextWriter output,
        Func<string, Task>? onComponentCompleted = null, CancellationToken cancellationToken = default,
        string? onlyComponent = null, bool allowPrune = true)
    {
        await CheckMountsAsync(cancellationToken);
        if (onlyComponent is null or "nextcloud")
            await VerifyRemoteMountAsync("/mnt/cloud/nextcloud", Plan.NextcloudMount, output, cancellationToken);
        if (onlyComponent is null or "immich")
            await VerifyRemoteMountAsync("/mnt/cloud/immich", Plan.ImmichMount, output, cancellationToken);
        if (onlyComponent is null or "surveillance")
            await VerifyRemoteMountAsync("/mnt/cloud/frigate", Plan.SurveillanceMount, output, cancellationToken);
        var proxmoxFiles = onlyComponent is null or "proxmox" ? await GetProxmoxFilesAsync(cancellationToken) : [];
        if (onlyComponent is null or "proxmox") CheckProxmoxSpace(proxmoxFiles);
        var videoFiles = onlyComponent is null or "surveillance" ? await GetSurveillanceFilesAsync(cancellationToken) : [];

        if (!apply)
        {
            await output.WriteLineAsync("Dry run: mount and source checks passed; no data was written.");
            await output.WriteLineAsync($"Planned: {onlyComponent ?? "Nextcloud, Immich, Proxmox and surveillance"}; {proxmoxFiles.Count} Proxmox files, {videoFiles.Count} closed surveillance videos, checks and retention.");
            return;
        }

        if (onlyComponent is null or "nextcloud")
        {
            await RunCloudBackupAsync(
            "nextcloud", Plan.NextcloudDumpScript, "/home/eduard/backups/nextcloud-db/",
            [
                ("/mnt/cloud/nextcloud/", "data", true, true),
                ($"{Plan.CloudDockerDir}/nextcloud/", "appdata", true, false),
                ($"{Plan.CloudDockerDir}/postgres/", "postgres", true, false)
            ], output, cancellationToken, allowPrune);
            if (onComponentCompleted is not null) await onComponentCompleted("nextcloud");
        }
        if (onlyComponent is null or "immich")
        {
            await RunCloudBackupAsync(
            "immich", Plan.ImmichDumpScript, "/home/eduard/backups/immich-db/",
            [
                ("/mnt/cloud/immich/", "data", true, false),
                ($"{Plan.CloudDockerDir}/immich/", "local", true, false),
                ($"{Plan.CloudDockerDir}/compose.yaml", "config", true, false),
                ($"{Plan.CloudDockerDir}/.env", "config", true, false)
            ], output, cancellationToken, allowPrune);
            if (onComponentCompleted is not null) await onComponentCompleted("immich");
        }
        if (onlyComponent is null or "proxmox")
        {
            await RunProxmoxBackupAsync(proxmoxFiles, output, cancellationToken, allowPrune);
            if (onComponentCompleted is not null) await onComponentCompleted("proxmox");
        }
        if (onlyComponent is null or "surveillance")
        {
            await RunSurveillanceBackupAsync(videoFiles, output, cancellationToken, allowPrune);
            if (onComponentCompleted is not null) await onComponentCompleted("surveillance");
        }
    }

    private async Task<IReadOnlyList<ProxmoxFile>> GetProxmoxFilesAsync(CancellationToken cancellationToken)
    {
        var directory = Plan.ProxmoxDumpDir.TrimEnd('/');
        var listing = await CaptureAsync(Ssh(Plan.Proxmox,
            $"find {Quote(directory)} -maxdepth 1 -type f -printf '%f\\t%s\\t%T@\\n'"), cancellationToken);
        var files = new Dictionary<string, ProxmoxFile>(StringComparer.Ordinal);
        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t', 3);
            if (parts.Length != 3 || !long.TryParse(parts[1], out var size) || size < 0 ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var modified) ||
                !double.IsFinite(modified) || modified < 0 || modified >= long.MaxValue || parts[0].Contains('/'))
                throw new InvalidOperationException("Invalid Proxmox file listing");
            files.Add(parts[0], new ProxmoxFile(parts[0], size, (long)modified));
        }
        var names = files.Keys.ToHashSet(StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vmId in Plan.VmIds)
        {
            var backups = names.Where(name => name.StartsWith($"vzdump-qemu-{vmId}-", StringComparison.Ordinal) &&
                name.EndsWith(".vma.zst", StringComparison.Ordinal)).Order(StringComparer.Ordinal).TakeLast(2).ToArray();
            if (backups.Length == 0) throw new InvalidOperationException($"No Proxmox backup for VM {vmId}; staging and retention untouched");
            foreach (var backup in backups)
            {
                selected.Add(backup);
                var stem = backup[..^".vma.zst".Length];
                foreach (var suffix in new[] { ".vma.zst.notes", ".log" })
                {
                    if (names.Contains(stem + suffix)) selected.Add(stem + suffix);
                }
            }
        }

        var hostConfigs = names.Where(name => name.StartsWith("proxmox-host-config-", StringComparison.Ordinal) &&
            name.EndsWith(".tar.gz", StringComparison.Ordinal)).Order(StringComparer.Ordinal).TakeLast(2).ToArray();
        if (hostConfigs.Length == 0) throw new InvalidOperationException("Proxmox host configuration missing; staging and retention untouched");
        selected.UnionWith(hostConfigs);
        foreach (var prefix in new[] { "dpkg-selections-", "pveversion-", "lsblk-", "ip-addr-", "ip-route-" })
        {
            selected.UnionWith(names.Where(name => name.StartsWith(prefix, StringComparison.Ordinal) &&
                name.EndsWith(".txt", StringComparison.Ordinal)).Order(StringComparer.Ordinal).TakeLast(2));
        }
        return selected.Order(StringComparer.Ordinal).Select(name => files[name]).ToArray();
    }

    private async Task<IReadOnlyList<string>> GetSurveillanceFilesAsync(CancellationToken cancellationToken)
    {
        var directory = Plan.SurveillanceSource.TrimEnd('/');
        var listing = await CaptureAsync(Ssh(Plan.Cloudapps,
            $"find {Quote(directory)} -type f -name '*.mp4' -mmin +2 -mtime -15 -printf '%P\\n'"), cancellationToken);
        var selected = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (selected.Length == 0 || selected.Any(path => path.StartsWith('/') || path.Split('/').Contains("..") || !path.EndsWith(".mp4", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("No closed surveillance videos or invalid source paths; existing backup untouched");
        }
        return selected;
    }

    private async Task RunProxmoxBackupAsync(IReadOnlyList<ProxmoxFile> files, TextWriter output,
        CancellationToken cancellationToken, bool allowPrune)
    {
        var stage = Path.Combine(Plan.OldBase, "staging-proxmox");
        var repository = Path.Combine(Plan.OldBase, "restic-proxmox-vm");
        if (!File.Exists(Path.Combine(repository, "config"))) throw new InvalidOperationException("Proxmox restic repository missing");

        CheckProxmoxSpace(files);

        foreach (var file in files)
        {
            var destination = Path.Combine(stage, file.Name.StartsWith("vzdump-qemu-", StringComparison.Ordinal) ? "vm-backups" : "host-config");
            await CheckMountsAsync(cancellationToken);
            Directory.CreateDirectory(destination);
            await ExecuteAsync($"Proxmox {file.Name}", Rsync(Plan.Proxmox, $"{Plan.ProxmoxDumpDir.TrimEnd('/')}/{file.Name}",
                destination + "/", false, false), output, cancellationToken);
            if (file.Name.StartsWith("vzdump-qemu-", StringComparison.Ordinal) && file.Name.EndsWith(".vma.zst", StringComparison.Ordinal))
            {
                var vmId = file.Name.Split('-')[2];
                changes.Add($"VM BACKUP {vmId}: {file.Name}");
            }
            if (file.Name.StartsWith("proxmox-host-config-", StringComparison.Ordinal))
                changes.Add($"HOST CONFIG: {file.Name}");
        }
        await CheckMountsAsync(cancellationToken);
        var selected = files.Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var directory in new[] { Path.Combine(stage, "vm-backups"), Path.Combine(stage, "host-config") })
        {
            foreach (var localFile in Directory.GetFiles(directory))
            {
                var name = Path.GetFileName(localFile);
                var tracked = name.StartsWith("vzdump-qemu-", StringComparison.Ordinal) ||
                    new[] { "proxmox-host-config-", "dpkg-selections-", "pveversion-", "lsblk-", "ip-addr-", "ip-route-" }
                        .Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
                if (tracked && !selected.Contains(name)) File.Delete(localFile);
            }
        }
        await SnapshotAsync("proxmox-vm", repository, stage, "--keep-last", "2", output, cancellationToken, allowPrune);
    }

    private void CheckProxmoxSpace(IReadOnlyList<ProxmoxFile> files)
    {
        var stage = Path.Combine(Plan.OldBase, "staging-proxmox");
        var needed = files.Sum(file =>
        {
            var directory = Path.Combine(stage, file.Name.StartsWith("vzdump-qemu-", StringComparison.Ordinal) ? "vm-backups" : "host-config");
            var local = Path.Combine(directory, file.Name);
            if (!File.Exists(local)) return file.Size;
            var existing = new FileInfo(local);
            return existing.Length == file.Size && new DateTimeOffset(existing.LastWriteTimeUtc).ToUnixTimeSeconds() == file.ModifiedSeconds
                ? 0L : file.Size;
        });
        var repositoryGrowth = files.Sum(file => file.Size);
        const long reserve = 10L * 1024 * 1024 * 1024;
        var available = freeSpace.AvailableBytes(Plan.OldBase);
        var required = checked(needed + repositoryGrowth + reserve);
        if (required > available)
            throw new IOException($"Proxmox needs up to {required} bytes (staging {needed}, repository {repositoryGrowth}, 10 GiB reserve); available {available}, shortfall {required - available}. Staging untouched.");
    }

    private async Task RunSurveillanceBackupAsync(IReadOnlyList<string> files, TextWriter output,
        CancellationToken cancellationToken, bool allowPrune)
    {
        var stage = Path.Combine(Plan.NewBase, "staging-surveillance");
        var destination = Path.Combine(stage, "recordings");
        var repository = Path.Combine(Plan.NewBase, "restic-surveillance");
        await VerifyRemoteMountAsync("/mnt/cloud/frigate", Plan.SurveillanceMount, output, cancellationToken);
        await CheckMountsAsync(cancellationToken);
        Directory.CreateDirectory(destination);
        var fileList = string.Join('\0', files) + '\0';
        var transfer = Rsync(Plan.Cloudapps, Plan.SurveillanceSource.TrimEnd('/') + '/', destination + '/', false, false);
        var args = transfer.Arguments.ToList();
        args.Insert(0, "--files-from=-");
        args.Insert(0, "--from0");
        args.Remove("--delete-delay");
        await ExecuteAsync("surveillance transfer", transfer with { Arguments = args, StandardInput = fileList }, output, cancellationToken);
        changes.Add($"SURVEILLANCE SUMMARY: segments={files.Count}");

        await CheckMountsAsync(cancellationToken);
        var selected = files.ToHashSet(StringComparer.Ordinal);
        foreach (var localFile in Directory.EnumerateFiles(destination, "*.mp4", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(destination, localFile).Replace('\\', '/');
            if (!selected.Contains(relative)) File.Delete(localFile);
        }

        if (!File.Exists(Path.Combine(repository, "config")))
        {
            await CheckMountsAsync(cancellationToken);
            Directory.CreateDirectory(repository);
            await ExecuteAsync("surveillance repo init", new ExternalCommand("restic", ["init"],
                Environment: ResticEnvironment(repository)), output, cancellationToken);
        }
        await SnapshotAsync("surveillance", repository, stage, "--keep-weekly", "8", output, cancellationToken, allowPrune);
    }

    private async Task RunCloudBackupAsync(
        string name, string dumpScript, string remoteDumpDir,
        IReadOnlyList<(string Source, string Destination, bool Privileged, bool Excludes)> sources,
        TextWriter output, CancellationToken cancellationToken, bool allowPrune)
    {
        var stage = Path.Combine(Plan.NewBase, $"staging-{name}");
        var repo = Path.Combine(Plan.NewBase, $"restic-{name}");
        var immichCounts = new Dictionary<string, int> { ["added"] = 0, ["changed"] = 0, ["deleted"] = 0 };
        if (!File.Exists(Path.Combine(repo, "config")))
        {
            throw new InvalidOperationException($"Existing restic repository missing: {repo}");
        }

        await ExecuteAsync($"{name} DB dump", Ssh(Plan.Cloudapps, Quote(dumpScript), TimeSpan.FromHours(1)), output, cancellationToken);
        var dumpDir = Path.Combine(stage, "dbdump");
        await CheckMountsAsync(cancellationToken);
        Directory.CreateDirectory(dumpDir);
        await ExecuteAsync($"{name} database transfer", Rsync(Plan.Cloudapps, remoteDumpDir, dumpDir + "/", false, false), output, cancellationToken);
        var dumps = Directory.GetFiles(dumpDir, "*.gz");
        if (dumps.Length == 0)
        {
            throw new InvalidOperationException($"No database dump in {dumpDir}");
        }
        await ExecuteAsync($"{name} DB integrity", new ExternalCommand("gzip", ["-t", .. dumps]), output, cancellationToken);

        foreach (var source in sources)
        {
            if (source.Source == "/mnt/cloud/nextcloud/")
                await VerifyRemoteMountAsync("/mnt/cloud/nextcloud", Plan.NextcloudMount, output, cancellationToken);
            if (source.Source == "/mnt/cloud/immich/")
                await VerifyRemoteMountAsync("/mnt/cloud/immich", Plan.ImmichMount, output, cancellationToken);
            await CheckMountsAsync(cancellationToken);
            var destination = Path.Combine(stage, source.Destination);
            Directory.CreateDirectory(destination);
            var track = name == "immich" || name == "nextcloud" && source.Destination == "data";
            var transferOutput = name == "immich" ? changes.CaptureImmich(output, immichCounts) :
                track ? changes.CaptureNextcloud(output) : output;
            await ExecuteAsync($"{name} {source.Destination} transfer",
                Rsync(Plan.Cloudapps, source.Source, destination + "/", source.Privileged, source.Excludes, track),
                transferOutput, cancellationToken);
        }

        if (name == "immich") changes.Add($"IMMICH SUMMARY: added={immichCounts["added"]} changed={immichCounts["changed"]} deleted={immichCounts["deleted"]}");

        await SnapshotAsync(name, repo, stage, "--keep-weekly", "8", output, cancellationToken, allowPrune);
    }

    private ExternalCommand Rsync(RemoteHost host, string source, string destination, bool privileged, bool excludes, bool itemize = false)
    {
        var args = new List<string> { "-a", "--no-owner", "--no-group", "--no-perms", "--partial" };
        if (source.EndsWith('/')) args.Add("--delete-delay");
        if (privileged) args.Add("--rsync-path=sudo -n rsync");
        if (excludes) args.Add($"--exclude-from={Plan.NextcloudExcludesFile}");
        if (itemize) args.Add("--itemize-changes");
        args.AddRange(["-e", SshTransport(host), $"{host.Target}:{source}", destination]);
        return new ExternalCommand("rsync", args);
    }

    private async Task SnapshotAsync(string name, string repo, string stage, string retention, string count,
        TextWriter output, CancellationToken cancellationToken, bool allowPrune)
    {
        var environment = ResticEnvironment(repo);
        await ExecuteAsync($"{name} snapshot", new ExternalCommand("restic", ["backup", stage, "--tag", name, "--tag", "weekly"], Environment: environment), output, cancellationToken);
        await ExecuteAsync($"{name} repository check", new ExternalCommand("restic", ["check"], Environment: environment), output, cancellationToken);
        if (allowPrune)
        {
            var arguments = new List<string> { "forget", retention, count };
            if (retention == "--keep-weekly") arguments.AddRange(["--group-by", "host,tags"]);
            arguments.Add("--prune");
            await ExecuteAsync($"{name} retention", new ExternalCommand("restic", arguments, Environment: environment), output, cancellationToken);
        }
        else
            await output.WriteLineAsync($"Skipped {name} retention for this manual test");
    }

    private Dictionary<string, string> ResticEnvironment(string repo) => new()
    {
        ["RESTIC_REPOSITORY"] = repo,
        ["RESTIC_PASSWORD_FILE"] = Plan.ResticPasswordFile
    };

    private async Task VerifyRemoteMountAsync(string path, string expectedSource, TextWriter output, CancellationToken cancellationToken)
    {
        var result = await CaptureAsync(Ssh(Plan.Cloudapps, $"findmnt -T {Quote(path)} -t nfs4 -n -o SOURCE"), cancellationToken);
        if (!string.Equals(result, expectedSource, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected NFS source {expectedSource} at {path}, found {result}");
        }
        await output.WriteLineAsync($"Verified NFS mount {path}");
    }

    private async Task<string> CaptureAsync(ExternalCommand command, CancellationToken cancellationToken)
    {
        await CheckMountsAsync(cancellationToken);
        using var buffer = new StringWriter();
        var exitCode = await runner.RunAsync(command, buffer, cancellationToken);
        if (exitCode != 0) throw new InvalidOperationException($"{command.Executable} exited {exitCode}: {buffer}");
        return buffer.ToString().Trim();
    }

    private async Task ExecuteAsync(string name, ExternalCommand command, TextWriter output, CancellationToken cancellationToken)
    {
        var result = await new BackupWorkflow(verifier, runner).RunAsync(settings.Mounts,
            [new BackupStep(name, command)], output, cancellationToken);
        if (result != 0) throw new InvalidOperationException($"{name} failed (exit {result}); later steps were skipped");
    }

    private async Task CheckMountsAsync(CancellationToken cancellationToken)
    {
        var errors = await verifier.CheckAsync(settings.Mounts, cancellationToken);
        if (errors.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
    }

    private static ExternalCommand Ssh(RemoteHost host, string remoteCommand, TimeSpan? timeout = null) =>
        new("ssh", ["-i", host.KeyFile, "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new",
            "-o", "ConnectTimeout=10", host.Target, remoteCommand], timeout ?? TimeSpan.FromMinutes(2));

    private static string SshTransport(RemoteHost host) =>
        $"ssh -i {Quote(host.KeyFile)} -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=10";

    private static string Quote(string text) => $"'{text.Replace("'", "'\\''", StringComparison.Ordinal)}'";
}
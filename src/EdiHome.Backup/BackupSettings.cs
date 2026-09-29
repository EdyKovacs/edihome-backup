using System.Text.Json;

namespace EdiHome.Backup;

public sealed record RemoteHost(string Target, string KeyFile);

public sealed record BackupPlan(
    string OldBase,
    string NewBase,
    string ResticPasswordFile,
    string NextcloudExcludesFile,
    RemoteHost Cloudapps,
    RemoteHost Proxmox,
    string CloudDockerDir,
    string NextcloudDumpScript,
    string ImmichDumpScript,
    string NextcloudMount,
    string ImmichMount,
    string ProxmoxDumpDir,
    int[] VmIds,
    string SurveillanceSource,
    string SurveillanceMount,
    string MqttEnvFile);

public sealed record BackupSettings(IReadOnlyList<MountRequirement> Mounts, BackupPlan? Backup = null)
{
    public static BackupSettings Load(string path)
    {
        var settings = JsonSerializer.Deserialize<BackupSettings>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (settings?.Mounts is not { Count: > 0 } ||
            settings.Mounts.Any(mount =>
                string.IsNullOrWhiteSpace(mount.Name) ||
                string.IsNullOrWhiteSpace(mount.MountPoint) ||
                string.IsNullOrWhiteSpace(mount.Uuid)))
        {
            throw new InvalidDataException("Mounts must contain a name, mount point and UUID.");
        }

        if (settings.Mounts.Select(mount => mount.MountPoint).Distinct(StringComparer.Ordinal).Count() != settings.Mounts.Count)
        {
            throw new InvalidDataException("Mount points must be unique.");
        }

        return settings;
    }

    public BackupPlan RequireBackupPlan()
    {
        if (Backup is not { } plan ||
            new[] { plan.OldBase, plan.NewBase, plan.ResticPasswordFile, plan.NextcloudExcludesFile,
                plan.CloudDockerDir, plan.NextcloudDumpScript, plan.ImmichDumpScript,
                plan.NextcloudMount, plan.ImmichMount,
                plan.ProxmoxDumpDir, plan.SurveillanceSource, plan.SurveillanceMount,
                plan.MqttEnvFile, plan.Cloudapps?.Target, plan.Cloudapps?.KeyFile,
                plan.Proxmox?.Target, plan.Proxmox?.KeyFile }.Any(string.IsNullOrWhiteSpace) ||
            plan.VmIds is not { Length: > 0 } || plan.VmIds.Any(id => id <= 0) ||
            !Mounts.Any(mount => IsUnderMount(plan.OldBase, mount.MountPoint)) ||
            !Mounts.Any(mount => IsUnderMount(plan.NewBase, mount.MountPoint)) ||
            plan.OldBase == plan.NewBase)
        {
            throw new InvalidDataException("Backup plan must define distinct mounted destinations, remote hosts, VM IDs and required paths.");
        }

        return plan;
    }

    private static bool IsUnderMount(string path, string mountPoint) =>
        path.StartsWith(mountPoint.TrimEnd('/') + '/', StringComparison.Ordinal);
}
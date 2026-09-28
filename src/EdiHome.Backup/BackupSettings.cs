using System.Text.Json;

namespace EdiHome.Backup;

public sealed record BackupSettings(IReadOnlyList<MountRequirement> Mounts)
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
}
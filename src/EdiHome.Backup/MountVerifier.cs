namespace EdiHome.Backup;

public sealed record MountRequirement(string Name, string MountPoint, string Uuid);

public interface IMountUuidReader
{
    Task<string?> ReadUuidAsync(string mountPoint, CancellationToken cancellationToken);
}

public sealed class MountVerifier(IMountUuidReader reader)
{
    public async Task<IReadOnlyList<string>> CheckAsync(
        IEnumerable<MountRequirement> mounts,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        foreach (var mount in mounts)
        {
            var actualUuid = await reader.ReadUuidAsync(mount.MountPoint, cancellationToken);
            if (!string.Equals(actualUuid, mount.Uuid, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{mount.Name}: expected UUID {mount.Uuid} at {mount.MountPoint}, found {actualUuid ?? "no mount"}");
            }
        }

        return errors;
    }
}
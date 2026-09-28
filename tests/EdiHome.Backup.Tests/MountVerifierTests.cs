using EdiHome.Backup;

namespace EdiHome.Backup.Tests;

public class MountVerifierTests
{
    [Fact]
    public async Task AcceptsMatchingMounts()
    {
        var reader = new FakeReader(new Dictionary<string, string>
        {
            ["/media/edi/data"] = "1834C93C-9B86-4D86-87A4-19B82E0532F8",
            ["/mnt/backup4tb"] = "32b60a5c-3f9c-40d6-912d-e991b2e81668"
        });

        var errors = await new MountVerifier(reader).CheckAsync(RequiredMounts());

        Assert.Empty(errors);
    }

    [Fact]
    public async Task RejectsMissingMount()
    {
        var errors = await new MountVerifier(new FakeReader(new Dictionary<string, string>()))
            .CheckAsync(RequiredMounts());

        Assert.Equal(2, errors.Count);
        Assert.Contains("no mount", errors[0]);
    }

    [Fact]
    public async Task RejectsWrongDiskAtExpectedPath()
    {
        var reader = new FakeReader(new Dictionary<string, string>
        {
            ["/media/edi/data"] = "1834c93c-9b86-4d86-87a4-19b82e0532f8",
            ["/mnt/backup4tb"] = "different-disk"
        });

        var errors = await new MountVerifier(reader).CheckAsync(RequiredMounts());

        Assert.Single(errors);
        Assert.Contains("4 TB", errors[0]);
        Assert.Contains("different-disk", errors[0]);
    }

    private static MountRequirement[] RequiredMounts() =>
    [
        new("1 TB", "/media/edi/data", "1834c93c-9b86-4d86-87a4-19b82e0532f8"),
        new("4 TB", "/mnt/backup4tb", "32b60a5c-3f9c-40d6-912d-e991b2e81668")
    ];

    private sealed class FakeReader(IReadOnlyDictionary<string, string> mounts) : IMountUuidReader
    {
        public Task<string?> ReadUuidAsync(string mountPoint, CancellationToken cancellationToken) =>
            Task.FromResult(mounts.GetValueOrDefault(mountPoint));
    }
}
using EdiHome.Backup;

namespace EdiHome.Backup.Tests;

public class BackupWorkflowTests
{
    [Fact]
    public async Task RefusesToStartOnWrongDisk()
    {
        var runner = new FakeRunner([0]);
        var workflow = new BackupWorkflow(new MountVerifier(new FakeReader(["other-disk"])), runner);

        var result = await workflow.RunAsync(RequiredMounts(), Steps("backup"), TextWriter.Null);

        Assert.Equal(1, result);
        Assert.Empty(runner.Executed);
    }

    [Fact]
    public async Task ChecksMountAgainBeforeNextStage()
    {
        var runner = new FakeRunner([0, 0]);
        var workflow = new BackupWorkflow(new MountVerifier(new FakeReader([ExpectedUuid, "other-disk"])), runner);

        var result = await workflow.RunAsync(RequiredMounts(), Steps("backup", "prune"), TextWriter.Null);

        Assert.Equal(1, result);
        Assert.Equal(["backup"], runner.Executed);
    }

    [Fact]
    public async Task DoesNotPruneAfterBackupFailure()
    {
        var runner = new FakeRunner([0, 23, 0]);
        var workflow = new BackupWorkflow(new MountVerifier(new FakeReader([ExpectedUuid, ExpectedUuid])), runner);

        var result = await workflow.RunAsync(RequiredMounts(), Steps("sync", "backup", "prune"), TextWriter.Null);

        Assert.Equal(23, result);
        Assert.Equal(["sync", "backup"], runner.Executed);
    }

    [Fact]
    public async Task RunsAllStepsWhenTheySucceed()
    {
        var runner = new FakeRunner([0, 0]);
        var workflow = new BackupWorkflow(new MountVerifier(new FakeReader([ExpectedUuid, ExpectedUuid])), runner);

        var result = await workflow.RunAsync(RequiredMounts(), Steps("sync", "backup"), TextWriter.Null);

        Assert.Equal(0, result);
        Assert.Equal(["sync", "backup"], runner.Executed);
    }

    private const string ExpectedUuid = "32b60a5c-3f9c-40d6-912d-e991b2e81668";

    private static MountRequirement[] RequiredMounts() => [new("4 TB", "/mnt/backup4tb", ExpectedUuid)];

    private static BackupStep[] Steps(params string[] names) =>
        names.Select(name => new BackupStep(name, new ExternalCommand(name, []))).ToArray();

    private sealed class FakeReader(IEnumerable<string> uuids) : IMountUuidReader
    {
        private readonly IEnumerator<string> responses = uuids.GetEnumerator();

        public Task<string?> ReadUuidAsync(string mountPoint, CancellationToken cancellationToken)
        {
            responses.MoveNext();
            return Task.FromResult<string?>(responses.Current);
        }
    }

    private sealed class FakeRunner(IEnumerable<int> exitCodes) : IExternalCommandRunner
    {
        private readonly IEnumerator<int> responses = exitCodes.GetEnumerator();

        public List<string> Executed { get; } = [];

        public Task<int> RunAsync(ExternalCommand command, TextWriter output, CancellationToken cancellationToken = default)
        {
            Executed.Add(command.Executable);
            responses.MoveNext();
            return Task.FromResult(responses.Current);
        }
    }
}
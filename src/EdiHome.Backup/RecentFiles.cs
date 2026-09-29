using System.Text;

namespace EdiHome.Backup;

public sealed class RecentFiles
{
    private readonly List<string> entries = [];

    public IReadOnlyList<string> Entries => entries.TakeLast(40).ToArray();

    public void Add(string entry)
    {
        entries.Remove(entry);
        entries.Add(entry);
    }

    public TextWriter CaptureNextcloud(TextWriter output) => new ItemizedWriter(output, line =>
    {
        var separator = line.IndexOf(' ');
        if (separator < 0) return;
        var code = line[..separator];
        var path = line[(separator + 1)..].Trim();
        if (!path.Contains("/files/", StringComparison.Ordinal) &&
            !path.Contains("/files_trashbin/", StringComparison.Ordinal) &&
            !path.Contains("/files_versions/", StringComparison.Ordinal)) return;
        if (path.EndsWith("nextcloud.log", StringComparison.Ordinal) ||
            path.Contains("/preview/", StringComparison.Ordinal) ||
            path.Contains("/appdata_", StringComparison.Ordinal) ||
            path.Contains("/richdocuments/", StringComparison.Ordinal)) return;
        var action = code == "*deleting" ? "DELETED" : code.StartsWith(">f+++++++++", StringComparison.Ordinal) ? "ADDED" :
            code.StartsWith(">f", StringComparison.Ordinal) ? "CHANGED" : null;
        if (action is not null) Add($"NEXTCLOUD {action}: {path}");
    });

    public TextWriter CaptureImmich(TextWriter output, IDictionary<string, int> counts) => new ItemizedWriter(output, line =>
    {
        var separator = line.IndexOf(' ');
        if (separator < 0) return;
        var code = line[..separator];
        var path = "/" + line[(separator + 1)..].TrimStart('/');
        if (new[] { "/backups/", "/encoded-video/", "/model-cache/", "/postgres/", "/thumbs/" }
            .Any(part => path.Contains(part, StringComparison.Ordinal)) ||
            path is "/.env" or "/compose.yaml") return;
        var action = code == "*deleting" ? "deleted" : code.StartsWith(">f+++++++++", StringComparison.Ordinal) ? "added" :
            code.StartsWith(">f", StringComparison.Ordinal) ? "changed" : null;
        if (action is not null) counts[action]++;
    });

    private sealed class ItemizedWriter(TextWriter output, Action<string> onLine) : TextWriter
    {
        public override Encoding Encoding => output.Encoding;

        public override void WriteLine(string? value)
        {
            if (value is not null) onLine(value);
            output.WriteLine(value);
        }

        public override void WriteLine(ReadOnlySpan<char> buffer)
        {
            onLine(buffer.ToString());
            output.WriteLine(buffer);
        }

        public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            var line = buffer.ToString();
            onLine(line);
            await output.WriteLineAsync(buffer, cancellationToken);
        }
    }
}
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PixMcp.Pix;

public sealed record PixDiffInfoDto(bool Available, string? Executable, string? DiscoveredVia, string? Error);

/// <summary>Optional helper discovery does not execute the helper or initialize PIX.</summary>
public static class PixDiffDiscovery
{
    public static PixDiffInfoDto Info() => Find(Environment.GetEnvironmentVariable("PIXMCP_PIXDIFF_PATH"),
        AppContext.BaseDirectory, Environment.GetEnvironmentVariable("PATH"));

    internal static PixDiffInfoDto Find(string? configured, string baseDirectory, string? searchPath)
    {
        if (configured is not null)
        {
            if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathFullyQualified(configured) || !File.Exists(configured)
                || !Path.GetExtension(configured).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                return new(false, null, "PIXMCP_PIXDIFF_PATH", "PIXMCP_PIXDIFF_PATH must name an existing absolute path to pixdiff.exe. Correct or unset the override.");
            return new(true, Path.GetFullPath(configured), "PIXMCP_PIXDIFF_PATH", null);
        }
        string adjacent = Path.Combine(baseDirectory, "pixdiff.exe");
        if (File.Exists(adjacent)) return new(true, Path.GetFullPath(adjacent), "server directory", null);
        foreach (string entry in (searchPath ?? "").Split(Path.PathSeparator))
        {
            string directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            try
            {
                string candidate = Path.Combine(directory, "pixdiff.exe");
                if (File.Exists(candidate)) return new(true, Path.GetFullPath(candidate), "PATH", null);
            }
            catch (ArgumentException) { }
        }
        return new(false, null, null, "pixdiff.exe was not found. Build pix_tutorial and configure PIXMCP_PIXDIFF_PATH, place the helper beside the server, or add its directory to PATH.");
    }
}

public sealed record CsvSourceDto(string Path, long Frames);
public sealed record CsvPassDto(string Name, double? BaselineMs, double? CandidateMs, double? DeltaMs, double? DeltaPercent,
    bool BaselinePresent, bool CandidatePresent, long BaselineSamples, long CandidateSamples);
public sealed record CsvComparisonDto(string Kind, int SchemaVersion, string Source, string Stat, string Prefix, string Units,
    string DeltaConvention, CsvSourceDto Baseline, CsvSourceDto Candidate, CsvPassDto? Total,
    IReadOnlyList<CsvPassDto> Items, IReadOnlyList<CsvPassDto> Missing);
public sealed record CsvPassCandidateDto(EventDto Event, string MatchKind, IReadOnlyList<ToolCallDto> NextCalls);
public sealed record CsvPassCandidatesDto(string ResultRef, string PassName, string Handle, string SearchText,
    string Evidence, bool IdentityEstablished, CsvPassDto Pass, int Total, int Offset, int Count, int? NextOffset,
    IReadOnlyList<CsvPassCandidateDto> Items, string? UnavailableReason, IReadOnlyList<ToolCallDto> NextCalls);

internal static class CsvComparison
{
    internal const int MaxOutputBytes = 32 * 1024 * 1024;
    private const int MaxDiagnosticChars = 4096;
    private static readonly JsonSerializerOptions ProtocolOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.Strict,
        RespectRequiredConstructorParameters = true,
    };

    private sealed record HelperRow(string Name, double? OursMs, double? TheirsMs, double? DeltaMs, double? DeltaPercent,
        bool OursPresent, bool TheirsPresent, long OursSamples, long TheirsSamples);
    private sealed record HelperReport(string Kind, int SchemaVersion, string Stat, string Prefix, string Units,
        string DeltaConvention, CsvSourceDto Ours, CsvSourceDto Theirs, HelperRow? Total,
        HelperRow[] Rows, HelperRow[] Missing);

    internal static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new PixToolException(PixErrors.Codes.InvalidArguments, "CSV paths must not be empty.");
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) throw new PixToolException(PixErrors.Codes.CsvFileNotFound, $"CSV file does not exist: {fullPath}");
            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new PixToolException(PixErrors.Codes.InvalidArguments, "Invalid CSV path: " + ex.Message); }
    }

    internal static ProcessStartInfo BuildStartInfo(string executable, string baseline, string candidate, string prefix, string stat)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in new[] { candidate, baseline, "--format=json", "--prefix", prefix, "--stat", stat })
            start.ArgumentList.Add(argument);
        return start;
    }

    internal static async Task<string> RunProcess(ProcessStartInfo start, TimeSpan timeout, CancellationToken cancellation,
        Action<string> diagnostic, int maxOutputBytes = MaxOutputBytes)
    {
        using var process = new Process { StartInfo = start };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        cancellation.ThrowIfCancellationRequested();
        try
        {
            if (!process.Start()) throw new PixToolException(PixErrors.Codes.PixdiffStartFailed, "Could not start pixdiff.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        { throw new PixToolException(PixErrors.Codes.PixdiffStartFailed, "Could not start pixdiff: " + ex.Message); }

        // Always drain both pipes. Oversized stdout is discarded after the cap, then rejected in full.
        Task<(byte[] Bytes, bool Truncated)> stdout = DrainOutput(process.StandardOutput.BaseStream, maxOutputBytes);
        Task<string> stderr = DrainDiagnostic(process.StandardError);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync().ConfigureAwait(false);
            // A descendant may hold an inherited pipe open after its parent exits.
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
            await ObservePipes(stdout, stderr).ConfigureAwait(false);
            if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
            throw new PixToolException(PixErrors.Codes.PixdiffTimeout, "pixdiff exceeded the comparison timeout and was terminated.", true);
        }
        string messages = await stderr.ConfigureAwait(false);
        diagnostic(messages);
        if (process.ExitCode != 0)
            throw new PixToolException(PixErrors.Codes.PixdiffFailed, $"pixdiff exited with code {process.ExitCode}; see bounded job diagnostics.");
        (byte[] bytes, bool truncated) = await stdout.ConfigureAwait(false);
        if (truncated) throw new PixToolException(PixErrors.Codes.PixdiffOutputTooLarge, $"pixdiff JSON exceeds the {maxOutputBytes} byte limit; use a narrower prefix.");
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw ProtocolError("pixdiff output is not UTF-8 JSON."); }
    }

    private static async Task ObservePipes(Task stdout, Task stderr)
    {
        try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private static async Task<(byte[], bool)> DrainOutput(Stream stream, int maximum)
    {
        using var retained = new MemoryStream();
        byte[] buffer = new byte[8192];
        bool truncated = false;
        int count;
        while ((count = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            int keep = Math.Min(count, maximum - (int)retained.Length);
            if (keep > 0) retained.Write(buffer, 0, keep);
            truncated |= keep < count;
        }
        return (retained.ToArray(), truncated);
    }

    private static async Task<string> DrainDiagnostic(StreamReader reader)
    {
        var text = new StringBuilder();
        char[] buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            if (text.Length < MaxDiagnosticChars) text.Append(buffer, 0, Math.Min(count, MaxDiagnosticChars - text.Length));
        return text.ToString();
    }

    internal static CsvComparisonDto Parse(string json, string baselinePath, string candidatePath, string prefix, string stat)
    {
        HelperReport report;
        try { report = JsonSerializer.Deserialize<HelperReport>(json, ProtocolOptions) ?? throw new JsonException("Empty report."); }
        catch (JsonException ex) { throw ProtocolError("pixdiff did not produce the supported complete JSON report: " + ex.Message); }
        if (report.Kind != "pixdiff" || report.SchemaVersion != 1 || report.Units != "ms" || report.DeltaConvention != "oursMinusTheirs"
            || report.Stat != stat || report.Prefix != prefix || report.Ours is null || report.Theirs is null
            || report.Rows is null || report.Missing is null || report.Ours.Frames < 0 || report.Theirs.Frames < 0
            || !string.Equals(report.Ours.Path, candidatePath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(report.Theirs.Path, baselinePath, StringComparison.OrdinalIgnoreCase))
            throw ProtocolError("pixdiff report version, provenance, or comparison options do not match the request.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        CsvPassDto Row(HelperRow row, bool missing)
        {
            if (row is null || string.IsNullOrWhiteSpace(row.Name) || !row.Name.StartsWith(prefix, StringComparison.Ordinal)
                || !names.Add(row.Name) || row.OursSamples < 0 || row.TheirsSamples < 0
                || row.OursSamples > report.Ours.Frames || row.TheirsSamples > report.Theirs.Frames
                || (!row.OursPresent && row.OursSamples != 0) || (!row.TheirsPresent && row.TheirsSamples != 0)
                || (row.OursSamples > 0) != row.OursMs.HasValue || (row.TheirsSamples > 0) != row.TheirsMs.HasValue
                || (!row.OursMs.HasValue || !row.TheirsMs.HasValue) != missing
                || new[] { row.OursMs, row.TheirsMs, row.DeltaMs, row.DeltaPercent }.Any(n => n.HasValue && !double.IsFinite(n.Value)))
                throw ProtocolError("pixdiff report contains an invalid or duplicate pass row.");
            double? delta = row.OursMs - row.TheirsMs;
            double? percentage = row.TheirsMs == 0 ? null : delta / row.TheirsMs * 100;
            if (row.DeltaMs != delta || row.DeltaPercent != percentage)
                throw ProtocolError($"pixdiff delta semantics are invalid for '{row.Name}'.");
            return new(row.Name, row.TheirsMs, row.OursMs, row.DeltaMs, row.DeltaPercent,
                row.TheirsPresent, row.OursPresent, row.TheirsSamples, row.OursSamples);
        }
        if (report.Total is not null && report.Total.Name != prefix + "Total")
            throw ProtocolError("pixdiff total row does not match the requested prefix.");
        CsvPassDto? total = report.Total is null ? null : Row(report.Total, false);
        CsvPassDto[] items = report.Rows.Select(r => Row(r, false)).ToArray();
        CsvPassDto[] missing = report.Missing.Select(r => Row(r, true)).ToArray();
        return new("csvComparison", 1, "ueCsvProfiler", stat, prefix, "ms", "candidateMinusBaseline",
            report.Theirs, report.Ours, total, items, missing);
    }

    internal static (string Prefix, CsvPassDto Pass) ReadPass(ResultStore store, string resultRef, string passName, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(passName)) throw new PixToolException(PixErrors.Codes.InvalidArguments, "passName must be an exact CSV column name returned by pix_csv_compare.");
        using ResultStore.Lease lease = store.Acquire(resultRef);
        using Stream stream = lease.Open();
        var json = new StoredJson(stream, cancellation);
        JsonElement Property(string pointer) => json.Element(json.Locate(pointer), MaxOutputBytes, resultRef, pointer);
        try
        {
            if (Property("/kind").GetString() != "csvComparison" || Property("/schemaVersion").GetInt32() != 1)
                throw new JsonException();
            string prefix = Property("/prefix").GetString() ?? throw new JsonException();
            // Read only individual pass rows from the retained snapshot, never the original CSV files.
            CsvPassDto? Find(string pointer)
            {
                var array = json.Locate(pointer);
                if (array.Kind != JsonValueKind.Array) throw new JsonException();
                foreach (var child in json.Children(array))
                {
                    cancellation.ThrowIfCancellationRequested();
                    JsonElement item = json.Element(child.Value, MaxOutputBytes, resultRef, pointer + "/" + child.Key);
                    if (item.GetProperty("name").GetString() == passName) return item.Deserialize<CsvPassDto>(Json.Options) ?? throw new JsonException();
                }
                return null;
            }
            CsvPassDto? pass = Find("/items") ?? Find("/missing");
            if (pass is null && passName == prefix + "Total")
            {
                try { pass = Property("/total").Deserialize<CsvPassDto>(Json.Options); }
                catch (PixToolException ex) when (ex.Detail.Code == "invalid_pointer") { }
            }
            if (pass is null) throw new PixToolException(PixErrors.Codes.CsvPassNotFound, $"'{passName}' is not present in this saved CSV comparison.");
            return (prefix, pass);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
            || ex is PixToolException p && p.Detail.Code == "invalid_pointer")
        { throw new PixToolException(PixErrors.Codes.InvalidArguments, "resultRef must identify a complete result returned by pix_csv_compare."); }
    }

    private static PixToolException ProtocolError(string message) => new("pixdiff_invalid_output", message);
}

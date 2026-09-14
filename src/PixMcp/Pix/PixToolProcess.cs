using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>Shared pixtool plumbing. Callers run the entire replay on the PIX worker.</summary>
internal static class PixToolProcess
{
    internal static void EnsureReplayAvailable(PixSession session)
    {
        string[] connected = session.Handles.OfType<GpuCaptureHandle>()
            .Where(h => h.AnalysisConnected || h.AnalysisStarted).Select(h => h.Id).ToArray();
        if (connected.Length > 0)
            throw new PixToolException(PixErrors.Codes.AnalysisActive, $"Stop connected GPU analyses before CLI replay: {string.Join(", ", connected)}.", true,
                connected.Select(id => new ToolCallDto("pix_gpu_analysis_stop", new { handle = id })).ToArray());
    }

    internal static string Executable(string operation, string? installDirectory)
    {
        string executable = Path.Combine(installDirectory ?? string.Empty, "pixtool.exe");
        if (!File.Exists(executable))
            throw new PixToolException(PixErrors.Codes.PixToolUnavailable, $"{operation}: the installed PIX runtime does not contain pixtool.exe.", false,
                [new ToolCallDto("pix_info", new { probe = true }, CostHints.Query)]);
        return executable;
    }

    internal static ProcessStartInfo StartInfo(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    internal static void CopyCapture(string sourcePath, string destinationPath, Action checkCancellation)
    {
        // PIX documents hold an exclusive engine lock on the original; pixtool opens its own copy.
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write);
        byte[] buffer = new byte[1024 * 1024];
        int copied;
        while ((copied = source.Read(buffer)) > 0)
        {
            checkCancellation();
            destination.Write(buffer, 0, copied);
        }
        checkCancellation();
    }

    internal static void PrepareArguments(ProcessStartInfo start)
    {
        // pixtool requires option values to be quoted after '=', rather than quoting the whole option.
        string arguments = FormatArguments(start.ArgumentList);
        start.ArgumentList.Clear();
        start.Arguments = arguments;
    }

    internal static string FormatArguments(IEnumerable<string> arguments)
        => string.Join(' ', arguments.Select(argument =>
        {
            int separator = argument.StartsWith("--", StringComparison.Ordinal) ? argument.IndexOf('=') : -1;
            return separator < 0 ? QuoteWindowsArgument(argument) : argument[..(separator + 1)] + QuoteWindowsArgument(argument[(separator + 1)..]);
        }));

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var quoted = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { backslashes++; continue; }
            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    internal static void Run(ProcessStartInfo start, TimeSpan timeout, CancellationToken cancellation,
        Action<string> diagnostic, string operation)
    {
        using var process = new Process { StartInfo = start };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        cancellation.ThrowIfCancellationRequested();
        try
        {
            if (!process.Start()) throw new PixToolException(PixErrors.Codes.PixToolStartFailed, $"{operation}: could not start pixtool.");
        }
        catch (Win32Exception ex)
        {
            throw new PixToolException(PixErrors.Codes.PixToolStartFailed, $"{operation}: could not start pixtool: {ex.Message}");
        }
        Task<string> stdout = Drain(process.StandardOutput);
        Task<string> stderr = Drain(process.StandardError);
        string output = "", errors = "";
        try
        {
            process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            finally { process.WaitForExit(); }
            if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
            throw new PixToolException(PixErrors.Codes.PixToolTimeout, $"{operation}: pixtool exceeded the timeout and was terminated.", true);
        }
        finally
        {
            output = stdout.GetAwaiter().GetResult();
            errors = stderr.GetAwaiter().GetResult();
            diagnostic(output);
            diagnostic(errors);
        }
        if (process.ExitCode != 0)
            throw new PixToolException(PixErrors.Codes.PixToolFailed, $"{operation}: pixtool exited with code {process.ExitCode}{FirstError(output, errors)}; see bounded job diagnostics.");
    }

    /// <summary>pixtool's own error line (": ... pixtool error: PIXTOOL9 - ..."), else the first nonblank output line, bounded to 300 characters.</summary>
    internal static string FirstError(params string[] streams)
    {
        string[] lines = streams.SelectMany(s => s.Split((char)13, (char)10)).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        string? line = lines.FirstOrDefault(l => l.Contains("pixtool error", StringComparison.OrdinalIgnoreCase)) ?? lines.FirstOrDefault();
        return line is null ? "" : ": " + (line.Length > 300 ? line[..300] : line);
    }

    private static async Task<string> Drain(StreamReader reader)
    {
        const int max = 4096;
        var text = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            if (text.Length < max) text.Append(buffer, 0, Math.Min(count, max - text.Length));
        return text.ToString();
    }
}

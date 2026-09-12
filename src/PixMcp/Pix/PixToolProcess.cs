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
            throw new PixToolException("analysis_active", $"Stop connected GPU analyses before CLI replay: {string.Join(", ", connected)}.", true,
                connected.Select(id => new ToolCallDto("pix_gpu_analysis_stop", new { handle = id })).ToArray());
    }

    internal static string Executable(string operation, string? installDirectory)
    {
        string executable = Path.Combine(installDirectory ?? string.Empty, "pixtool.exe");
        if (!File.Exists(executable))
            throw new PixToolException(operation + "_unavailable", "The installed PIX runtime does not contain pixtool.exe.");
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
            if (!process.Start()) throw new PixToolException(operation + "_start_failed", "Could not start pixtool.");
        }
        catch (Win32Exception ex)
        {
            throw new PixToolException(operation + "_start_failed", "Could not start pixtool: " + ex.Message);
        }
        Task<string> stdout = Drain(process.StandardOutput);
        Task<string> stderr = Drain(process.StandardError);
        try
        {
            process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            finally { process.WaitForExit(); }
            if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
            throw new PixToolException(operation + "_timeout", $"pixtool exceeded the {operation} timeout and was terminated.", true);
        }
        finally
        {
            diagnostic(stdout.GetAwaiter().GetResult());
            diagnostic(stderr.GetAwaiter().GetResult());
        }
        if (process.ExitCode != 0)
            throw new PixToolException(operation + "_failed", $"pixtool exited with code {process.ExitCode}; see bounded job diagnostics.");
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

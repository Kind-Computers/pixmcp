using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PixMcp;

/// <summary>
/// Keeps the MCP protocol stream away from native code. Native PIX components (the shader profiling document and its D3D12
/// device, for one) print diagnostics to the process's standard output, which is the MCP stdio transport. At startup the server
/// duplicates the real standard output handle for the transport, then points the Win32 standard output handle, the C runtime's
/// file descriptor 1 and <see cref="Console.Out"/> at standard error.
/// </summary>
internal static class ProtocolStdout
{
    private const int StdOutputHandle = -11, StdErrorHandle = -12;
    private const uint DuplicateSameAccess = 2;
    private static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int handle, IntPtr value);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _dup2(int source, int target);

    /// <summary>
    /// The stream the MCP transport writes to, with standard output redirected to standard error for everything else; null when
    /// standard output could not be claimed, in which case the plain stdio transport is used.
    /// </summary>
    public static Stream? Claim(TextWriter diagnostics)
    {
        try
        {
            IntPtr output = GetStdHandle(StdOutputHandle);
            IntPtr error = GetStdHandle(StdErrorHandle);
            if (output == IntPtr.Zero || output == InvalidHandle || error == IntPtr.Zero || error == InvalidHandle) return null;
            IntPtr process = GetCurrentProcess();
            if (!DuplicateHandle(process, output, process, out IntPtr protocol, 0, false, DuplicateSameAccess)) return null;
            var stream = new FileStream(new SafeFileHandle(protocol, ownsHandle: true), FileAccess.Write, bufferSize: 1);
            Console.Out.Flush();
            if (!SetStdHandle(StdOutputHandle, error))
            {
                stream.Dispose();
                return null;
            }
            try { _dup2(2, 1); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                diagnostics.WriteLine("pixmcp: warning: could not redirect the C runtime's standard output (" + ex.Message + "); native prints may still reach the protocol stream.");
            }
            Console.SetOut(Console.Error);
            return stream;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            diagnostics.WriteLine("pixmcp: warning: standard output could not be isolated from native code (" + ex.Message + "); using the plain stdio transport.");
            return null;
        }
    }
}

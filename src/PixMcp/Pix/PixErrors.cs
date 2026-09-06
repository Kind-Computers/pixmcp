using System.Runtime.InteropServices;
using ModelContextProtocol;

namespace PixMcp.Pix;

public static class PixErrors
{
    public const int E_PIX_DEVELOPER_MODE_NOT_ENABLED = unchecked((int)0x8ABC0000);
    public const int E_PIX_FEATURE_REQUIRES_DEVELOPER_MODE = unchecked((int)0x8ABC0001);
    public const int E_NOT_VALID_STATE = unchecked((int)0x8007139F);
    /// <summary>E_ABORT: PIX reports an operation interrupted by its cancellation token this way.</summary>
    public const int E_ABORT = unchecked((int)0x80004004);
    /// <summary>HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED).</summary>
    public const int E_OPERATION_ABORTED = unchecked((int)0x800704C7);

    public static string Hex(int hresult) => $"0x{hresult:X8}";

    public static int? HResultOf(Exception ex) => ex switch
    {
        COMException com => com.HResult,
        ExternalException ext => ext.ErrorCode,
        _ => null,
    };

    /// <summary>True for the HRESULTs PIX uses to report an interrupted (cancelled) operation.</summary>
    public static bool IsCancellationHResult(int hresult) => hresult is E_ABORT or E_OPERATION_ABORTED;

    /// <summary>Human-readable description with HRESULT and, where relevant, remediation.</summary>
    public static string Describe(Exception ex)
    {
        int? hr = HResultOf(ex);
        string message = ex.Message;
        if (hr is null)
        {
            return $"{ex.GetType().Name}: {message}";
        }

        string text = $"PIX API call failed ({Hex(hr.Value)}): {message}";
        if (hr == E_PIX_DEVELOPER_MODE_NOT_ENABLED || hr == E_PIX_FEATURE_REQUIRES_DEVELOPER_MODE)
        {
            text += " Windows Developer Mode is required for this operation. Enable it in Settings > Privacy & security > For developers, " +
                    "or run: reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\AppModelUnlock\" /v AllowDevelopmentWithoutDevLicense /t REG_DWORD /d 1 /f";
        }
        else if (hr == E_NOT_VALID_STATE)
        {
            text += " (E_NOT_VALID_STATE: the object is not in a state that supports this call, e.g. pipeline state not bound for this event, or analysis not started.)";
        }
        return text;
    }

    /// <summary>Wraps any exception into an McpException so the client sees a useful tool error.</summary>
    public static McpException ToMcp(Exception ex, string context)
    {
        if (ex is McpException mcp)
        {
            return mcp;
        }
        return new McpException($"{context}: {Describe(ex)}");
    }

    /// <summary>Runs <paramref name="work"/>, converting failures to McpExceptions. Cancellation propagates unchanged so the transport reports it as such.</summary>
    public static async Task<T> Guard<T>(string context, Func<Task<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ToMcp(ex, context);
        }
    }

    /// <summary>Returns an "unavailable" marker instead of failing, for optional PIX features.</summary>
    public static object Unavailable(string feature, Exception ex) => new
    {
        unavailable = true,
        feature,
        reason = Describe(ex),
    };
}

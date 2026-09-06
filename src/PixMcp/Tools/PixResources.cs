using System.ComponentModel;
using ModelContextProtocol.Server;
using PixMcp.Pix;

namespace PixMcp.Tools;

/// <summary>Read-only MCP resources mirroring the handle table.</summary>
[McpServerResourceType]
public static class PixResources
{
    [McpServerResource(UriTemplate = "pix://handles", Name = "Open PIX handles", MimeType = "application/json"), Description("The list of open PIX handles with their summaries.")]
    public static Task<string> Handles(PixSession session)
        => Tools.Run(session, "pix://handles", () => session.Handles.Select(h => h.Summary()).ToArray());

    [McpServerResource(UriTemplate = "pix://handles/{handle}", Name = "PIX handle summary", MimeType = "application/json"), Description("Summary of one open handle.")]
    public static Task<string> Handle(PixSession session, string handle)
        => Tools.Run(session, "pix://handles/" + handle, () => session.Get(handle).Summary());
}

using ModelContextProtocol;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>Checks immutable capture metadata before scheduling any replay.</summary>
internal static class ReferenceValidation
{
    internal static GpuCaptureHandle Event(PixSession session, EventRef eventRef)
    {
        if (eventRef is null) throw new PixToolException(PixErrors.Codes.InvalidReference, "eventRef is required.");
        GpuCaptureHandle h = Capture(session, eventRef.Handle);
        if (eventRef.QueueIndex < 0 || eventRef.QueueIndex >= h.Queues.Count)
            throw new PixToolException(PixErrors.Codes.InvalidReference, $"queueIndex {eventRef.QueueIndex} is out of range.",
                nextCalls: [new("pix_gpu_queues", new { handle = h.Id })]);
        QueueEntry queue = h.Queues[eventRef.QueueIndex];
        if (eventRef.EventIndex >= queue.EventCount)
            throw new PixToolException(PixErrors.Codes.InvalidReference, $"eventIndex {eventRef.EventIndex} is out of range; queue {eventRef.QueueIndex} has {queue.EventCount} event(s).",
                nextCalls: [new("pix_gpu_events", new { handle = h.Id, queueIndex = eventRef.QueueIndex })]);
        return h;
    }

    internal static void Shader(PixSession session, ShaderRef shaderRef)
    {
        if (shaderRef is null) throw new PixToolException(PixErrors.Codes.InvalidReference, "shaderRef is required.");
        Event(session, shaderRef.EventRef);
        if (shaderRef.ShaderIndex < 0) throw new PixToolException(PixErrors.Codes.InvalidReference, "shaderIndex must be nonnegative.",
            nextCalls: [new("pix_gpu_pipeline_state", new { eventRef = shaderRef.EventRef })]);
    }

    internal static void Resource(PixSession session, ResourceRef resourceRef)
    {
        if (resourceRef is null) throw new PixToolException(PixErrors.Codes.InvalidReference, "resourceRef is required.");
        Capture(session, resourceRef.Handle);
        Tools.Tools.ParseId(resourceRef.ApiObjectId, "apiObjectId");
    }

    internal static void Page(int offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > Paging.MaxLimit)
            throw new PixToolException(PixErrors.Codes.InvalidArguments, "offset must be nonnegative and limit must be between 1 and 1000.");
    }

    private static GpuCaptureHandle Capture(PixSession session, string handle)
        => session.TryGet<GpuCaptureHandle>(handle) ?? throw new PixToolException(PixErrors.Codes.InvalidReference,
            $"'{handle}' is not an open GPU capture handle.", nextCalls: [new("pix_handles", new { })]);
}

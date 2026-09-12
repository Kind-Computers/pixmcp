using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>A scope includes the selected event and its descendants, identified within one queue.</summary>
internal static class EventScope
{
    internal const string Description = "Restrict to this event and its descendants. Must belong to handle and any explicitly selected queue.";

    internal static int? ResolveQueue(PixSession session, string handle, int? queueIndex, EventRef? scope)
    {
        ValidateIdentity(handle, queueIndex, scope);
        if (scope is not null) ReferenceValidation.Event(session, scope);
        return queueIndex ?? scope?.QueueIndex;
    }

    internal static void ValidateIdentity(string handle, int? queueIndex, EventRef? scope)
    {
        if (scope is not null && (scope.Handle != handle || queueIndex.HasValue && queueIndex != scope.QueueIndex))
            throw new PixToolException("invalid_reference", "scope must belong to the selected capture and queue.");
    }

    internal static bool Contains(GpuCaptureHandle capture, EventRef? candidate, EventRef? scope)
        => scope is null || candidate is not null && candidate.Handle == scope.Handle &&
            candidate.QueueIndex == scope.QueueIndex &&
            EventNavigation.IsWithin(capture.AllEvents(scope.QueueIndex), candidate.EventIndex, scope.EventIndex);
}

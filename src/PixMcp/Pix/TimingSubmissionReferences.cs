using System.Globalization;

namespace PixMcp.Pix;

internal static class TimingSubmissionReferences
{
    internal static string Create(string handle, int generation, long id)
        => $"submission:{handle}:{generation.ToString(CultureInfo.InvariantCulture)}:{TimingDatabase.Ns(id)}";

    internal static long Parse(string reference, string handle, int generation)
    {
        string[] parts = reference.Split(':');
        if (parts.Length != 4 || parts[0] != "submission" ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int version) ||
            !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out long id))
            throw new PixToolException(PixErrors.Codes.InvalidReference, "submissionRef is not a recorded submission reference.");
        if (parts[1] != handle || version != generation)
            throw new PixToolException(PixErrors.Codes.ResultExpired, "This submission reference belongs to another or changed timing capture. Repeat pix_timing_submissions.",
                nextCalls: [new("pix_timing_submissions", new { handle })]);
        return id;
    }
}

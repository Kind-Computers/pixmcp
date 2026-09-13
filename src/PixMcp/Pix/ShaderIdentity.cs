namespace PixMcp.Pix;

/// <summary>Stable shader and pipeline identities built from stage and hash only; names and slots never identify a shader.</summary>
public static class ShaderIdentity
{
    public const string KeyDescription = "Stable shader identity hash:STAGE:HASH as returned by pix_gpu_shaders, pix_gpu_pipelines or pix_gpu_rollup (groupBy=shader); matches every occurrence with that stage and hash.";

    /// <summary><c>hash:STAGE:HASH</c> in upper case, or null when the stage or hash is missing.</summary>
    public static string? ShaderKey(string? stage, string? hash)
        => string.IsNullOrWhiteSpace(stage) || string.IsNullOrWhiteSpace(hash) ? null
            : "hash:" + stage.Trim().ToUpperInvariant() + ":" + hash.Trim().ToUpperInvariant();

    /// <summary><c>pso:STAGE:HASH;STAGE:HASH</c> over every bound shader in ordinal order, or null when a hash is missing or nothing is bound.</summary>
    public static string? PsoKey(IEnumerable<ShaderInfoDto> shaders)
    {
        var parts = new List<string>();
        foreach (ShaderInfoDto shader in shaders)
        {
            if (ShaderKey(shader.Stage, shader.Hash) is not string key) return null;
            parts.Add(key["hash:".Length..]);
        }
        if (parts.Count == 0) return null;
        parts.Sort(StringComparer.Ordinal);
        return "pso:" + string.Join(';', parts.Distinct());
    }

    /// <summary>Parses <c>hash:STAGE:HASH</c> (case-insensitive) into its canonical upper-case parts.</summary>
    public static bool TryParseShaderKey(string? key, out string stage, out string hash)
    {
        stage = hash = "";
        if (string.IsNullOrWhiteSpace(key)) return false;
        string[] parts = key.Trim().Split(':');
        if (parts.Length != 3 || !parts[0].Equals("hash", StringComparison.OrdinalIgnoreCase) || parts[1].Length == 0 || parts[2].Length == 0) return false;
        stage = parts[1].ToUpperInvariant();
        hash = parts[2].ToUpperInvariant();
        return true;
    }

    /// <summary>The shader keys a pso key is made of.</summary>
    public static IReadOnlyList<string> ShaderKeysOf(string psoKey)
        => psoKey.StartsWith("pso:", StringComparison.Ordinal) ? psoKey[4..].Split(';').Select(p => "hash:" + p).ToArray() : [];
}

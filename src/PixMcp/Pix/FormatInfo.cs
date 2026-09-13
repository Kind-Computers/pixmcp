namespace PixMcp.Pix;

/// <summary>An estimated resource size and how it was derived.</summary>
internal readonly record struct ResourceSizeEstimate(ulong? Bytes, string Method);

/// <summary>
/// DXGI format sizes for resource size estimates. Names are accepted as PIX reports them (R8G8B8A8_UNORM) or with the
/// DXGI_FORMAT_ prefix.
/// </summary>
internal static class FormatInfo
{
    public const string Note = "Estimated from the description: texels times bits per texel over every mip, array slice and sample (4x4 blocks for " +
        "block-compressed formats); buffers use their width. Alignment, padding, tiling and heap placement are ignored.";

    private static readonly Dictionary<string, int> Bits = BuildBits();

    private static readonly Dictionary<string, int> BlockBytes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BC1_TYPELESS"] = 8, ["BC1_UNORM"] = 8, ["BC1_UNORM_SRGB"] = 8, ["BC4_TYPELESS"] = 8, ["BC4_UNORM"] = 8, ["BC4_SNORM"] = 8,
        ["BC2_TYPELESS"] = 16, ["BC2_UNORM"] = 16, ["BC2_UNORM_SRGB"] = 16, ["BC3_TYPELESS"] = 16, ["BC3_UNORM"] = 16, ["BC3_UNORM_SRGB"] = 16,
        ["BC5_TYPELESS"] = 16, ["BC5_UNORM"] = 16, ["BC5_SNORM"] = 16, ["BC6H_TYPELESS"] = 16, ["BC6H_UF16"] = 16, ["BC6H_SF16"] = 16,
        ["BC7_TYPELESS"] = 16, ["BC7_UNORM"] = 16, ["BC7_UNORM_SRGB"] = 16,
    };

    /// <summary>Formats without one texel size: planar and packed video, palettized, opaque feedback formats and UNKNOWN.</summary>
    private static readonly HashSet<string> Unsized = new(StringComparer.OrdinalIgnoreCase)
    {
        "UNKNOWN", "NV12", "P010", "P016", "420_OPAQUE", "YUY2", "Y210", "Y216", "NV11", "AI44", "IA44", "P8", "A8P8", "AYUV", "Y410", "Y416",
        "P208", "V208", "V408", "SAMPLER_FEEDBACK_MIN_MIP_OPAQUE", "SAMPLER_FEEDBACK_MIP_REGION_USED_OPAQUE", "FORCE_UINT",
    };

    private static Dictionary<string, int> BuildBits()
    {
        var bits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Add(int size, params string[] names)
        {
            foreach (string name in names) bits[name] = size;
        }
        Add(128, "R32G32B32A32_TYPELESS", "R32G32B32A32_FLOAT", "R32G32B32A32_UINT", "R32G32B32A32_SINT");
        Add(96, "R32G32B32_TYPELESS", "R32G32B32_FLOAT", "R32G32B32_UINT", "R32G32B32_SINT");
        Add(64, "R16G16B16A16_TYPELESS", "R16G16B16A16_FLOAT", "R16G16B16A16_UNORM", "R16G16B16A16_UINT", "R16G16B16A16_SNORM", "R16G16B16A16_SINT",
            "R32G32_TYPELESS", "R32G32_FLOAT", "R32G32_UINT", "R32G32_SINT", "R32G8X24_TYPELESS", "D32_FLOAT_S8X24_UINT", "R32_FLOAT_X8X24_TYPELESS",
            "X32_TYPELESS_G8X24_UINT");
        Add(32, "R10G10B10A2_TYPELESS", "R10G10B10A2_UNORM", "R10G10B10A2_UINT", "R11G11B10_FLOAT",
            "R8G8B8A8_TYPELESS", "R8G8B8A8_UNORM", "R8G8B8A8_UNORM_SRGB", "R8G8B8A8_UINT", "R8G8B8A8_SNORM", "R8G8B8A8_SINT",
            "R16G16_TYPELESS", "R16G16_FLOAT", "R16G16_UNORM", "R16G16_UINT", "R16G16_SNORM", "R16G16_SINT",
            "R32_TYPELESS", "R32_FLOAT", "D32_FLOAT", "R32_UINT", "R32_SINT",
            "R24G8_TYPELESS", "D24_UNORM_S8_UINT", "R24_UNORM_X8_TYPELESS", "X24_TYPELESS_G8_UINT", "R9G9B9E5_SHAREDEXP", "R8G8_B8G8_UNORM", "G8R8_G8B8_UNORM",
            "B8G8R8A8_UNORM", "B8G8R8X8_UNORM", "R10G10B10_XR_BIAS_A2_UNORM", "B8G8R8A8_TYPELESS", "B8G8R8A8_UNORM_SRGB", "B8G8R8X8_TYPELESS",
            "B8G8R8X8_UNORM_SRGB");
        Add(16, "R8G8_TYPELESS", "R8G8_UNORM", "R8G8_UINT", "R8G8_SNORM", "R8G8_SINT", "R16_TYPELESS", "R16_FLOAT", "D16_UNORM", "R16_UNORM", "R16_UINT",
            "R16_SNORM", "R16_SINT", "B5G6R5_UNORM", "B5G5R5A1_UNORM", "B4G4R4A4_UNORM", "A4B4G4R4_UNORM");
        Add(8, "R8_TYPELESS", "R8_UNORM", "R8_UINT", "R8_SNORM", "R8_SINT", "A8_UNORM");
        Add(1, "R1_UNORM");
        return bits;
    }

    public static string Normalize(string format) => format.StartsWith("DXGI_FORMAT_", StringComparison.OrdinalIgnoreCase) ? format[12..] : format;

    /// <summary>True when the table has an explicit entry (sized, block-compressed or deliberately unsized).</summary>
    public static bool Known(string format)
    {
        string f = Normalize(format);
        return Bits.ContainsKey(f) || BlockBytes.ContainsKey(f) || Unsized.Contains(f);
    }

    /// <summary>
    /// Buffers: width bytes. Textures: bits per texel over every mip (0 mip levels means a full chain), times array slices
    /// (depth for TEXTURE3D shrinks per mip instead) and samples. Unsized formats return null bytes.
    /// </summary>
    public static ResourceSizeEstimate EstimateBytes(string dimension, ulong width, uint height, ushort depthOrArraySize, ushort mipLevels, string format, uint sampleCount)
    {
        if (dimension.Equals("BUFFER", StringComparison.OrdinalIgnoreCase)) return new(width, "bufferWidth");
        string f = Normalize(format);
        bool volume = dimension.Equals("TEXTURE3D", StringComparison.OrdinalIgnoreCase);
        ulong w = Math.Max(width, 1UL), h = Math.Max((ulong)height, 1UL);
        ulong depth = volume ? Math.Max((ulong)depthOrArraySize, 1UL) : 1UL, slices = volume ? 1UL : Math.Max((ulong)depthOrArraySize, 1UL);
        int levels = mipLevels > 0 ? mipLevels : FullChain(Math.Max(w, Math.Max(h, depth)));
        ulong samples = Math.Max((ulong)sampleCount, 1UL);
        if (BlockBytes.TryGetValue(f, out int block))
        {
            ulong blocks = 0;
            for (int m = 0; m < levels; m++) blocks += Math.Max(1UL, (Mip(w, m) + 3) / 4) * Math.Max(1UL, (Mip(h, m) + 3) / 4) * Mip(depth, m);
            return new(blocks * (ulong)block * slices * samples, "blockCompressed");
        }
        if (!Bits.TryGetValue(f, out int bits)) return new(null, "unknownFormat");
        ulong texelBits = 0;
        for (int m = 0; m < levels; m++) texelBits += Mip(w, m) * Mip(h, m) * Mip(depth, m) * (ulong)bits;
        return new((texelBits + 7) / 8 * slices * samples, "dimsMipsArraySamples");
    }

    private static ulong Mip(ulong size, int level) => level >= 63 ? 1UL : Math.Max(1UL, size >> level);

    private static int FullChain(ulong largest)
    {
        int levels = 1;
        while (largest > 1)
        {
            largest >>= 1;
            levels++;
        }
        return levels;
    }
}

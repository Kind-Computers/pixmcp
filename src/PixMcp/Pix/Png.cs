using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.PIX;
using Windows.Win32.Graphics.Dxgi.Common;

namespace PixMcp.Pix;

/// <summary>
/// Minimal dependency-free PNG encoder for captured screenshots. 8-bit and 10-bit UNORM swapchain
/// formats are copied; HDR formats (16-bit float/UNORM, R11G11B10) are tone-mapped to 8-bit sRGB
/// (clamp to [0, 1] then the sRGB transfer function), which is what the PIX UI shows for them too.
/// </summary>
public static class Png
{
    private enum Layout { Rgba8, Bgra8, Bgrx8, R10G10B10A2, Rgba16Float, Rgba16Unorm, R11G11B10Float }

    private static Layout? LayoutOf(DXGI_FORMAT format) => format switch
    {
        DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM or DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB or DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_TYPELESS => Layout.Rgba8,
        DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM or DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM_SRGB or DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_TYPELESS => Layout.Bgra8,
        DXGI_FORMAT.DXGI_FORMAT_B8G8R8X8_UNORM or DXGI_FORMAT.DXGI_FORMAT_B8G8R8X8_UNORM_SRGB or DXGI_FORMAT.DXGI_FORMAT_B8G8R8X8_TYPELESS => Layout.Bgrx8,
        DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM or DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_TYPELESS => Layout.R10G10B10A2,
        DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT or DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_TYPELESS => Layout.Rgba16Float,
        DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_UNORM => Layout.Rgba16Unorm,
        DXGI_FORMAT.DXGI_FORMAT_R11G11B10_FLOAT => Layout.R11G11B10Float,
        _ => null,
    };

    public static bool IsSupported(DXGI_FORMAT format) => LayoutOf(format) is not null;

    /// <summary>True when the format is HDR/linear and gets tone-mapped rather than copied.</summary>
    public static bool IsToneMapped(DXGI_FORMAT format) => LayoutOf(format) is Layout.Rgba16Float or Layout.Rgba16Unorm or Layout.R11G11B10Float;

    public static int BytesPerPixel(DXGI_FORMAT format) => LayoutOf(format) switch
    {
        Layout.Rgba16Float or Layout.Rgba16Unorm => 8,
        null => throw new NotSupportedException($"Screenshot format {Json.EnumName(format)} is not supported by the PNG encoder."),
        _ => 4,
    };

    public static byte[] Encode(byte[] pixels, int width, int height, int rowPitch, DXGI_FORMAT format)
    {
        Layout layout = LayoutOf(format) ?? throw new NotSupportedException($"Screenshot format {Json.EnumName(format)} is not supported by the PNG encoder.");
        int bytesPerPixel = BytesPerPixel(format);
        long needed = (long)(height - 1) * rowPitch + (long)width * bytesPerPixel;
        if (height > 0 && width > 0 && needed > pixels.Length)
        {
            throw new ArgumentException($"Screenshot pixel data is {pixels.Length} bytes but {width}x{height} at row pitch {rowPitch} needs {needed}.");
        }

        // Raw scanlines: filter byte 0 + RGBA per pixel.
        var raw = new byte[height * (1 + width * 4)];
        int o = 0;
        for (int y = 0; y < height; y++)
        {
            raw[o++] = 0;
            int row = y * rowPitch;
            for (int x = 0; x < width; x++)
            {
                ReadOnlySpan<byte> p = pixels.AsSpan(row + x * bytesPerPixel, bytesPerPixel);
                switch (layout)
                {
                    case Layout.Rgba8:
                        raw[o++] = p[0]; raw[o++] = p[1]; raw[o++] = p[2]; raw[o++] = p[3];
                        break;
                    case Layout.Bgra8:
                        raw[o++] = p[2]; raw[o++] = p[1]; raw[o++] = p[0]; raw[o++] = p[3];
                        break;
                    case Layout.Bgrx8:
                        raw[o++] = p[2]; raw[o++] = p[1]; raw[o++] = p[0]; raw[o++] = 255;
                        break;
                    case Layout.R10G10B10A2:
                    {
                        uint v = BinaryPrimitives.ReadUInt32LittleEndian(p);
                        raw[o++] = (byte)((v & 0x3FF) >> 2);
                        raw[o++] = (byte)(((v >> 10) & 0x3FF) >> 2);
                        raw[o++] = (byte)(((v >> 20) & 0x3FF) >> 2);
                        raw[o++] = 255;
                        break;
                    }
                    case Layout.Rgba16Float:
                        raw[o++] = ToSrgb8((float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(p)));
                        raw[o++] = ToSrgb8((float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(p[2..])));
                        raw[o++] = ToSrgb8((float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(p[4..])));
                        raw[o++] = Clamp8((float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(p[6..])));
                        break;
                    case Layout.Rgba16Unorm:
                        raw[o++] = ToSrgb8(BinaryPrimitives.ReadUInt16LittleEndian(p) / 65535f);
                        raw[o++] = ToSrgb8(BinaryPrimitives.ReadUInt16LittleEndian(p[2..]) / 65535f);
                        raw[o++] = ToSrgb8(BinaryPrimitives.ReadUInt16LittleEndian(p[4..]) / 65535f);
                        raw[o++] = Clamp8(BinaryPrimitives.ReadUInt16LittleEndian(p[6..]) / 65535f);
                        break;
                    case Layout.R11G11B10Float:
                    {
                        uint v = BinaryPrimitives.ReadUInt32LittleEndian(p);
                        raw[o++] = ToSrgb8(Float11(v & 0x7FF));
                        raw[o++] = ToSrgb8(Float11((v >> 11) & 0x7FF));
                        raw[o++] = ToSrgb8(Float10((v >> 22) & 0x3FF));
                        raw[o++] = 255;
                        break;
                    }
                }
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // colour type RGBA
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    /// <summary>Linear [0, 1] (clamped; NaN treated as 0) to 8-bit sRGB.</summary>
    internal static byte ToSrgb8(float linear)
    {
        if (float.IsNaN(linear) || linear <= 0) return 0;
        if (linear >= 1) return 255;
        double srgb = linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (byte)Math.Round(srgb * 255);
    }

    private static byte Clamp8(float value) => float.IsNaN(value) || value <= 0 ? (byte)0 : value >= 1 ? (byte)255 : (byte)Math.Round(value * 255);

    // Unsigned 11- and 10-bit floats (5-bit exponent, no sign) as used by R11G11B10_FLOAT.
    internal static float Float11(uint bits) => SmallFloat(bits, mantissaBits: 6);
    internal static float Float10(uint bits) => SmallFloat(bits, mantissaBits: 5);

    private static float SmallFloat(uint bits, int mantissaBits)
    {
        uint exponent = (bits >> mantissaBits) & 0x1F;
        uint mantissa = bits & ((1u << mantissaBits) - 1);
        float m = mantissa / (float)(1 << mantissaBits);
        if (exponent == 0) return m * MathF.Pow(2, -14); // denormal
        if (exponent == 31) return mantissa == 0 ? float.PositiveInfinity : float.NaN;
        return (1 + m) * MathF.Pow(2, (int)exponent - 15);
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        uint crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (byte x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}

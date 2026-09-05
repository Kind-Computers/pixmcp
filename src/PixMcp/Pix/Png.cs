using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.PIX;
using Windows.Win32.Graphics.Dxgi.Common;

namespace PixMcp.Pix;

/// <summary>Minimal dependency-free PNG encoder for 8-bit-per-channel RGBA/BGRA screenshots.</summary>
public static class Png
{
    public static bool IsSupported(DXGI_FORMAT format) => format switch
    {
        DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM or DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB or DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_TYPELESS => true,
        DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM or DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM_SRGB or DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_TYPELESS => true,
        DXGI_FORMAT.DXGI_FORMAT_B8G8R8X8_UNORM or DXGI_FORMAT.DXGI_FORMAT_B8G8R8X8_UNORM_SRGB or DXGI_FORMAT.DXGI_FORMAT_B8G8R8X8_TYPELESS => true,
        DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM or DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_TYPELESS => true,
        _ => false,
    };

    public static byte[] Encode(byte[] pixels, int width, int height, int rowPitch, DXGI_FORMAT format)
    {
        bool bgr = format is DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM or DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM_SRGB or DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_TYPELESS
            or DXGI_FORMAT.DXGI_FORMAT_B8G8R8X8_UNORM or DXGI_FORMAT.DXGI_FORMAT_B8G8R8X8_UNORM_SRGB or DXGI_FORMAT.DXGI_FORMAT_B8G8R8X8_TYPELESS;
        bool opaque = format is DXGI_FORMAT.DXGI_FORMAT_B8G8R8X8_UNORM or DXGI_FORMAT.DXGI_FORMAT_B8G8R8X8_UNORM_SRGB or DXGI_FORMAT.DXGI_FORMAT_B8G8R8X8_TYPELESS;
        bool r10 = format is DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM or DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_TYPELESS;

        // Raw scanlines: filter byte 0 + RGBA per pixel.
        var raw = new byte[height * (1 + width * 4)];
        int o = 0;
        for (int y = 0; y < height; y++)
        {
            raw[o++] = 0;
            int row = y * rowPitch;
            for (int x = 0; x < width; x++)
            {
                int p = row + x * 4;
                if (p + 3 >= pixels.Length)
                {
                    o += 4;
                    continue;
                }
                if (r10)
                {
                    uint v = BinaryPrimitives.ReadUInt32LittleEndian(pixels.AsSpan(p, 4));
                    raw[o++] = (byte)((v & 0x3FF) >> 2);
                    raw[o++] = (byte)(((v >> 10) & 0x3FF) >> 2);
                    raw[o++] = (byte)(((v >> 20) & 0x3FF) >> 2);
                    raw[o++] = 255;
                }
                else
                {
                    raw[o++] = bgr ? pixels[p + 2] : pixels[p];
                    raw[o++] = pixels[p + 1];
                    raw[o++] = bgr ? pixels[p] : pixels[p + 2];
                    raw[o++] = opaque ? (byte)255 : pixels[p + 3];
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

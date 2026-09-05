using System.Text.Json;
using Microsoft.PIX;
using Windows.Win32.Foundation;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace PixMcp.Pix;

/// <summary>Small helpers for turning PIX interop types into plain managed values.</summary>
public static class Interop
{
    public static string W(PCWSTR value)
    {
        try { return value.ToString() ?? string.Empty; }
        catch { return string.Empty; }
    }

    public static string A(PCSTR value)
    {
        try { return value.ToString() ?? string.Empty; }
        catch { return string.Empty; }
    }

    public static string? WOrNull(PCWSTR value)
    {
        string s = W(value);
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public static string E(Enum value) => Json.EnumName(value);

    public static object? Value(PIX_VALUE value)
    {
        if (value.ValueType == PIX_VALUE_TYPE.PIX_VALUE_STRING)
        {
            return W(value.Value.ValueString);
        }
        if (value.ValueType == PIX_VALUE_TYPE.PIX_VALUE_UNKNOWN)
        {
            return null;
        }

        return NumericValue(value.Value.ValueNumeric.Bits, value.Value.ValueNumeric.FormatSpecifier);
    }

    /// <summary>Interprets the raw storage used by PIX numeric values and GPU hardware counters.</summary>
    public static object NumericValue(ulong bits, PIX_FORMAT_SPECIFIER_TYPE spec)
    {
        uint typeBits = (uint)spec & (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_TYPE_AND_SIZE_BITMASK;
        uint type = typeBits & ~(uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_SIZE_BITMASK;
        uint size = typeBits & (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_SIZE_BITMASK;

        if (type == (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_TYPE_FLOAT)
        {
            if (size == (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_SIZE_64BIT)
            {
                return BitConverter.Int64BitsToDouble((long)bits);
            }
            if (size == (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_SIZE_16BIT)
            {
                return (double)BitConverter.UInt16BitsToHalf((ushort)bits);
            }
            return BitConverter.Int32BitsToSingle((int)bits);
        }
        if (type == (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_TYPE_INT)
        {
            return size switch
            {
                (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_SIZE_4BIT => unchecked((long)(bits << 60)) >> 60,
                (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_SIZE_8BIT => (long)(sbyte)bits,
                (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_SIZE_16BIT => (long)(short)bits,
                (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_SIZE_32BIT => (long)(int)bits,
                _ => (long)bits,
            };
        }
        if (type == (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_TYPE_UINT)
        {
            return size switch
            {
                (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_SIZE_4BIT => bits & 0xFUL,
                (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_SIZE_8BIT => bits & 0xFFUL,
                (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_SIZE_16BIT => bits & 0xFFFFUL,
                (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_SIZE_32BIT => bits & 0xFFFFFFFFUL,
                _ => bits,
            };
        }
        if (type == (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_TYPE_BOOL)
        {
            return bits != 0;
        }
        if (type == (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_TYPE_HEX
            || type == (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_TYPE_BINARY
            || type == (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_TYPE_BITMASK)
        {
            return $"0x{bits:X}";
        }
        return bits;
    }

    /// <summary>Decodes a PIX_FORMAT_SPECIFIER_TYPE bitfield into a readable data type such as "UINT64" or "FLOAT32".</summary>
    public static string FormatSpecifierName(PIX_FORMAT_SPECIFIER_TYPE spec)
    {
        uint bits = (uint)spec;
        uint type = bits & (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_TYPE_BITMASK;
        uint size = bits & (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_SIZE_BITMASK;
        string typeName = type switch
        {
            (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_TYPE_FLOAT => "FLOAT",
            (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_TYPE_INT => "INT",
            (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_TYPE_UINT => "UINT",
            (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_TYPE_BOOL => "BOOL",
            (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_TYPE_HEX => "HEX",
            (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_TYPE_BINARY => "BINARY",
            (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_TYPE_BITMASK => "BITMASK",
            _ => "DEFAULT",
        };
        string sizeName = size switch
        {
            (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_SIZE_4BIT => "4",
            (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_SIZE_8BIT => "8",
            (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_SIZE_16BIT => "16",
            (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_SIZE_32BIT => "32",
            (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_SIZE_64BIT => "64",
            _ => "",
        };
        return typeName + sizeName;
    }

    /// <summary>
    /// D3D12 view descriptions are unions keyed by a ViewDimension field. Given the reflected
    /// dictionary, keeps only the union arm matching the dimension so output stays compact.
    /// </summary>
    public static object? CollapseUnion(object? reflected)
    {
        if (reflected is not Dictionary<string, object?> dict || !dict.TryGetValue("anonymous", out object? union) || union is not Dictionary<string, object?> arms)
        {
            return reflected;
        }
        string? dimension = dict.TryGetValue("viewDimension", out object? d) ? d as string : null;
        if (dimension is null)
        {
            return reflected;
        }
        string wanted = dimension.Replace("_", "").ToLowerInvariant();
        foreach ((string key, object? value) in arms)
        {
            if (key.Replace("_", "").ToLowerInvariant() == wanted)
            {
                dict.Remove("anonymous");
                dict[JsonNamingPolicy.CamelCase.ConvertName(key)] = value;
                return dict;
            }
        }
        return reflected;
    }

    public static DateTime? FileTime(FILETIME ft)
    {
        long value = ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
        if (value <= 0)
        {
            return null;
        }
        try { return DateTime.FromFileTimeUtc(value); }
        catch { return null; }
    }

    public static string VersionString(D3D12_VERSION_NUMBER v)
    {
        ulong n = v.Version;
        return $"{(n >> 48) & 0xFFFF}.{(n >> 32) & 0xFFFF}.{(n >> 16) & 0xFFFF}.{n & 0xFFFF}";
    }

    public static string Hex(ulong value) => $"0x{value:X}";

    public static IEnumerable<T> Items<T>(IPixCollection collection) where T : class
    {
        ulong count = collection.GetCount();
        for (ulong i = 0; i < count; i++)
        {
            T? item = Microsoft.PIX.Extension.PixApiExtensions.TryGet<T>(collection, i, out _);
            if (item is not null)
            {
                yield return item;
            }
        }
    }
}

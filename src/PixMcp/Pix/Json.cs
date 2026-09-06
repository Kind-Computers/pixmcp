using System.Text.Json;
using System.Text.Json.Serialization;

namespace PixMcp.Pix;

/// <summary>Tool result serialization: camelCase, nulls omitted, PIX/D3D enums as trimmed names.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new TrimmedEnumConverterFactory() },
    };

    /// <summary>Converter factory that writes any enum as its trimmed member name; reusable for third-party JsonSerializerOptions.</summary>
    public static JsonConverterFactory EnumConverterFactory { get; } = new TrimmedEnumConverterFactory();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>"PIX_QUEUE_TYPE_GRAPHICS" -> "GRAPHICS", "DXGI_FORMAT_R8G8B8A8_UNORM" -> "R8G8B8A8_UNORM".</summary>
    public static string EnumName(Enum value)
    {
        string name = value.ToString();
        string typeName = value.GetType().Name;
        if (name.StartsWith(typeName + "_", StringComparison.Ordinal))
        {
            return name[(typeName.Length + 1)..];
        }
        // Enums whose members drop a suffix of the type name, e.g. PIX_RESOURCE_VIEW_TYPE -> PIX_RESOURCE_CONSTANT_BUFFER_VIEW.
        string[] typeParts = typeName.Split('_');
        for (int keep = typeParts.Length - 1; keep >= 1; keep--)
        {
            string prefix = string.Join('_', typeParts, 0, keep) + "_";
            if (name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length)
            {
                return name[prefix.Length..];
            }
        }
        return name;
    }

    private sealed class TrimmedEnumConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(typeof(TrimmedEnumConverter<>).MakeGenericType(typeToConvert))!;
    }

    private sealed class TrimmedEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => Enum.Parse<T>(reader.GetString() ?? string.Empty, ignoreCase: true);

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => writer.WriteStringValue(EnumName(value));
    }
}

/// <summary>Offset/limit paging shared by every enumeration tool.</summary>
public static class Paging
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 1000;

    public static (int offset, int limit) Normalize(int? offset, int? limit)
    {
        int o = Math.Max(0, offset ?? 0);
        int l = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        return (o, l);
    }

    public static PageResult<T> Page<T>(IReadOnlyList<T> items, long total, int offset, int limit, object? extra = null)
        => new(total, offset, items.Count,
            offset + (long)items.Count < total ? checked(offset + items.Count) : null, items, extra);
}

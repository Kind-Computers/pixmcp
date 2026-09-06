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
        /// <summary>Accepts the trimmed names this converter writes as well as full member names.</summary>
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string text = reader.GetString() ?? string.Empty;
            foreach (T member in Enum.GetValues<T>())
            {
                if (member.ToString().Equals(text, StringComparison.OrdinalIgnoreCase) || EnumName(member).Equals(text, StringComparison.OrdinalIgnoreCase))
                {
                    return member;
                }
            }
            return Enum.Parse<T>(text, ignoreCase: true);
        }

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

    /// <summary>Counts every (already filtered) item for <c>total</c> and projects only the requested window.</summary>
    public static PageResult<object> Collect<T>(IEnumerable<T> items, int offset, int limit, Func<T, object> project, object? extra = null)
    {
        var page = new List<object>();
        long total = 0;
        foreach (T item in items)
        {
            if (total >= offset && page.Count < limit)
            {
                page.Add(project(item));
            }
            total++;
        }
        return Page(page, total, offset, limit, extra);
    }

    /// <summary>An empty page carrying an unavailable marker, so paged tools keep their shape when PIX has no data.</summary>
    public static PageResult<object> Unavailable(int offset, int limit, string feature, Exception? error, string? fallbackReason = null)
        => Page(Array.Empty<object>(), 0, offset, limit, new
        {
            unavailable = true,
            feature,
            reason = error is null ? fallbackReason ?? "PIX returned no data." : PixErrors.Describe(error),
        });
}

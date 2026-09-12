using System.Collections;
using System.Reflection;
using System.Text.Json;
using Microsoft.PIX;
using Windows.Win32.Foundation;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace PixMcp.Pix;

/// <summary>
/// Generic struct-to-JSON-friendly conversion for PIX/D3D interop structs whose exact shape
/// we do not want to hand-map: public fields and properties become a dictionary, PCWSTR/PCSTR
/// become strings, enums become trimmed names, pointers are skipped.
/// </summary>
public static class Reflect
{
    public static object? ToObject(object? value, int depth = 0)
        => ConvertValue(value, new HashSet<object>(ReferenceEqualityComparer.Instance));

    private static object? ConvertValue(object? value, HashSet<object> ancestors)
    {
        if (value is null)
        {
            return null;
        }

        switch (value)
        {
            case string s: return s;
            case PCWSTR w: return Interop.W(w);
            case PCSTR a: return Interop.A(a);
            case BOOL b: return (bool)b;
            case HRESULT h: return PixErrors.Hex(h.Value);
            case FILETIME ft: return Interop.FileTime(ft);
            case Guid g: return g.ToString();
            case Enum e: return Json.EnumName(e);
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or char:
                return value;
            case DateTime or DateTimeOffset or TimeSpan:
                return value;
            // Generated array wrappers expose all elements through their spans.
            case __float_4 floats:
                return ConvertValue(floats.AsReadOnlySpan().ToArray(), ancestors);
            case __uint_4 uints:
                return ConvertValue(uints.AsReadOnlySpan().ToArray(), ancestors);
            case __ushort_4 ushorts:
                return ConvertValue(ushorts.AsReadOnlySpan().ToArray(), ancestors);
            case __DXGI_FORMAT_8 formats:
                return ConvertValue(formats.AsReadOnlySpan().ToArray(), ancestors);
            case __D3D12_RENDER_TARGET_BLEND_DESC_8 blends:
                return ConvertValue(blends.AsReadOnlySpan().ToArray(), ancestors);
            case Windows.Win32.__char_128 chars:
                return chars.ToString();
        }

        Type type = value.GetType();
        if (type.IsPointer)
        {
            return null;
        }
        // ResultStore owns output budgets. Copy complete finite data here so tails/deep fields
        // remain retrievable instead of being irreversibly replaced by "..." or type names.
        bool reference = !type.IsValueType;
        if (reference && !ancestors.Add(value))
            return new { unavailable = true, reason = "Reference cycle in reflected data.", type = type.FullName };
        try
        {
            if (value is IDictionary dictionary)
            {
                var entries = new Dictionary<string, object?>();
                foreach (DictionaryEntry entry in dictionary)
                    entries[Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture) ?? ""] = ConvertValue(entry.Value, ancestors);
                return entries;
            }
            if (value is IEnumerable enumerable)
            {
                var list = new List<object?>();
                foreach (object? item in enumerable) list.Add(ConvertValue(item, ancestors));
                return list;
            }

            var dict = new Dictionary<string, object?>();
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (IsPointer(field.FieldType)) continue;
                object? fieldValue;
                try { fieldValue = field.GetValue(value); }
                catch (Exception ex) { dict[JsonNamingPolicy.CamelCase.ConvertName(field.Name.TrimStart('_'))] = new { unavailable = true, reason = ex.Message }; continue; }
                dict[JsonNamingPolicy.CamelCase.ConvertName(field.Name.TrimStart('_'))] = ConvertValue(fieldValue, ancestors);
            }
            if (dict.Count == 0)
            {
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.GetIndexParameters().Length > 0 || IsPointer(property.PropertyType)) continue;
                    object? propertyValue;
                    try { propertyValue = property.GetValue(value); }
                    catch (Exception ex) { dict[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = new { unavailable = true, reason = ex.Message }; continue; }
                    dict[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = ConvertValue(propertyValue, ancestors);
                }
            }
            return dict.Count == 0 ? value.ToString() : dict;
        }
        finally
        {
            if (reference) ancestors.Remove(value);
        }
    }

    private static bool IsPointer(Type type) => type.IsPointer || type.IsByRefLike || type == typeof(IntPtr) || type == typeof(UIntPtr);
}
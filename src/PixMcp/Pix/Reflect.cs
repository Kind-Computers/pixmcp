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
                return ToObject(floats.AsReadOnlySpan().ToArray(), depth);
            case __uint_4 uints:
                return ToObject(uints.AsReadOnlySpan().ToArray(), depth);
            case __ushort_4 ushorts:
                return ToObject(ushorts.AsReadOnlySpan().ToArray(), depth);
            case __DXGI_FORMAT_8 formats:
                return ToObject(formats.AsReadOnlySpan().ToArray(), depth);
            case __D3D12_RENDER_TARGET_BLEND_DESC_8 blends:
                return ToObject(blends.AsReadOnlySpan().ToArray(), depth);
            case Windows.Win32.__char_128 chars:
                return chars.ToString();
        }

        Type type = value.GetType();
        if (type.IsPointer)
        {
            return null;
        }
        if (depth > 5)
        {
            return type.Name;
        }

        if (value is IEnumerable enumerable && value is not IDictionary)
        {
            var list = new List<object?>();
            int n = 0;
            foreach (object? item in enumerable)
            {
                if (n++ >= 256)
                {
                    list.Add("...");
                    break;
                }
                list.Add(ToObject(item, depth + 1));
            }
            return list;
        }

        var dict = new Dictionary<string, object?>();
        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.FieldType.IsPointer || field.FieldType == typeof(IntPtr) || field.FieldType == typeof(UIntPtr))
            {
                continue;
            }
            object? fieldValue;
            try { fieldValue = field.GetValue(value); }
            catch { continue; }
            dict[JsonNamingPolicy.CamelCase.ConvertName(field.Name.TrimStart('_'))] = ToObject(fieldValue, depth + 1);
        }
        if (dict.Count == 0)
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || property.PropertyType.IsPointer)
                {
                    continue;
                }
                object? propertyValue;
                try { propertyValue = property.GetValue(value); }
                catch { continue; }
                dict[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = ToObject(propertyValue, depth + 1);
            }
        }
        return dict.Count == 0 ? value.ToString() : dict;
    }
}

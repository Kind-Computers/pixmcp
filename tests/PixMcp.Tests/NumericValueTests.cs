using System.Text.Json;
using Microsoft.PIX;
using PixMcp.Pix;
using Xunit;
using Spec = Microsoft.PIX.PIX_FORMAT_SPECIFIER_TYPE;

namespace PixMcp.Tests;

public class NumericValueTests
{
    // Primitive attribute arguments let xUnit discover cases before PIX assembly resolution is initialized.
    [Theory]
    [InlineData(0x3C00UL, (uint)(Spec.PIX_FORMAT_SPECIFIER_FLOAT16), 1.0)]
    [InlineData(0xFFFF_FFFF_FFFF_C100UL, (uint)(Spec.PIX_FORMAT_SPECIFIER_FLOAT16), -2.5)]
    [InlineData(0x3F80_0000UL, (uint)(Spec.PIX_FORMAT_SPECIFIER_FLOAT32), 1.0)]
    [InlineData(0xFFFF_FFFF_C020_0000UL, (uint)(Spec.PIX_FORMAT_SPECIFIER_FLOAT32), -2.5)]
    [InlineData(0x3FF0_0000_0000_0000UL, (uint)(Spec.PIX_FORMAT_SPECIFIER_FLOAT64), 1.0)]
    [InlineData(0xC004_0000_0000_0000UL, (uint)(Spec.PIX_FORMAT_SPECIFIER_FLOAT64), -2.5)]
    [InlineData(0x3FF0_0000_0000_0000UL, (uint)(Spec.PIX_FORMAT_SPECIFIER_FLOAT64 | Spec.PIX_FORMAT_SPECIFIER_OPTION_FLOAT_NOTATION_SCIENTIFIC), 1.0)]
    public void FloatingPointStorageBecomesJsonNumbers(ulong bits, uint spec, double expected)
    {
        JsonElement json = SerializeValue(Interop.NumericValue(bits, (Spec)spec));
        Assert.Equal(JsonValueKind.Number, json.ValueKind);
        Assert.Equal(expected, json.GetDouble());
    }

    [Theory]
    [InlineData(0xFFFF_FFFF_FFFF_FFF7UL, (uint)(Spec.PIX_FORMAT_SPECIFIER_TYPE_INT | Spec.PIX_FORMAT_SPECIFIER_SIZE_4BIT), 7L)]
    [InlineData(0x8UL, (uint)(Spec.PIX_FORMAT_SPECIFIER_TYPE_INT | Spec.PIX_FORMAT_SPECIFIER_SIZE_4BIT), -8L)]
    [InlineData(0xFUL, (uint)(Spec.PIX_FORMAT_SPECIFIER_TYPE_INT | Spec.PIX_FORMAT_SPECIFIER_SIZE_4BIT), -1L)]
    [InlineData(0x80UL, (uint)(Spec.PIX_FORMAT_SPECIFIER_TYPE_INT | Spec.PIX_FORMAT_SPECIFIER_SIZE_8BIT), -128L)]
    [InlineData(0xFFFF_FFFF_FFFF_FF2AUL, (uint)(Spec.PIX_FORMAT_SPECIFIER_TYPE_INT | Spec.PIX_FORMAT_SPECIFIER_SIZE_8BIT), 42L)]
    [InlineData(0x8000UL, (uint)(Spec.PIX_FORMAT_SPECIFIER_INT16), -32768L)]
    [InlineData(0xFFFFUL, (uint)(Spec.PIX_FORMAT_SPECIFIER_INT16), -1L)]
    [InlineData(0x8000_0000UL, (uint)(Spec.PIX_FORMAT_SPECIFIER_INT32), -2147483648L)]
    [InlineData(0xFFFF_FFFFUL, (uint)(Spec.PIX_FORMAT_SPECIFIER_INT32), -1L)]
    [InlineData(0x8000_0000_0000_0000UL, (uint)(Spec.PIX_FORMAT_SPECIFIER_INT64), long.MinValue)]
    [InlineData(ulong.MaxValue, (uint)(Spec.PIX_FORMAT_SPECIFIER_INT64), -1L)]
    public void SignedIntegersUseTheirDeclaredWidth(ulong bits, uint spec, long expected)
    {
        Assert.Equal(expected, SerializeValue(Interop.NumericValue(bits, (Spec)spec)).GetInt64());
    }

    [Theory]
    [InlineData(0xFFFF_FFFF_FFFF_FFFAUL, (uint)(Spec.PIX_FORMAT_SPECIFIER_TYPE_UINT | Spec.PIX_FORMAT_SPECIFIER_SIZE_4BIT), 10UL)]
    [InlineData(0xFFFF_FFFF_FFFF_FFABUL, (uint)(Spec.PIX_FORMAT_SPECIFIER_TYPE_UINT | Spec.PIX_FORMAT_SPECIFIER_SIZE_8BIT), 171UL)]
    [InlineData(0xFFFF_FFFF_FFFF_ABCDUL, (uint)(Spec.PIX_FORMAT_SPECIFIER_UINT16), 43981UL)]
    [InlineData(0xFFFF_FFFF_FFFF_FFFFUL, (uint)(Spec.PIX_FORMAT_SPECIFIER_UINT32), 4294967295UL)]
    [InlineData(ulong.MaxValue, (uint)(Spec.PIX_FORMAT_SPECIFIER_UINT64), ulong.MaxValue)]
    [InlineData(ulong.MaxValue, (uint)(Spec.PIX_FORMAT_SPECIFIER_DEFAULT), ulong.MaxValue)]
    public void UnsignedIntegersUseTheirDeclaredWidthAndPreserveFullPrecision(ulong bits, uint spec, ulong expected)
    {
        Assert.Equal(expected, SerializeValue(Interop.NumericValue(bits, (Spec)spec)).GetUInt64());
    }

    [Theory]
    [InlineData(0UL, (uint)(Spec.PIX_FORMAT_SPECIFIER_BOOL8), false)]
    [InlineData(1UL, (uint)(Spec.PIX_FORMAT_SPECIFIER_BOOL8), true)]
    [InlineData(42UL, (uint)(Spec.PIX_FORMAT_SPECIFIER_BOOL32), true)]
    public void BooleansRemainJsonBooleans(ulong bits, uint spec, bool expected)
    {
        Assert.Equal(expected, SerializeValue(Interop.NumericValue(bits, (Spec)spec)).GetBoolean());
    }

    [Theory]
    [InlineData((uint)(Spec.PIX_FORMAT_SPECIFIER_X64))]
    [InlineData((uint)(Spec.PIX_FORMAT_SPECIFIER_TYPE_BINARY | Spec.PIX_FORMAT_SPECIFIER_SIZE_64BIT))]
    [InlineData((uint)(Spec.PIX_FORMAT_SPECIFIER_TYPE_BITMASK | Spec.PIX_FORMAT_SPECIFIER_SIZE_64BIT))]
    public void DisplayFormatsKeepExistingHexStrings(uint spec)
    {
        Assert.Equal("0xABCD", SerializeValue(Interop.NumericValue(0xABCDUL, (Spec)spec)).GetString());
    }

    [Theory]
    [InlineData(0x7FF0_0000_0000_0000UL, "Infinity")]
    [InlineData(0xFFF0_0000_0000_0000UL, "-Infinity")]
    [InlineData(0x7FF8_0000_0000_0000UL, "NaN")]
    public void NonFiniteFloatsUseConfiguredJsonNamedLiterals(ulong bits, string expected)
    {
        Assert.Equal(expected, SerializeValue(Interop.NumericValue(bits, Spec.PIX_FORMAT_SPECIFIER_FLOAT64)).GetString());
    }

    [Theory]
    [InlineData(0x3FF0_0000_0000_0000UL, (uint)(Spec.PIX_FORMAT_SPECIFIER_FLOAT64), "1")]
    [InlineData(0xFFFFUL, (uint)(Spec.PIX_FORMAT_SPECIFIER_INT16), "-1")]
    [InlineData(0xFFFF_FFFF_0000_002AUL, (uint)(Spec.PIX_FORMAT_SPECIFIER_UINT32), "42")]
    [InlineData(1UL, (uint)(Spec.PIX_FORMAT_SPECIFIER_BOOL8), "true")]
    [InlineData(0xABCDUL, (uint)(Spec.PIX_FORMAT_SPECIFIER_X64), "\"0xABCD\"")]
    public void PixValuesUseTheSameNumericInterpretation(ulong bits, uint spec, string expectedJson)
    {
        PIX_VALUE value = default;
        value.ValueType = PIX_VALUE_TYPE.PIX_VALUE_NUMERIC;
        value.Value.ValueNumeric.Bits = bits;
        value.Value.ValueNumeric.FormatSpecifier = (Spec)spec;

        Assert.Equal(expectedJson, Json.Serialize(Interop.Value(value)));
    }

    [Fact]
    public void UnknownPixValueStaysNull()
    {
        Assert.Null(Interop.Value(default));
    }

    private static JsonElement SerializeValue(object value)
        => JsonSerializer.Deserialize<JsonElement>(Json.Serialize(value));
}

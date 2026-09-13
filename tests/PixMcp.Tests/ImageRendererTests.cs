using PixMcp.Pix;
using PixMcp.Tools;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.Win32.Graphics.Dxgi.Common;
using Xunit;

namespace PixMcp.Tests;

public sealed class ImageRendererTests
{
    private static byte[] PngImage(int width, int height, byte[] pixels)
        => Png.Encode(pixels, width, height, width * 4, DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM);

    [Fact]
    public async Task InlineOriginalIsByteExactAndNeverUpscaled()
    {
        byte[] original = PngImage(2, 1, [255, 0, 0, 255, 0, 255, 0, 128]);
        RenderedImage rendered = await ImageRenderer.Render(original, maxDimension: 1024);
        Assert.Same(original, rendered.Png);
        Assert.Equal((2u, 1u), (rendered.Width, rendered.Height));
        Assert.False(rendered.Resized);
        Assert.False(rendered.AlphaIgnored);
    }

    [Fact]
    public async Task OpaqueRgbViewKeepsTransparentColorsAndBypassesOriginalFastPath()
    {
        byte[] original = PngImage(2, 1, [240, 80, 40, 0, 20, 180, 100, 128]);
        byte[] retained = original.ToArray();
        RenderedImage normal = await ImageRenderer.Render(original);
        Assert.Same(original, normal.Png);
        Assert.False(normal.AlphaIgnored);

        RenderedImage opaque = await ImageRenderer.Render(original, ignoreAlpha: true);
        Assert.NotSame(original, opaque.Png);
        Assert.False(opaque.Resized);
        Assert.True(opaque.AlphaIgnored);
        Assert.Equal(new byte[] { 240, 80, 40, 255, 20, 180, 100, 255 }, await Pixels(opaque.Png));
        Assert.Equal(retained, original);
        using var result = System.Text.Json.JsonDocument.Parse(Json.Serialize(PreviewTools.ImageResult("preview-test", opaque)));
        Assert.True(result.RootElement.GetProperty("structuredContent").GetProperty("alphaIgnored").GetBoolean());
    }

    [Fact]
    public async Task OpaqueRgbViewDiscardsAlphaBeforeCroppingAndScaling()
    {
        byte[] original = PngImage(4, 2, [
            255, 0, 0, 255, 255, 0, 0, 255, 24, 120, 200, 0, 24, 120, 200, 0,
            255, 0, 0, 255, 255, 0, 0, 255, 24, 120, 200, 0, 24, 120, 200, 0]);
        RenderedImage opaque = await ImageRenderer.Render(original, new(2, 0, 2, 2), 1, ignoreAlpha: true);
        Assert.True(opaque.Resized);
        Assert.True(opaque.AlphaIgnored);
        Assert.Equal((1u, 1u), (opaque.Width, opaque.Height));
        Assert.Equal(new byte[] { 24, 120, 200, 255 }, await Pixels(opaque.Png));
    }

    [Fact]
    public async Task CropCoordinatesUseOriginalPixelsAndPreserveAlpha()
    {
        byte[] original = PngImage(4, 2, [255, 0, 0, 255, 255, 0, 0, 255, 0, 255, 0, 128, 0, 255, 0, 128,
            255, 0, 0, 255, 255, 0, 0, 255, 0, 255, 0, 128, 0, 255, 0, 128]);
        var crop = new ImageCrop(2, 0, 2, 2);
        RenderedImage cropped = await ImageRenderer.Render(original, crop);
        Assert.Equal((2u, 2u), PreviewTools.PngDimensions(cropped.Png));
        byte[] pixels = await Pixels(cropped.Png);
        Assert.Equal(new byte[] { 0, 255, 0, 128 }, pixels[..4]);
        RenderedImage scaled = await ImageRenderer.Render(original, crop, 1);
        Assert.Equal((1u, 1u), PreviewTools.PngDimensions(scaled.Png));
        Assert.Equal(new byte[] { 0, 255, 0, 128 }, (await Pixels(scaled.Png))[..4]);
        Assert.True(scaled.Resized);
        Assert.Equal(4u, scaled.OriginalWidth);
    }

    [Fact]
    public async Task OversizedOriginalAutomaticallyProducesBoundedThumbnailWithoutChangingArtifact()
    {
        const int width = 2048, height = 1024;
        var pixels = new byte[width * height * 4];
        new Random(7).NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        byte[] original = PngImage(width, height, pixels);
        Assert.True(original.Length > ImageRenderer.MaxInlineBytes);
        var artifacts = new PreviewArtifacts();
        string reference = artifacts.Add("gpu-1", original);
        RenderedImage rendered = await ImageRenderer.Render(artifacts.Get(reference, _ => true));
        Assert.True(rendered.Resized);
        Assert.True(rendered.Png.Length <= ImageRenderer.MaxInlineBytes);
        Assert.InRange(rendered.Width, 1u, 1024u);
        Assert.Equal(rendered.Width / 2, rendered.Height);
        Assert.Same(original, artifacts.Get(reference, _ => true));
    }

    [Fact]
    public async Task OversizedTransparentRenderTargetProducesVisibleOpaqueThumbnail()
    {
        const int width = 2048, height = 1024;
        var pixels = new byte[width * height * 4];
        new Random(9).NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 0;
        // A known flat patch makes the resampling assertion independent of random noise.
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                int i = (y * width + x) * 4;
                pixels[i] = 160; pixels[i + 1] = 80; pixels[i + 2] = 40;
            }
        byte[] original = PngImage(width, height, pixels);
        Assert.True(original.Length > ImageRenderer.MaxInlineBytes);
        var artifacts = new PreviewArtifacts();
        string reference = artifacts.Add("gpu-1", original);
        RenderedImage rendered = await ImageRenderer.Render(artifacts.Get(reference, _ => true), ignoreAlpha: true);
        Assert.True(rendered.Resized);
        Assert.True(rendered.AlphaIgnored);
        Assert.True(rendered.Png.Length <= ImageRenderer.MaxInlineBytes);
        Assert.Equal((1024u, 512u), (rendered.Width, rendered.Height));
        Assert.Equal(new byte[] { 160, 80, 40, 255 }, (await Pixels(rendered.Png))[..4]);
        Assert.Same(original, artifacts.Get(reference, _ => true));
    }

    [Fact]
    public void InvalidCropAndOverflowingBoundsAreRejected()
    {
        foreach (ImageCrop crop in new[] { new ImageCrop(-1, 0, 1, 1), new(0, 0, 0, 1), new(1, 1, 2, 2), new(int.MaxValue, 0, int.MaxValue, 1) })
            Assert.Throws<PixToolException>(() => ImageRenderer.Validate(2, 2, crop, null));
        Assert.Throws<PixToolException>(() => ImageRenderer.Validate(2, 2, null, 0));
        Assert.Throws<PixToolException>(() => ImageRenderer.Validate(2, 2, null, 4097));
        Assert.Equal((1024u, 512u), ImageRenderer.Fit(4096, 2048, 1024));
    }

    [Fact]
    public async Task ImageCancellationIsObservedBeforeDecoding()
        => await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageRenderer.Render([], cancellationToken: new CancellationToken(true)));

    private static async Task<byte[]> Pixels(byte[] png)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        return (await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Straight, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
    }
}

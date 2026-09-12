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

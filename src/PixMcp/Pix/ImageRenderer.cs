using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PixMcp.Pix;

public sealed record ImageCrop(int X, int Y, int Width, int Height);
internal sealed record RenderedImage(byte[] Png, uint OriginalWidth, uint OriginalHeight, uint Width, uint Height,
    ImageCrop? Crop, bool Resized);

/// <summary>Transforms detached PNG data using the Windows codecs; never enters the PIX worker.</summary>
internal static class ImageRenderer
{
    internal const int MaxInlineBytes = 4 * 1024 * 1024;
    private const ulong MaxDecodedPixels = 64 * 1024 * 1024;

    internal static async Task<RenderedImage> Render(byte[] png, ImageCrop? crop = null, int? maxDimension = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (originalWidth, originalHeight) = Tools.PreviewTools.PngDimensions(png);
        Validate(originalWidth, originalHeight, crop, maxDimension);
        uint width = (uint)(crop?.Width ?? (int)originalWidth), height = (uint)(crop?.Height ?? (int)originalHeight);
        int? bound = maxDimension ?? (png.Length > MaxInlineBytes ? 1024 : null);
        var (outputWidth, outputHeight) = Fit(width, height, bound);
        if (crop is null && outputWidth == width && outputHeight == height && png.Length <= MaxInlineBytes)
            return new(png, originalWidth, originalHeight, width, height, null, false);
        if ((ulong)originalWidth * originalHeight > MaxDecodedPixels)
            throw new PixToolException("image_too_large", "The image exceeds the 64 megapixel decoding limit. Retrieve its original bytes instead.");

        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(false);
            writer.DetachStream();
        }
        input.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.PngDecoderId, input).AsTask(cancellationToken).ConfigureAwait(false);
        var transform = new BitmapTransform();
        if (crop is not null)
            transform.Bounds = new BitmapBounds { X = (uint)crop.X, Y = (uint)crop.Y, Width = (uint)crop.Width, Height = (uint)crop.Height };
        // Windows transforms scale before cropping. A crop-only decode followed by resize-only
        // encoding preserves our contract that crop coordinates always refer to original pixels.
        using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)
            .AsTask(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var output = new InMemoryRandomAccessStream();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output).AsTask(cancellationToken).ConfigureAwait(false);
            encoder.SetSoftwareBitmap(bitmap);
            encoder.BitmapTransform.ScaledWidth = outputWidth;
            encoder.BitmapTransform.ScaledHeight = outputHeight;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Linear;
            await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (output.Size <= MaxInlineBytes)
            {
                using var reader = new DataReader(output.GetInputStreamAt(0));
                await reader.LoadAsync((uint)output.Size).AsTask(cancellationToken).ConfigureAwait(false);
                byte[] rendered = new byte[(int)output.Size];
                reader.ReadBytes(rendered);
                return new(rendered, originalWidth, originalHeight, outputWidth, outputHeight, crop,
                    outputWidth != width || outputHeight != height);
            }
            (outputWidth, outputHeight) = Fit(width, height, Math.Max(1, (int)Math.Max(outputWidth, outputHeight) / 2));
        }
    }

    internal static void Validate(uint width, uint height, ImageCrop? crop, int? maxDimension)
    {
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
            throw new PixToolException("invalid_image", "PNG dimensions must be positive and representable as pixel coordinates.");
        if (maxDimension is < 1 or > 4096)
            throw new PixToolException("invalid_arguments", "maxDimension must be between 1 and 4096.");
        if (crop is not null && (crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0 ||
            (long)crop.X + crop.Width > width || (long)crop.Y + crop.Height > height))
            throw new PixToolException("invalid_arguments", "crop must be a positive rectangle within the original image bounds.");
    }

    internal static (uint Width, uint Height) Fit(uint width, uint height, int? maxDimension)
    {
        if (maxDimension is null || Math.Max(width, height) <= maxDimension.Value) return (width, height);
        double scale = (double)maxDimension.Value / Math.Max(width, height);
        return (Math.Max(1u, (uint)Math.Round(width * scale)), Math.Max(1u, (uint)Math.Round(height * scale)));
    }
}

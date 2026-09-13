using System.ComponentModel;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PixMcp.Pix;

/// <summary>A crop rectangle in original pixel coordinates.</summary>
public sealed record ImageCrop(
    [property: Description("Left edge in original pixels.")] int X,
    [property: Description("Top edge in original pixels.")] int Y,
    [property: Description("Width in original pixels (positive).")] int Width,
    [property: Description("Height in original pixels (positive).")] int Height);
internal sealed record RenderedImage(byte[] Png, uint OriginalWidth, uint OriginalHeight, uint Width, uint Height,
    ImageCrop? Crop, bool Resized, bool AlphaIgnored = false);

/// <summary>Transforms detached PNG data using the Windows codecs; never enters the PIX worker.</summary>
internal static class ImageRenderer
{
    internal const int MaxInlineBytes = 4 * 1024 * 1024;
    private const ulong MaxDecodedPixels = 64 * 1024 * 1024;

    internal static async Task<RenderedImage> Render(byte[] png, ImageCrop? crop = null, int? maxDimension = null,
        bool ignoreAlpha = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (originalWidth, originalHeight) = Tools.PreviewTools.PngDimensions(png);
        Validate(originalWidth, originalHeight, crop, maxDimension);
        uint width = (uint)(crop?.Width ?? (int)originalWidth), height = (uint)(crop?.Height ?? (int)originalHeight);
        int? bound = maxDimension ?? (png.Length > MaxInlineBytes ? 1024 : null);
        var (outputWidth, outputHeight) = Fit(width, height, bound);
        if (!ignoreAlpha && crop is null && outputWidth == width && outputHeight == height && png.Length <= MaxInlineBytes)
            return new(png, originalWidth, originalHeight, width, height, null, false);
        if ((ulong)originalWidth * originalHeight > MaxDecodedPixels)
            throw new PixToolException(PixErrors.Codes.ImageTooLarge, "The image exceeds the 64 megapixel decoding limit. Retrieve its original bytes instead.");

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
        // Render-target alpha need not represent opacity. Ignore it during decoding,
        // before premultiplication could destroy useful RGB values where alpha is zero.
        using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,
            ignoreAlpha ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)
            .AsTask(cancellationToken).ConfigureAwait(false);
        if (ignoreAlpha)
        {
            // Ignore prevents alpha multiplication while decoding, but the Windows
            // PNG encoder still reads the stored alpha bytes when scaling/encoding.
            // Make those bytes opaque before either operation can consume them.
            var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
            var buffer = new Windows.Storage.Streams.Buffer((uint)pixels.Length);
            bitmap.CopyToBuffer(buffer);
            using (DataReader reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            cancellationToken.ThrowIfCancellationRequested();
            using var writer = new DataWriter();
            writer.WriteBytes(pixels);
            bitmap.CopyFromBuffer(writer.DetachBuffer());
        }
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
                    outputWidth != width || outputHeight != height, ignoreAlpha);
            }
            (outputWidth, outputHeight) = Fit(width, height, Math.Max(1, (int)Math.Max(outputWidth, outputHeight) / 2));
        }
    }

    /// <summary>
    /// Shrinks a rendered image until its base64 form fits <paramref name="maxBase64Bytes"/>, halving the longest edge
    /// down to 64 pixels. Returns the image and whether the budget (not the caller) chose the size.
    /// </summary>
    internal static async Task<(RenderedImage Image, bool BudgetLimited)> FitToBudget(RenderedImage rendered, byte[] original, int maxBase64Bytes, CancellationToken cancellationToken)
    {
        bool limited = false;
        while (Base64Length(rendered.Png.Length) > maxBase64Bytes && Math.Max(rendered.Width, rendered.Height) > 64)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int next = Math.Max(64, (int)Math.Max(rendered.Width, rendered.Height) / 2);
            rendered = await Render(original, rendered.Crop, next, rendered.AlphaIgnored, cancellationToken).ConfigureAwait(false);
            limited = true;
        }
        return (rendered, limited);
    }

    internal static int Base64Length(int bytes) => checked((bytes + 2) / 3 * 4);

    internal static void Validate(uint width, uint height, ImageCrop? crop, int? maxDimension)
    {
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
            throw new PixToolException(PixErrors.Codes.InvalidImage, "PNG dimensions must be positive and representable as pixel coordinates.");
        if (maxDimension is < 1 or > 4096)
            throw new PixToolException(PixErrors.Codes.InvalidArguments, "maxDimension must be between 1 and 4096.");
        if (crop is not null && (crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0 ||
            (long)crop.X + crop.Width > width || (long)crop.Y + crop.Height > height))
            throw new PixToolException(PixErrors.Codes.InvalidArguments, "crop must be a positive rectangle within the original image bounds.");
    }

    internal static (uint Width, uint Height) Fit(uint width, uint height, int? maxDimension)
    {
        if (maxDimension is null || Math.Max(width, height) <= maxDimension.Value) return (width, height);
        double scale = (double)maxDimension.Value / Math.Max(width, height);
        return (Math.Max(1u, (uint)Math.Round(width * scale)), Math.Max(1u, (uint)Math.Round(height * scale)));
    }
}

namespace Security_Camera_Backend.Services.Security;

/// <summary>Supported upload formats. The wire format is sniffed from the payload, never trusted
/// from the declared <c>Content-Type</c> alone.</summary>
public enum ImageFormat
{
    Unknown = 0,
    Jpeg,
    Png,
    WebP
}

/// <summary>
/// Magic-byte validation for uploaded images.
///
/// A declared <c>Content-Type</c> is a client claim: without inspecting the bytes, an attacker can
/// store arbitrary payloads on the captures volume and hand them to cv-service. Every upload is
/// therefore sniffed before it is written to disk or forwarded.
/// </summary>
public static class ImageSignatureValidator
{
    /// <summary>Longest signature prefix that has to be buffered to classify a payload.</summary>
    public const int HeaderBytesRequired = 12;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Detects the container format from the leading bytes of a payload.</summary>
    public static ImageFormat Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return ImageFormat.Jpeg;
        }

        if (header.Length >= PngSignature.Length && header[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            return ImageFormat.Png;
        }

        // RIFF....WEBP
        if (header.Length >= 12 &&
            header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F' &&
            header[8] == 'W' && header[9] == 'E' && header[10] == 'B' && header[11] == 'P')
        {
            return ImageFormat.WebP;
        }

        return ImageFormat.Unknown;
    }

    public static bool IsJpeg(ReadOnlySpan<byte> header) => Detect(header) == ImageFormat.Jpeg;

    /// <summary>Maps a detected format to the content type the backend is willing to store.</summary>
    public static string? ToContentType(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "image/jpeg",
        ImageFormat.Png => "image/png",
        ImageFormat.WebP => "image/webp",
        _ => null
    };

    /// <summary>
    /// True when the declared content type (if any) is consistent with the detected format.
    /// An empty declared type is accepted because the payload itself was verified.
    /// </summary>
    public static bool MatchesDeclaredContentType(ImageFormat format, string? declaredContentType)
    {
        if (string.IsNullOrWhiteSpace(declaredContentType))
        {
            return format != ImageFormat.Unknown;
        }

        var mediaType = declaredContentType.Split(';', 2)[0].Trim();

        return format switch
        {
            ImageFormat.Jpeg => mediaType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
                                || mediaType.Equals("image/jpg", StringComparison.OrdinalIgnoreCase),
            ImageFormat.Png => mediaType.Equals("image/png", StringComparison.OrdinalIgnoreCase),
            ImageFormat.WebP => mediaType.Equals("image/webp", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}

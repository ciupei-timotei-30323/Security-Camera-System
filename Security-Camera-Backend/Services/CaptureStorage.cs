using Microsoft.Extensions.Options;
using Security_Camera_Backend.Configuration;
using Security_Camera_Backend.Exceptions;
using Security_Camera_Backend.Services.Security;

namespace Security_Camera_Backend.Services;

public sealed class CaptureStorage : ICaptureStorage
{
    private static readonly HashSet<string> AllowedImageContentTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp" };

    private readonly string _capturesRoot;
    private readonly ILogger<CaptureStorage> _logger;

    public CaptureStorage(IOptions<StorageOptions> storageOptions, ILogger<CaptureStorage> logger)
    {
        _logger = logger;
        _capturesRoot = EnsureTrailingSeparator(
            Path.GetFullPath(storageOptions.Value.CapturesDirectory));
    }

    public async Task<string> SaveFrameAsync(IFormFile? image, CancellationToken ct)
    {
        Validate(image);

        Directory.CreateDirectory(_capturesRoot);

        // The extension is derived from the bytes we actually received, never from the client's
        // file name (which may claim ".jpg" while containing anything at all).
        var detectedFormat = await DetectImageFormatAsync(image!, ct);

        var destinationPath = Path.Combine(
            _capturesRoot,
            $"raw_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}{GetExtension(detectedFormat)}");

        try
        {
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);

            await image!.CopyToAsync(destination, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save uploaded frame to {DestinationPath}; removing partial file.",
                destinationPath);
            TryDelete(destinationPath);
            throw;
        }

        return destinationPath;
    }

    public string ResolveExistingCapturePath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            throw new ValidationAppException(
                "'imagePath' is required.",
                "Analyze request without an imagePath.");
        }

        var fullPath = Path.IsPathRooted(imagePath)
            ? Path.GetFullPath(imagePath)
            : Path.GetFullPath(Path.Combine(_capturesRoot, imagePath));

        if (!fullPath.StartsWith(_capturesRoot, StringComparison.Ordinal))
        {
            throw new ValidationAppException(
                "imagePath must point to a file inside the captures directory.",
                $"Rejected imagePath '{imagePath}' because it resolves outside '{_capturesRoot}'.");
        }

        if (!File.Exists(fullPath))
        {
            throw new ValidationAppException(
                "The requested image does not exist on the captures volume.",
                $"Capture file not found at '{fullPath}'.");
        }

        return fullPath;
    }

    public string? TryResolveExistingCapturePath(string? storedPathOrFileName)
    {
        if (string.IsNullOrWhiteSpace(storedPathOrFileName))
        {
            return null;
        }

        try
        {
            var fullPath = Path.IsPathRooted(storedPathOrFileName)
                ? Path.GetFullPath(storedPathOrFileName)
                : Path.GetFullPath(Path.Combine(_capturesRoot, storedPathOrFileName));

            return fullPath.StartsWith(_capturesRoot, StringComparison.Ordinal) && File.Exists(fullPath)
                ? fullPath
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve capture path {StoredPath}.", storedPathOrFileName);
            return null;
        }
    }

    public void TryDelete(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            var fullPath = Path.IsPathRooted(filePath)
                ? Path.GetFullPath(filePath)
                : Path.GetFullPath(Path.Combine(_capturesRoot, filePath));

            if (!fullPath.StartsWith(_capturesRoot, StringComparison.Ordinal))
            {
                _logger.LogWarning("Refusing to delete '{FilePath}' because it is outside {CapturesRoot}.",
                    filePath, _capturesRoot);
                return;
            }

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                _logger.LogDebug("Deleted capture file {FilePath}", fullPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete capture file {FilePath}", filePath);
        }
    }

    /// <summary>
    /// Reads the payload header and rejects anything that is not a JPEG/PNG/WebP image. Doing this
    /// before the file is created keeps arbitrary payloads (scripts, archives, polyglots) off the
    /// captures volume and out of cv-service.
    /// </summary>
    private static async Task<ImageFormat> DetectImageFormatAsync(IFormFile image, CancellationToken ct)
    {
        var header = new byte[ImageSignatureValidator.HeaderBytesRequired];
        var totalRead = 0;

        await using (var stream = image.OpenReadStream())
        {
            while (totalRead < header.Length)
            {
                var read = await stream.ReadAsync(header.AsMemory(totalRead), ct);

                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }
        }

        var format = ImageSignatureValidator.Detect(header.AsSpan(0, totalRead));

        if (format == ImageFormat.Unknown)
        {
            throw new ValidationAppException(
                "The uploaded file is not a valid JPEG, PNG, or WebP image.",
                $"Rejected upload '{image.FileName}': payload does not start with a supported image signature.");
        }

        if (!ImageSignatureValidator.MatchesDeclaredContentType(format, image.ContentType))
        {
            throw new ValidationAppException(
                "The uploaded file's content type does not match its contents.",
                $"Rejected upload '{image.FileName}': declared '{image.ContentType}' but the payload is {format}.");
        }

        return format;
    }

    private static string GetExtension(ImageFormat format) => format switch
    {
        ImageFormat.Png => ".png",
        ImageFormat.WebP => ".webp",
        _ => ".jpg"
    };

    private static void Validate(IFormFile? image)
    {
        if (image is null)
        {
            throw new ValidationAppException(
                "An image file is required in the 'image' form field.",
                "Missing multipart form field 'image'.");
        }

        if (image.Length <= 0)
        {
            throw new ValidationAppException(
                "The uploaded image is empty.",
                $"Uploaded file '{image.FileName}' has zero length.");
        }

        if (!AllowedImageContentTypes.Contains(image.ContentType))
        {
            throw new ValidationAppException(
                "Unsupported image content type. Allowed types: image/jpeg, image/png, image/webp.",
                $"Unsupported content type '{image.ContentType}' for file '{image.FileName}'.");
        }
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}

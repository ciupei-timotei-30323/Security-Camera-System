using Microsoft.AspNetCore.Http;

namespace Security_Camera_Backend.Services;

/// <summary>
/// Owns the shared captures volume: saving uploaded frames, resolving paths requested
/// by internal callers and deleting files that must not linger on disk.
/// </summary>
public interface ICaptureStorage
{
    /// <summary>
    /// Validates and streams an uploaded image to a collision-safe file inside the captures directory.
    /// </summary>
    /// <returns>Full path of the saved frame.</returns>
    Task<string> SaveFrameAsync(IFormFile? image, CancellationToken ct);

    /// <summary>
    /// Resolves an existing file inside the captures directory. Absolute paths must point inside
    /// the captures directory; relative paths are resolved against it. Throws a
    /// <see cref="Exceptions.ValidationAppException"/> when the file is missing or escapes the volume.
    /// </summary>
    string ResolveExistingCapturePath(string? imagePath);

    /// <summary>
    /// Resolves an existing file inside the captures directory without throwing. Stored values are
    /// normally bare file names, but legacy absolute/relative paths are accepted as well.
    /// </summary>
    string? TryResolveExistingCapturePath(string? storedPathOrFileName);

    /// <summary>Deletes a file on the shared volume; paths outside the captures directory are ignored.</summary>
    void TryDelete(string? filePath);
}

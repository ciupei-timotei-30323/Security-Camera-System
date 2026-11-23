namespace SecurityCameraBackend.IntegrationTests;

/// <summary>Small valid image payload shared by tests that need real magic bytes.</summary>
internal static class TestImages
{
    /// <summary>Minimal JFIF-prefixed JPEG (SOI + APP0 + EOI), enough to pass signature sniffing.</summary>
    public static readonly byte[] Jpeg =
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
        0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9
    ];
}

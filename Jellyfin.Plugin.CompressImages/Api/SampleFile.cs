namespace Jellyfin.Plugin.CompressImages.Api;

/// <summary>
/// A sample file entry for preview display.
/// </summary>
public class SampleFile
{
    /// <summary>
    /// Gets or sets the relative file path.
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the image width in pixels.
    /// </summary>
    public int Width { get; set; }

    /// <summary>
    /// Gets or sets the image height in pixels.
    /// </summary>
    public int Height { get; set; }
}

using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.CompressImages.Api;

/// <summary>
/// Result model for the preview endpoint.
/// </summary>
public class PreviewResult
{
    /// <summary>
    /// Gets or sets the people metadata path.
    /// </summary>
    public string PeoplePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the people folder exists.
    /// </summary>
    public bool Exists { get; set; }

    /// <summary>
    /// Gets or sets the total number of images in the people folder.
    /// </summary>
    public int TotalImageCount { get; set; }

    /// <summary>
    /// Gets or sets the number of oversized images pending compression.
    /// </summary>
    public int PendingImageCount { get; set; }

    /// <summary>
    /// Gets or sets the total size of all images in bytes.
    /// </summary>
    public long TotalSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the total size of oversized images in bytes.
    /// </summary>
    public long PendingSizeBytes { get; set; }

    /// <summary>
    /// Gets sample file entries.
    /// </summary>
    public IReadOnlyList<SampleFile> SampleFiles { get; init; } = [];
}

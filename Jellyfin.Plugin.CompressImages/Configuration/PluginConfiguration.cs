using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.CompressImages.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        MaxWidth = 600;
        MaxHeight = 900;
        Quality = 80;
        MaxFileSizeKB = 0;
    }

    /// <summary>
    /// Gets or sets the maximum width.
    /// </summary>
    public int MaxWidth { get; set; }

    /// <summary>
    /// Gets or sets the maximum height.
    /// </summary>
    public int MaxHeight { get; set; }

    /// <summary>
    /// Gets or sets the image quality percentage.
    /// </summary>
    public int Quality { get; set; }

    /// <summary>
    /// Gets or sets the maximum file size in KB. 0 means no file size limit (only dimensions are checked).
    /// </summary>
    public int MaxFileSizeKB { get; set; }
}

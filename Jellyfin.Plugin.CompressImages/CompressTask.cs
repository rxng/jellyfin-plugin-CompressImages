using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CompressImages;

/// <summary>
/// The task that runs the image compression.
/// </summary>
public class CompressTask : IScheduledTask
{
    private static readonly string[] _imageExtensions = [".jpg", ".jpeg", ".png"];

    private readonly IServerConfigurationManager _configManager;
    private readonly IImageEncoder _imageEncoder;
    private readonly ILogger<CompressTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompressTask"/> class.
    /// </summary>
    /// <param name="configManager">The config manager.</param>
    /// <param name="imageEncoder">The image encoder.</param>
    /// <param name="logger">The logger.</param>
    public CompressTask(
        IServerConfigurationManager configManager,
        IImageEncoder imageEncoder,
        ILogger<CompressTask> logger)
    {
        _configManager = configManager;
        _imageEncoder = imageEncoder;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Compress Images";

    /// <inheritdoc />
    public string Key => "CompressImagesTask";

    /// <inheritdoc />
    public string Description => "Compresses and resizes oversized images in the People metadata folder using Jellyfin's built-in image encoder.";

    /// <inheritdoc />
    public string Category => "Maintenance";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var metadataPath = _configManager.ApplicationPaths.InternalMetadataPath;
        var peoplePath = Path.Combine(metadataPath, "People");

        if (!Directory.Exists(peoplePath))
        {
            _logger.LogWarning("People metadata folder not found at {Path}", peoplePath);
            return Task.CompletedTask;
        }

        var config = Plugin.Instance!.Configuration;
        var maxWidth = Math.Clamp(config.MaxWidth, 1, 10000);
        var maxHeight = Math.Clamp(config.MaxHeight, 1, 10000);
        var quality = Math.Clamp(config.Quality, 1, 100);
        var maxFileSizeBytes = config.MaxFileSizeKB > 0 ? config.MaxFileSizeKB * 1024L : long.MaxValue;

        var files = FindOversizedImages(peoplePath, maxWidth, maxHeight, maxFileSizeBytes, cancellationToken);

        if (files.Count == 0)
        {
            _logger.LogInformation("No oversized images found in {Path}", peoplePath);
            progress.Report(100);
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "Found {Count} oversized images in {Path} to compress to {Width}x{Height} at quality {Quality}",
            files.Count,
            peoplePath,
            maxWidth,
            maxHeight,
            quality);

        var processed = 0;
        var failed = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (CompressImage(file, maxWidth, maxHeight, quality))
            {
                processed++;
            }
            else
            {
                failed++;
            }

            var done = processed + failed;
            if (done % 100 == 0 || done == files.Count)
            {
                _logger.LogInformation("CompressImages: Compressed {Done}/{Total} images ({Processed} OK, {Failed} failed)", done, files.Count, processed, failed);
            }

            progress.Report((double)done / files.Count * 100);
        }

        _logger.LogInformation("CompressImages: Compression complete. Processed: {Processed}, Failed: {Failed}", processed, failed);

        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks
            }
        ];
    }

    /// <summary>
    /// Checks if a given image exceeds the configured dimension or file size limits.
    /// </summary>
    /// <param name="path">The file path to check.</param>
    /// <param name="maxWidth">Maximum allowed width in pixels.</param>
    /// <param name="maxHeight">Maximum allowed height in pixels.</param>
    /// <param name="maxFileSizeBytes">Maximum allowed file size in bytes.</param>
    /// <returns>True if the image exceeds any of the configured limits.</returns>
    internal bool IsOversized(string path, int maxWidth, int maxHeight, long maxFileSizeBytes)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length == 0)
            {
                return false;
            }

            if (maxFileSizeBytes < long.MaxValue && fileInfo.Length > maxFileSizeBytes)
            {
                return true;
            }

            var dims = _imageEncoder.GetImageSize(path);
            return dims.Width > maxWidth || dims.Height > maxHeight;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read image info for {Path}", path);
            return false;
        }
    }

    private List<string> FindOversizedImages(string peoplePath, int maxWidth, int maxHeight, long maxFileSizeBytes, CancellationToken cancellationToken)
    {
        var candidateList = Directory.EnumerateFiles(peoplePath, "*.*", SearchOption.AllDirectories)
            .Where(f => _imageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();
        _logger.LogInformation("CompressImages: Scanning {Count} candidate images for oversized files", candidateList.Count);

        var result = new List<string>();
        var checked_ = 0;
        foreach (var f in candidateList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsOversized(f, maxWidth, maxHeight, maxFileSizeBytes))
            {
                result.Add(f);
            }

            checked_++;
            if (checked_ % 500 == 0)
            {
                _logger.LogInformation("CompressImages: Scanned {Checked}/{Total} images, {Oversized} oversized so far", checked_, candidateList.Count, result.Count);
            }
        }

        return result;
    }

    private bool CompressImage(string path, int maxWidth, int maxHeight, int quality)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var outputFormat = ext == ".png" ? ImageFormat.Png : ImageFormat.Jpg;

            var tempPath = Path.Combine(
                Path.GetTempPath(),
                string.Concat("jfci_", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), ext));

            try
            {
                var options = new ImageProcessingOptions
                {
                    MaxWidth = maxWidth,
                    MaxHeight = maxHeight,
                    Quality = quality,
                    SupportedOutputFormats = [outputFormat]
                };

                var result = _imageEncoder.EncodeImage(
                    path,
                    File.GetLastWriteTimeUtc(path),
                    tempPath,
                    autoOrient: true,
                    orientation: null,
                    quality: quality,
                    options: options,
                    outputFormat: outputFormat);

                // If encoder returned original path unchanged, no compression was needed
                if (string.Equals(result, path, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // Only replace if the compressed file is actually smaller
                var originalSize = new FileInfo(path).Length;
                var compressedSize = new FileInfo(tempPath).Length;

                if (compressedSize >= originalSize)
                {
                    _logger.LogDebug("Skipping {Path}: compressed size ({Compressed}) >= original ({Original})", path, compressedSize, originalSize);
                    return true;
                }

                File.Move(tempPath, path, overwrite: true);
                _logger.LogDebug("Compressed {Path}: {Original} -> {Compressed}", path, originalSize, compressedSize);
                return true;
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compress {Path}", path);
            return false;
        }
    }
}

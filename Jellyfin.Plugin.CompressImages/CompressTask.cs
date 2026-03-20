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

    private enum CompressResult
    {
        Compressed,
        Skipped,
        Failed
    }

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
    public string Category => "Image Compressor";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var metadataPath = _configManager.ApplicationPaths.InternalMetadataPath;
        var peoplePath = Path.Combine(metadataPath, "People");

        if (!Directory.Exists(peoplePath))
        {
            _logger.LogWarning("People metadata folder not found at {Path}", peoplePath);
            progress.Report(100);
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

        var compressed = 0;
        var skipped = 0;
        var failed = 0;

        var skippedFiles = new List<string>();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = CompressImage(file, maxWidth, maxHeight, quality, maxFileSizeBytes);
            switch (result)
            {
                case CompressResult.Compressed:
                    compressed++;
                    break;
                case CompressResult.Skipped:
                    skipped++;
                    skippedFiles.Add(file);
                    break;
                case CompressResult.Failed:
                    failed++;
                    break;
            }

            var done = compressed + skipped + failed;
            if (done % 100 == 0 || done == files.Count)
            {
                _logger.LogInformation(
                    "CompressImages: Processed {Done}/{Total} images ({Compressed} compressed, {Skipped} skipped, {Failed} failed)",
                    done,
                    files.Count,
                    compressed,
                    skipped,
                    failed);
            }

            progress.Report((double)done / files.Count * 100);
        }

        _logger.LogInformation(
            "CompressImages: Complete. Compressed: {Compressed}, Skipped: {Skipped}, Failed: {Failed}",
            compressed,
            skipped,
            failed);

        foreach (var skippedFile in skippedFiles.Take(20))
        {
            _logger.LogInformation("CompressImages: Skipped {Path}", skippedFile);
        }

        if (skippedFiles.Count > 20)
        {
            _logger.LogInformation(
                "CompressImages: {Count} additional skipped files not shown",
                skippedFiles.Count - 20);
        }

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
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(5).Ticks
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
            if (!fileInfo.Exists || fileInfo.Length == 0)
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

    /// <summary>
    /// Finds oversized images in the People metadata folder.
    /// </summary>
    /// <param name="peoplePath">The People metadata folder path.</param>
    /// <param name="maxWidth">Maximum allowed width in pixels.</param>
    /// <param name="maxHeight">Maximum allowed height in pixels.</param>
    /// <param name="maxFileSizeBytes">Maximum allowed file size in bytes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of oversized image file paths.</returns>
    private List<string> FindOversizedImages(
        string peoplePath,
        int maxWidth,
        int maxHeight,
        long maxFileSizeBytes,
        CancellationToken cancellationToken)
    {
        var candidateList = Directory.EnumerateFiles(peoplePath, "*.*", SearchOption.AllDirectories)
            .Where(f => _imageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        _logger.LogInformation("CompressImages: Scanning {Count} candidate images for oversized files", candidateList.Count);

        var result = new List<string>();
        var checkedCount = 0;

        foreach (var f in candidateList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsOversized(f, maxWidth, maxHeight, maxFileSizeBytes))
            {
                result.Add(f);
            }

            checkedCount++;
            if (checkedCount % 500 == 0)
            {
                _logger.LogInformation(
                    "CompressImages: Scanned {Checked}/{Total} images, {Oversized} oversized so far",
                    checkedCount,
                    candidateList.Count,
                    result.Count);
            }
        }

        return result;
    }

    /// <summary>
    /// Compresses a single image if the encoded result is better and meets the configured limits.
    /// </summary>
    /// <param name="path">The file path to compress.</param>
    /// <param name="maxWidth">Maximum allowed width in pixels.</param>
    /// <param name="maxHeight">Maximum allowed height in pixels.</param>
    /// <param name="quality">Image quality to use.</param>
    /// <param name="maxFileSizeBytes">Maximum allowed file size in bytes.</param>
    /// <returns>The compression result.</returns>
    private CompressResult CompressImage(string path, int maxWidth, int maxHeight, int quality, long maxFileSizeBytes)
    {
        string? tempPath = null;

        try
        {
            var originalInfo = new FileInfo(path);
            if (!originalInfo.Exists || originalInfo.Length == 0)
            {
                return CompressResult.Skipped;
            }

            var originalSizeBytes = originalInfo.Length;
            var originalDimensions = _imageEncoder.GetImageSize(path);

            var originalIsOversized =
                originalDimensions.Width > maxWidth ||
                originalDimensions.Height > maxHeight ||
                (maxFileSizeBytes < long.MaxValue && originalSizeBytes > maxFileSizeBytes);

            if (!originalIsOversized)
            {
                return CompressResult.Skipped;
            }

            var ext = Path.GetExtension(path).ToLowerInvariant();
            var outputFormat = ext == ".png" ? ImageFormat.Png : ImageFormat.Jpg;

            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                _logger.LogWarning("Could not determine directory for {Path}", path);
                return CompressResult.Failed;
            }

            tempPath = Path.Combine(
                directory,
                string.Concat("jfci_", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), ext));

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

            if (string.Equals(result, path, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping {Path}: encoder returned original path (no-op)", path);
                return CompressResult.Skipped;
            }

            if (!File.Exists(tempPath))
            {
                _logger.LogWarning("Failed to compress {Path}: encoder did not produce output file", path);
                return CompressResult.Failed;
            }

            var compressedInfo = new FileInfo(tempPath);
            if (!compressedInfo.Exists || compressedInfo.Length == 0)
            {
                _logger.LogWarning("Failed to compress {Path}: output file is missing or empty", path);
                return CompressResult.Failed;
            }

            var compressedSizeBytes = compressedInfo.Length;
            var compressedDimensions = _imageEncoder.GetImageSize(tempPath);

            var meetsDimensionLimits =
                compressedDimensions.Width <= maxWidth &&
                compressedDimensions.Height <= maxHeight;

            var meetsFileSizeLimit =
                maxFileSizeBytes == long.MaxValue ||
                compressedSizeBytes <= maxFileSizeBytes;

            var dimensionsImproved =
                compressedDimensions.Width < originalDimensions.Width ||
                compressedDimensions.Height < originalDimensions.Height;

            var sizeImproved = compressedSizeBytes < originalSizeBytes;
            var sizeNotWorse = compressedSizeBytes <= originalSizeBytes;

            var better = sizeImproved || (dimensionsImproved && sizeNotWorse);

            if (!meetsDimensionLimits || !meetsFileSizeLimit)
            {
                _logger.LogDebug(
                    "Skipping {Path}: output still exceeds limits. New {NewWidth}x{NewHeight}, {NewBytes} bytes",
                    path,
                    compressedDimensions.Width,
                    compressedDimensions.Height,
                    compressedSizeBytes);
                return CompressResult.Skipped;
            }

            if (!better)
            {
                _logger.LogDebug(
                    "Skipping {Path}: output is not better. Original {OriginalWidth}x{OriginalHeight}, {OriginalBytes} bytes. New {NewWidth}x{NewHeight}, {NewBytes} bytes",
                    path,
                    originalDimensions.Width,
                    originalDimensions.Height,
                    originalSizeBytes,
                    compressedDimensions.Width,
                    compressedDimensions.Height,
                    compressedSizeBytes);
                return CompressResult.Skipped;
            }

            ReplaceFile(tempPath, path);
            tempPath = null;

            return CompressResult.Compressed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compress {Path}", path);
            return CompressResult.Failed;
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>
    /// Replaces the destination file with the source file.
    /// </summary>
    /// <param name="sourcePath">The source file path.</param>
    /// <param name="destinationPath">The destination file path.</param>
    private static void ReplaceFile(string sourcePath, string destinationPath)
    {
        try
        {
            File.Replace(sourcePath, destinationPath, null, true);
            return;
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (IOException)
        {
            if (!File.Exists(sourcePath))
            {
                throw;
            }
        }

        File.Move(sourcePath, destinationPath, overwrite: true);
    }
}

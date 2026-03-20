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
/// Scheduled task that compresses oversized images in the People metadata folder.
/// </summary>
public class CompressTask : IScheduledTask
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png"
    };

    private readonly IServerConfigurationManager _configManager;
    private readonly IImageEncoder _imageEncoder;
    private readonly ILogger<CompressTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompressTask"/> class.
    /// </summary>
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
        var maxFileSizeBytes = config.MaxFileSizeKB > 0
            ? config.MaxFileSizeKB * 1024L
            : long.MaxValue;

        var allFiles = EnumerateCandidateImages(peoplePath).ToList();
        if (allFiles.Count == 0)
        {
            _logger.LogInformation("No candidate images found in {Path}", peoplePath);
            progress.Report(100);
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "CompressImages: Scanning {Count} candidate images in {Path} with limits {Width}x{Height}, quality {Quality}, max size {MaxFileSizeBytes} bytes",
            allFiles.Count,
            peoplePath,
            maxWidth,
            maxHeight,
            quality,
            maxFileSizeBytes == long.MaxValue ? -1 : maxFileSizeBytes);

        var oversizedFiles = new List<string>();
        var scannedCount = 0;

        foreach (var file in allFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsOversized(file, maxWidth, maxHeight, maxFileSizeBytes))
            {
                oversizedFiles.Add(file);
            }

            scannedCount++;
            if (scannedCount % 500 == 0 || scannedCount == allFiles.Count)
            {
                _logger.LogInformation(
                    "CompressImages: Scanned {Scanned}/{Total}, oversized so far: {Oversized}",
                    scannedCount,
                    allFiles.Count,
                    oversizedFiles.Count);
            }

            progress.Report((double)scannedCount / allFiles.Count * 20.0);
        }

        if (oversizedFiles.Count == 0)
        {
            _logger.LogInformation("No oversized images found in {Path}", peoplePath);
            progress.Report(100);
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "CompressImages: Found {Count} oversized images to process",
            oversizedFiles.Count);

        var improved = 0;
        var skipped = 0;
        var failed = 0;

        for (var i = 0; i < oversizedFiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = oversizedFiles[i];
            var result = CompressImage(file, maxWidth, maxHeight, quality, maxFileSizeBytes);

            switch (result)
            {
                case CompressResult.Improved:
                    improved++;
                    break;
                case CompressResult.Skipped:
                    skipped++;
                    break;
                case CompressResult.Failed:
                    failed++;
                    break;
            }

            var done = i + 1;
            if (done % 100 == 0 || done == oversizedFiles.Count)
            {
                _logger.LogInformation(
                    "CompressImages: Processed {Done}/{Total} oversized images ({Improved} improved, {Skipped} skipped, {Failed} failed)",
                    done,
                    oversizedFiles.Count,
                    improved,
                    skipped,
                    failed);
            }

            progress.Report(20.0 + ((double)done / oversizedFiles.Count * 80.0));
        }

        _logger.LogInformation(
            "CompressImages: Complete. Improved: {Improved}, Skipped: {Skipped}, Failed: {Failed}",
            improved,
            skipped,
            failed);

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
    /// Returns true when the image exceeds width, height, or file size limits.
    /// </summary>
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

            var size = _imageEncoder.GetImageSize(path);
            return size.Width > maxWidth || size.Height > maxHeight;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read image info for {Path}", path);
            return false;
        }
    }

    private IEnumerable<string> EnumerateCandidateImages(string rootPath)
    {
        return Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path)));
    }

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

            var extension = Path.GetExtension(path);
            var outputFormat = string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
                ? ImageFormat.Png
                : ImageFormat.Jpg;

            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                _logger.LogWarning("Could not determine directory for {Path}", path);
                return CompressResult.Failed;
            }

            tempPath = Path.Combine(
                directory,
                string.Concat("jfci_", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), extension));

            var options = new ImageProcessingOptions
            {
                MaxWidth = maxWidth,
                MaxHeight = maxHeight,
                Quality = quality,
                SupportedOutputFormats = [outputFormat]
            };

            var encoderResultPath = _imageEncoder.EncodeImage(
                path,
                File.GetLastWriteTimeUtc(path),
                tempPath,
                autoOrient: true,
                orientation: null,
                quality: quality,
                options: options,
                outputFormat: outputFormat);

            if (string.Equals(encoderResultPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping {Path}: encoder returned original path", path);
                return CompressResult.Skipped;
            }

            if (!File.Exists(tempPath))
            {
                _logger.LogWarning("Skipping {Path}: encoder did not produce output file", path);
                return CompressResult.Failed;
            }

            var newInfo = new FileInfo(tempPath);
            if (!newInfo.Exists || newInfo.Length == 0)
            {
                _logger.LogWarning("Skipping {Path}: output file is missing or empty", path);
                return CompressResult.Failed;
            }

            var newSizeBytes = newInfo.Length;
            var newDimensions = _imageEncoder.GetImageSize(tempPath);

            var meetsDimensionLimits =
                newDimensions.Width <= maxWidth &&
                newDimensions.Height <= maxHeight;

            var meetsFileSizeLimit =
                maxFileSizeBytes == long.MaxValue ||
                newSizeBytes <= maxFileSizeBytes;

            var dimensionsImproved =
                newDimensions.Width < originalDimensions.Width ||
                newDimensions.Height < originalDimensions.Height;

            var sizeImproved = newSizeBytes < originalSizeBytes;
            var sizeNotWorse = newSizeBytes <= originalSizeBytes;

            var better = sizeImproved || (dimensionsImproved && sizeNotWorse);

            if (!meetsDimensionLimits || !meetsFileSizeLimit)
            {
                _logger.LogDebug(
                    "Skipping {Path}: output still exceeds limits. New {NewWidth}x{NewHeight}, {NewBytes} bytes",
                    path,
                    newDimensions.Width,
                    newDimensions.Height,
                    newSizeBytes);
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
                    newDimensions.Width,
                    newDimensions.Height,
                    newSizeBytes);
                return CompressResult.Skipped;
            }

            ReplaceFile(tempPath, path);

            /*_logger.LogInformation(
                "Improved {Path}: {OriginalWidth}x{OriginalHeight}, {OriginalBytes} bytes -> {NewWidth}x{NewHeight}, {NewBytes} bytes",
                path,
                originalDimensions.Width,
                originalDimensions.Height,
                originalSizeBytes,
                newDimensions.Width,
                newDimensions.Height,
                newSizeBytes);*/

            tempPath = null;
            return CompressResult.Improved;
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

    private static void ReplaceFile(string sourcePath, string destinationPath)
    {
        try
        {
            File.Replace(sourcePath, destinationPath, destinationBackupName: null, ignoreMetadataErrors: true);
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

    private enum CompressResult
    {
        Improved,
        Skipped,
        Failed
    }
}
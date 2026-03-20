using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CompressImages.Api;

/// <summary>
/// API controller for people image compression preview.
/// </summary>
[ApiController]
[Route("CompressImages")]
[Authorize(Policy = Policies.RequiresElevation)]
public class CompressImagesController : ControllerBase
{
    private static readonly string[] _imageExtensions = [".jpg", ".jpeg", ".png"];

    private readonly IServerConfigurationManager _configManager;
    private readonly IImageEncoder _imageEncoder;
    private readonly ILogger<CompressImagesController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompressImagesController"/> class.
    /// </summary>
    /// <param name="configManager">The server configuration manager.</param>
    /// <param name="imageEncoder">The image encoder.</param>
    /// <param name="logger">The logger.</param>
    public CompressImagesController(
        IServerConfigurationManager configManager,
        IImageEncoder imageEncoder,
        ILogger<CompressImagesController> logger)
    {
        _configManager = configManager;
        _imageEncoder = imageEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Streams a preview of images that would be processed by the compression task.
    /// Returns newline-delimited JSON: progress lines followed by the final result.
    /// </summary>
    /// <param name="sampleLimit">Maximum number of sample file paths to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A streaming NDJSON response.</returns>
    [HttpGet("Preview")]
    public async Task GetPreview([FromQuery] int sampleLimit = 20, CancellationToken cancellationToken = default)
    {
        Response.ContentType = "application/x-ndjson";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            var peoplePath = GetPeoplePath();
            _logger.LogInformation("CompressImages: Preview endpoint called, people path: {Path}", peoplePath ?? "null");

            if (peoplePath is null || !Directory.Exists(peoplePath))
            {
                var notFound = new PreviewResult
                {
                    PeoplePath = peoplePath ?? "Unknown",
                    Exists = false
                };
                await SendLine(notFound, cancellationToken).ConfigureAwait(false);
                return;
            }

            await SendLine(new { Status = "Enumerating image files..." }, cancellationToken).ConfigureAwait(false);

            var config = Plugin.Instance!.Configuration;
            var maxWidth = Math.Clamp(config.MaxWidth, 1, 10000);
            var maxHeight = Math.Clamp(config.MaxHeight, 1, 10000);
            var maxFileSizeBytes = config.MaxFileSizeKB > 0 ? config.MaxFileSizeKB * 1024L : long.MaxValue;

            var allImages = Directory.EnumerateFiles(peoplePath, "*.*", SearchOption.AllDirectories)
                .Where(f => _imageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();

            _logger.LogInformation("CompressImages: Found {Count} images in {Path}", allImages.Count, peoplePath);

            await SendLine(new { Status = "Found " + allImages.Count + " images. Checking dimensions..." }, cancellationToken).ConfigureAwait(false);

            var oversized = new List<string>();
            var dimCache = new Dictionary<string, (int Width, int Height)>();
            long totalSize = 0;
            long oversizedSize = 0;
            var checked_ = 0;

            foreach (var file in allImages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var info = new FileInfo(file);
                    if (info.Length == 0)
                    {
                        checked_++;
                        continue;
                    }

                    totalSize += info.Length;

                    var needsCompression = false;
                    if (maxFileSizeBytes < long.MaxValue && info.Length > maxFileSizeBytes)
                    {
                        needsCompression = true;
                    }
                    else
                    {
                        var dims = _imageEncoder.GetImageSize(file);
                        dimCache[file] = (dims.Width, dims.Height);
                        if (dims.Width > maxWidth || dims.Height > maxHeight)
                        {
                            needsCompression = true;
                        }
                    }

                    if (needsCompression)
                    {
                        oversized.Add(file);
                        oversizedSize += info.Length;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "CompressImages: Could not read {File}", file);
                }

                checked_++;
                if (checked_ % 200 == 0)
                {
                    _logger.LogInformation("CompressImages: Checked {Checked}/{Total}, {Oversized} oversized", checked_, allImages.Count, oversized.Count);
                    var progress = new { Checked = checked_, Total = allImages.Count, Oversized = oversized.Count };
                    await SendLine(progress, cancellationToken).ConfigureAwait(false);
                }
            }

            sampleLimit = Math.Clamp(sampleLimit, 1, 200);

            var samples = oversized
                .Take(sampleLimit)
                .Select(f =>
                {
                    var info = new FileInfo(f);
                    int width = 0, height = 0;
                    if (dimCache.TryGetValue(f, out var cached))
                    {
                        width = cached.Width;
                        height = cached.Height;
                    }
                    else
                    {
                        try
                        {
                            var dims = _imageEncoder.GetImageSize(f);
                            width = dims.Width;
                            height = dims.Height;
                        }
                        catch
                        {
                            // Dimensions unavailable
                        }
                    }

                    return new SampleFile
                    {
                        RelativePath = Path.GetRelativePath(peoplePath, f),
                        SizeBytes = info.Length,
                        Width = width,
                        Height = height
                    };
                })
                .ToList();

            var result = new PreviewResult
            {
                PeoplePath = peoplePath,
                Exists = true,
                TotalImageCount = allImages.Count,
                PendingImageCount = oversized.Count,
                TotalSizeBytes = totalSize,
                PendingSizeBytes = oversizedSize,
                SampleFiles = samples
            };
            await SendLine(result, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "CompressImages: Preview complete. {Oversized}/{Total} oversized, totalSize={TotalSize}, oversizedSize={OversizedSize}",
                oversized.Count,
                allImages.Count,
                totalSize,
                oversizedSize);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CompressImages: Preview failed with exception");
            try
            {
                await SendLine(new { Error = ex.Message }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Response may already be closed
            }
        }
    }

    private async Task SendLine(object data, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(data);
        await Response.WriteAsync(json + "\n", cancellationToken).ConfigureAwait(false);
        await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private string? GetPeoplePath()
    {
        var metadataPath = _configManager.ApplicationPaths.InternalMetadataPath;
        if (string.IsNullOrEmpty(metadataPath))
        {
            return null;
        }

        return Path.Combine(metadataPath, "People");
    }
}

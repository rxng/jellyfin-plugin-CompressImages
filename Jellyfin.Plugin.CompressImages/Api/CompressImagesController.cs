using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
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
[Produces(MediaTypeNames.Application.Json)]
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
    /// Gets a preview of images that would be processed by the compression task.
    /// </summary>
    /// <param name="sampleLimit">Maximum number of sample file paths to return.</param>
    /// <returns>Preview info including count, total size, and sample paths.</returns>
    [HttpGet("Preview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PreviewResult> GetPreview([FromQuery] int sampleLimit = 20)
    {
        _logger.LogInformation("CompressImages: Preview endpoint called");

        try
        {
            var peoplePath = GetPeoplePath();
            _logger.LogInformation("CompressImages: People path resolved to {Path}", peoplePath ?? "null");

            if (peoplePath is null || !Directory.Exists(peoplePath))
            {
                _logger.LogWarning("CompressImages: People folder not found at {Path}", peoplePath ?? "null");
                return Ok(new PreviewResult
                {
                    PeoplePath = peoplePath ?? "Unknown",
                    Exists = false
                });
            }

            var config = Plugin.Instance!.Configuration;
            var maxWidth = Math.Clamp(config.MaxWidth, 1, 10000);
            var maxHeight = Math.Clamp(config.MaxHeight, 1, 10000);
            var maxFileSizeBytes = config.MaxFileSizeKB > 0 ? config.MaxFileSizeKB * 1024L : long.MaxValue;

            _logger.LogInformation("CompressImages: Config - MaxWidth={MaxWidth}, MaxHeight={MaxHeight}, MaxFileSizeKB={MaxFileSizeKB}", maxWidth, maxHeight, config.MaxFileSizeKB);

            var allImages = Directory.EnumerateFiles(peoplePath, "*.*", SearchOption.AllDirectories)
                .Where(f => _imageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();

            _logger.LogInformation("CompressImages: Found {Count} total images in {Path}", allImages.Count, peoplePath);

            var oversized = new List<string>();
            long totalSize = 0;
            long oversizedSize = 0;
            var checked_ = 0;

            foreach (var file in allImages)
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length == 0)
                    {
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
                if (checked_ % 500 == 0)
                {
                    _logger.LogInformation("CompressImages: Checked {Checked}/{Total} images, {Oversized} oversized so far", checked_, allImages.Count, oversized.Count);
                }
            }

            _logger.LogInformation("CompressImages: Scan complete. {Oversized}/{Total} oversized, totalSize={TotalSize}, oversizedSize={OversizedSize}", oversized.Count, allImages.Count, totalSize, oversizedSize);

            sampleLimit = Math.Clamp(sampleLimit, 1, 200);

            var samples = oversized
                .Take(sampleLimit)
                .Select(f =>
                {
                    var info = new FileInfo(f);
                    int width = 0, height = 0;
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

                    return new SampleFile
                    {
                        RelativePath = Path.GetRelativePath(peoplePath, f),
                        SizeBytes = info.Length,
                        Width = width,
                        Height = height
                    };
                })
                .ToList();

            return Ok(new PreviewResult
            {
                PeoplePath = peoplePath,
                Exists = true,
                TotalImageCount = allImages.Count,
                PendingImageCount = oversized.Count,
                TotalSizeBytes = totalSize,
                PendingSizeBytes = oversizedSize,
                LastRunUtc = config.LastRunUtc,
                SampleFiles = samples
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CompressImages: Preview failed with exception");
            throw;
        }
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

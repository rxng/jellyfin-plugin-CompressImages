# Jellyfin Compress Images Plugin

A Jellyfin plugin that automatically compresses oversized images in the **People** metadata folder to reduce disk usage. Uses Jellyfin's built-in image encoder (SkiaSharp) — no external tools like ImageMagick required.

## Features

- **Automatic scheduled compression** — runs every 24 hours as a Jellyfin scheduled task
- **Dimension limits** — resizes images that exceed a configurable max width/height (default: 600×900 px)
- **File size limits** — optionally re-encodes files above a configurable size threshold
- **Quality control** — configurable JPEG/PNG encoding quality (default: 75%)
- **Smart skipping** — only replaces an image if the compressed version is actually smaller
- **Incremental runs** — tracks the last run timestamp so subsequent runs only check newly modified images
- **Preview & test** — built-in preview button in the config page to scan and show which images would be compressed, without making any changes
- **No external dependencies** — uses Jellyfin's built-in SkiaSharp encoder, works out of the box in Docker

## Requirements

- **Jellyfin 10.11.0 or later**

## Installation

### From Plugin Repository (recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**
2. Add a new repository with the URL:
   ```
   https://raw.githubusercontent.com/rxng/jellyfin-plugin-CompressImages/master/manifest.json
   ```
3. Go to **Catalog**, find **Compress Images** under the General category, and install it
4. Restart Jellyfin

### Manual Installation

1. Download the latest `compress-images-v*.zip` from the [Releases](https://github.com/rxng/jellyfin-plugin-CompressImages/releases) page
2. Extract the `.dll` file into your Jellyfin plugins directory:
   - **Docker**: `/config/plugins/Jellyfin.Plugin.CompressImages/`
   - **Linux**: `~/.local/share/jellyfin/plugins/Jellyfin.Plugin.CompressImages/`
   - **Windows**: `%LOCALAPPDATA%\jellyfin\plugins\Jellyfin.Plugin.CompressImages\`
3. Restart Jellyfin

## Usage

1. Go to **Dashboard → Plugins** and click on **Compress Images** to open the settings page
2. Configure the compression settings:
   | Setting | Default | Description |
   |---------|---------|-------------|
   | Max Width | 600 px | Images wider than this are resized down |
   | Max Height | 900 px | Images taller than this are resized down |
   | Quality | 75% | JPEG/PNG encoding quality (lower = smaller files) |
   | Max File Size | 0 KB | Re-encode files above this size regardless of dimensions (0 = disabled) |
3. Click **Save**
4. (Optional) Click **Preview Oversized Images** to see which images in your People folder exceed the configured limits — this is read-only and won't modify anything
5. The compression task runs automatically every 24 hours. You can also trigger it manually from **Dashboard → Scheduled Tasks → Compress Images**

## Building from Source

```bash
git clone https://github.com/rxng/jellyfin-plugin-CompressImages.git
cd jellyfin-plugin-CompressImages
dotnet publish --configuration=Release Jellyfin.Plugin.CompressImages.sln
```

The compiled DLL will be at `Jellyfin.Plugin.CompressImages/bin/Release/net9.0/publish/Jellyfin.Plugin.CompressImages.dll`.

## License

Licensed under the [GPL v3 License](LICENSE).

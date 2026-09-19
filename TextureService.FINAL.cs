using System.Collections.Concurrent;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;

namespace NovaSparx.Backend;

/// <summary>
/// Decodes Fortnite UTexture assets into browser-ready PNGs.
///
/// CUE4Parse does the Unreal texture decode.
/// CUE4Parse-Conversion does the PNG encoding.
/// NovaSparx only controls limits, caching and API-safe output.
/// </summary>
public sealed class TextureService
{
    private readonly LiveProviderService _provider;
    private readonly ILogger<TextureService> _log;

    private readonly SemaphoreSlim _decodeGate;

    private readonly ConcurrentDictionary<string, CacheEntry>
        _cache =
            new(StringComparer.OrdinalIgnoreCase);

    private sealed record CacheEntry(
        DateTimeOffset CreatedAt,
        TexturePayload Value);

    private static readonly bool LowMemoryMode =
        bool.TryParse(
            Environment.GetEnvironmentVariable(
                "NOVASPARX_LOW_MEMORY_MODE"),
            out var lowMemoryMode) &&
        lowMemoryMode;

    private static readonly TimeSpan CacheTtl =
        TimeSpan.FromMinutes(
            int.TryParse(
                Environment.GetEnvironmentVariable(
                    "NOVASPARX_TEXTURE_CACHE_MINUTES"),
                out var minutes)
                ? Math.Clamp(minutes, 1, 180)
                : LowMemoryMode
                    ? 1
                    : 30);

    private static readonly int MaxCacheEntries =
        int.TryParse(
            Environment.GetEnvironmentVariable(
                "NOVASPARX_TEXTURE_CACHE_MAX_ENTRIES"),
            out var cacheEntries)
            ? Math.Clamp(cacheEntries, 0, 64)
            : LowMemoryMode
                ? 1
                : 12;

    private static readonly long MaxCacheBytes =
        long.TryParse(
            Environment.GetEnvironmentVariable(
                "NOVASPARX_TEXTURE_CACHE_MAX_BYTES"),
            out var cacheBytes)
            ? Math.Clamp(
                cacheBytes,
                0,
                128L * 1024 * 1024)
            : LowMemoryMode
                ? 4L * 1024 * 1024
                : 48L * 1024 * 1024;

    private static readonly int MaxMipSize =
        int.TryParse(
            Environment.GetEnvironmentVariable(
                "NOVASPARX_TEXTURE_MAX_SIZE"),
            out var maxSize)
            ? Math.Clamp(maxSize, 64, 4096)
            : 2048;

    private static readonly int MaxEncodedBytes =
        int.TryParse(
            Environment.GetEnvironmentVariable(
                "NOVASPARX_TEXTURE_MAX_BYTES"),
            out var maxBytes)
            ? Math.Clamp(
                maxBytes,
                256 * 1024,
                32 * 1024 * 1024)
            : 12 * 1024 * 1024;

    public TextureService(
        LiveProviderService provider,
        ILogger<TextureService> log)
    {
        _provider = provider;
        _log = log;

        var concurrency =
            int.TryParse(
                Environment.GetEnvironmentVariable(
                    "NOVASPARX_TEXTURE_CONCURRENCY"),
                out var parsed)
                ? Math.Clamp(parsed, 1, 2)
                : 1;

        _decodeGate =
            new SemaphoreSlim(
                concurrency,
                concurrency);
    }

    public int CacheEntries =>
        _cache.Count;

    public async Task<TexturePayload?>
        DecodePngAsync(
            string rawPath,
            CancellationToken cancellationToken)
    {
        var canonical =
            AssetPathResolver.Canonicalize(
                rawPath);

        if (canonical.Length == 0)
            return null;

        if (_cache.TryGetValue(
                canonical,
                out var cached) &&
            DateTimeOffset.UtcNow -
            cached.CreatedAt < CacheTtl)
        {
            return cached.Value;
        }

        var loaded =
            await _provider.LoadObjectAsync(
                rawPath,
                cancellationToken);

        if (loaded is null)
            return null;

        if (loaded.Value.Object is not UTexture texture)
            return null;

        await _decodeGate.WaitAsync(
            cancellationToken);

        try
        {
            if (_cache.TryGetValue(
                    canonical,
                    out cached) &&
                DateTimeOffset.UtcNow -
                cached.CreatedAt < CacheTtl)
            {
                return cached.Value;
            }

            cancellationToken
                .ThrowIfCancellationRequested();

            CTexture? decoded =
                null;

            try
            {
                decoded =
                    texture.Decode(
                        MaxMipSize,
                        ETexturePlatform.DesktopMobile);

                if (decoded is null)
                {
                    decoded =
                        texture.Decode(
                            ETexturePlatform.DesktopMobile);
                }

                if (decoded is null)
                    return null;

                cancellationToken
                    .ThrowIfCancellationRequested();

                var png =
                    decoded.Encode(
                        ETextureFormat.Png,
                        false,
                        out var extension);

                if (!extension.Equals(
                        "png",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Texture encoder returned unexpected format: {extension}");
                }

                if (png.Length == 0)
                {
                    throw new InvalidOperationException(
                        "Texture encoder returned an empty PNG.");
                }

                if (png.Length > MaxEncodedBytes)
                {
                    throw new InvalidOperationException(
                        "Decoded texture exceeds the NovaSparx HTTP preview budget " +
                        $"({png.Length:N0} bytes > {MaxEncodedBytes:N0} bytes).");
                }

                var payload =
                    new TexturePayload(
                        Path:
                            canonical,

                        ResolvedPath:
                            loaded.Value.ResolvedPath,

                        ContentType:
                            "image/png",

                        Bytes:
                            png,

                        Width:
                            decoded.Width,

                        Height:
                            decoded.Height);

                cancellationToken
                    .ThrowIfCancellationRequested();

                TrimCacheIfNeeded(
                    payload.Bytes
                        .LongLength);

                if (
                    MaxCacheEntries > 0 &&
                    MaxCacheBytes > 0 &&
                    payload.Bytes
                        .LongLength <=
                    MaxCacheBytes)
                {
                    _cache[canonical] =
                        new CacheEntry(
                            DateTimeOffset.UtcNow,
                            payload);
                }

                return payload;
            }
            catch (Exception ex)
            {
                _log.LogDebug(
                    ex,
                    "Texture decode failed for {Path}.",
                    canonical);

                throw;
            }
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    public void ClearCache()
    {
        _cache.Clear();
    }

    private void TrimCacheIfNeeded(
        long incomingBytes)
    {
        if (
            MaxCacheEntries <= 0 ||
            MaxCacheBytes <= 0)
        {
            _cache.Clear();
            return;
        }

        var now =
            DateTimeOffset.UtcNow;

        foreach (
            var pair in
            _cache.ToArray())
        {
            if (
                now -
                pair.Value.CreatedAt >=
                CacheTtl)
            {
                _cache.TryRemove(
                    pair.Key,
                    out _);
            }
        }

        long CurrentBytes() =>
            _cache.Sum(
                pair =>
                    pair.Value.Value
                        .Bytes.LongLength);

        while (
            _cache.Count >=
                MaxCacheEntries ||
            CurrentBytes() +
                incomingBytes >
                MaxCacheBytes)
        {
            var oldest =
                _cache
                    .OrderBy(
                        pair =>
                            pair.Value.CreatedAt)
                    .Select(
                        pair =>
                            pair.Key)
                    .FirstOrDefault();

            if (
                string.IsNullOrEmpty(
                    oldest))
            {
                break;
            }

            _cache.TryRemove(
                oldest,
                out _);
        }
    }
}

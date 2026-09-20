using System.Net;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using EpicManifestParser;
using EpicManifestParser.UE;

namespace NovaSparx.Backend;

/// <summary>
/// Public data adapters used by NovaSparx.
/// Every endpoint can be overridden with environment variables so a schema/provider
/// change does not require redesigning the Live provider.
/// </summary>
public sealed partial class PublicFortniteSources
{
    private readonly HttpClient _http;
    private readonly ILogger<PublicFortniteSources> _log;
    private readonly string _cacheRoot;

    public PublicFortniteSources(
        HttpClient http,
        ILogger<PublicFortniteSources> log)
    {
        _http = http;
        _log = log;

        _cacheRoot =
            Environment.GetEnvironmentVariable("NOVASPARX_CACHE_DIR") ??
            Path.Combine(Path.GetTempPath(), "novasparx-cache");

        Directory.CreateDirectory(_cacheRoot);
        Directory.CreateDirectory(ManifestCache);
        Directory.CreateDirectory(ChunkCache);
        Directory.CreateDirectory(MappingsCache);
        Directory.CreateDirectory(TocCache);
    }

    public string CacheRoot => _cacheRoot;
    public string ManifestCache => Path.Combine(_cacheRoot, "manifests");
    public string ChunkCache => Path.Combine(_cacheRoot, "chunks");
    public string MappingsCache => Path.Combine(_cacheRoot, "mappings");
    public string TocCache => Path.Combine(_cacheRoot, "uondemandtoc");

    private static readonly long MaxManifestDownloadBytes =
        ReadByteLimit(
            "NOVASPARX_MAX_MANIFEST_DOWNLOAD_BYTES",
            64L * 1024 * 1024,
            4L * 1024 * 1024,
            192L * 1024 * 1024);

    private static readonly long MaxMetadataDownloadBytes =
        ReadByteLimit(
            "NOVASPARX_MAX_METADATA_DOWNLOAD_BYTES",
            4L * 1024 * 1024,
            64L * 1024,
            32L * 1024 * 1024);

    private static readonly long MaxMappingsDownloadBytes =
        ReadByteLimit(
            "NOVASPARX_MAX_MAPPINGS_DOWNLOAD_BYTES",
            24L * 1024 * 1024,
            1L * 1024 * 1024,
            96L * 1024 * 1024);

    private static readonly long MaxMappingsExpandedBytes =
        ReadByteLimit(
            "NOVASPARX_MAX_MAPPINGS_EXPANDED_BYTES",
            48L * 1024 * 1024,
            2L * 1024 * 1024,
            160L * 1024 * 1024);

    private static readonly long MaxTocDownloadBytes =
        ReadByteLimit(
            "NOVASPARX_MAX_TOC_DOWNLOAD_BYTES",
            24L * 1024 * 1024,
            1L * 1024 * 1024,
            96L * 1024 * 1024);

    private static readonly long MaxAesDownloadBytes =
        ReadByteLimit(
            "NOVASPARX_MAX_AES_DOWNLOAD_BYTES",
            2L * 1024 * 1024,
            32L * 1024,
            8L * 1024 * 1024);

    private static long ReadByteLimit(
        string name,
        long fallback,
        long minimum,
        long maximum)
    {
        return long.TryParse(
                Environment.GetEnvironmentVariable(
                    name),
                out var value)
            ? Math.Clamp(
                value,
                minimum,
                maximum)
            : fallback;
    }

    private static async Task<byte[]>
        ReadStreamBytesBoundedAsync(
            Stream input,
            long maxBytes,
            string label,
            CancellationToken cancellationToken,
            int initialCapacity = 0)
    {
        using var output =
            new MemoryStream(
                Math.Max(
                    0,
                    initialCapacity));

        var buffer =
            new byte[64 * 1024];

        long total = 0;

        while (true)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            var read =
                await input.ReadAsync(
                    buffer.AsMemory(
                        0,
                        buffer.Length),
                    cancellationToken);

            if (read <= 0)
                break;

            total += read;

            if (total > maxBytes)
            {
                throw new InvalidDataException(
                    $"{label} exceeded the configured {maxBytes} byte limit.");
            }

            await output.WriteAsync(
                buffer.AsMemory(
                    0,
                    read),
                cancellationToken);
        }

        if (
            output.TryGetBuffer(
                out var segment) &&
            segment.Offset == 0 &&
            segment.Array is not null &&
            segment.Count ==
                segment.Array.Length)
        {
            return segment.Array;
        }

        return output.ToArray();
    }

    private static async Task<byte[]>
        ReadHttpBytesBoundedAsync(
            HttpResponseMessage response,
            long maxBytes,
            string label,
            CancellationToken cancellationToken)
    {
        var declared =
            response.Content.Headers
                .ContentLength;

        if (
            declared is > 0 &&
            declared.Value > maxBytes)
        {
            throw new InvalidDataException(
                $"{label} declared {declared.Value} bytes, above the configured {maxBytes} byte limit.");
        }

        await using var input =
            await response.Content
                .ReadAsStreamAsync(
                    cancellationToken);

        var initialCapacity =
            declared is > 0 &&
            declared.Value <=
                int.MaxValue
                ? checked((int)declared.Value)
                : 0;

        return await ReadStreamBytesBoundedAsync(
            input,
            maxBytes,
            label,
            cancellationToken,
            initialCapacity);
    }

    private static async Task<byte[]>
        DecompressGzipBoundedAsync(
            byte[] compressed,
            long maxBytes,
            CancellationToken cancellationToken)
    {
        using var input =
            new MemoryStream(
                compressed,
                writable: false);

        using var gzip =
            new GZipStream(
                input,
                CompressionMode.Decompress);

        return await ReadStreamBytesBoundedAsync(
            gzip,
            maxBytes,
            "Mappings decompression",
            cancellationToken);
    }

    private static string NormalizeHttpEndpoint(
        string? raw,
        string fallback,
        bool ensureTrailingSlash = false)
    {
        static Uri? Parse(string? value)
        {
            var clean = value?.Trim().Trim('"');

            if (!Uri.TryCreate(
                    clean,
                    UriKind.Absolute,
                    out var uri) ||
                uri.Scheme is not ("http" or "https") ||
                (
                    uri.Scheme.Equals(
                        "http",
                        StringComparison.OrdinalIgnoreCase) &&
                    !uri.IsLoopback
                ))
            {
                return null;
            }

            return uri;
        }

        var endpoint =
            Parse(raw) ??
            Parse(fallback) ??
            throw new InvalidOperationException(
                "NovaSparx endpoint configuration is not a valid HTTP URL.");

        var output = endpoint.ToString();

        return ensureTrailingSlash
            ? output.TrimEnd('/') + "/"
            : output;
    }

    private static string RequireHttpEndpoint(
        string raw)
    {
        var clean = raw.Trim().Trim('"');

        if (!Uri.TryCreate(
                clean,
                UriKind.Absolute,
                out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            (
                uri.Scheme.Equals(
                    "http",
                    StringComparison.OrdinalIgnoreCase) &&
                !uri.IsLoopback
            ))
        {
            throw new InvalidOperationException(
                "NovaSparx received an invalid or insecure HTTP URL.");
        }

        return uri.ToString();
    }

    private static bool IsPrivateNetworkHost(
        string host)
    {
        host =
            (host ?? string.Empty)
                .Trim()
                .TrimEnd('.');

        if (
            string.IsNullOrWhiteSpace(
                host) ||
            host.Equals(
                "localhost",
                StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(
                ".localhost",
                StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(
                ".local",
                StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(
                ".internal",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(
                host,
                out var address))
        {
            return false;
        }

        if (
            address.IsIPv4MappedToIPv6)
        {
            address =
                address.MapToIPv4();
        }

        if (
            IPAddress.IsLoopback(
                address))
        {
            return true;
        }

        var bytes =
            address.GetAddressBytes();

        if (
            address.AddressFamily ==
            System.Net.Sockets
                .AddressFamily.InterNetwork)
        {
            return
                bytes[0] == 0 ||
                bytes[0] == 10 ||
                bytes[0] == 127 ||
                (
                    bytes[0] == 169 &&
                    bytes[1] == 254
                ) ||
                (
                    bytes[0] == 172 &&
                    bytes[1] >= 16 &&
                    bytes[1] <= 31
                ) ||
                (
                    bytes[0] == 192 &&
                    bytes[1] == 168
                ) ||
                (
                    bytes[0] == 100 &&
                    bytes[1] >= 64 &&
                    bytes[1] <= 127
                ) ||
                bytes[0] >= 224;
        }

        if (
            address.AddressFamily ==
            System.Net.Sockets
                .AddressFamily.InterNetworkV6)
        {
            return
                address.IsIPv6LinkLocal ||
                address.IsIPv6SiteLocal ||
                (
                    bytes.Length > 0 &&
                    (bytes[0] & 0xFE) ==
                    0xFC
                );
        }

        return true;
    }

    private static string RequirePublicHttpsEndpoint(
        string raw)
    {
        var clean =
            RequireHttpEndpoint(
                raw);

        var uri =
            new Uri(
                clean,
                UriKind.Absolute);

        if (
            !uri.Scheme.Equals(
                "https",
                StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(
                uri.UserInfo) ||
            (
                !uri.IsDefaultPort &&
                uri.Port != 443
            ) ||
            IsPrivateNetworkHost(
                uri.Host))
        {
            throw new InvalidOperationException(
                "NovaSparx rejected an unsafe remote metadata URL.");
        }

        return uri.ToString();
    }

    public Uri GetOnDemandHostUri()
    {
        return new Uri(
            NormalizeHttpEndpoint(
                Environment.GetEnvironmentVariable(
                    "NOVASPARX_ONDEMAND_HOST"),
                "https://egdownload.fastly-edge.com/",
                ensureTrailingSlash: true),
            UriKind.Absolute);
    }

    public ManifestParseOptions CreateManifestOptions()
    {
        return new ManifestParseOptions
        {
            ChunkCacheDirectory = ChunkCache,
            ManifestCacheDirectory = ManifestCache,
            ChunkBaseUrl =
                NormalizeHttpEndpoint(
                    Environment.GetEnvironmentVariable(
                        "NOVASPARX_CHUNK_BASE_URL"),
                    "https://egdownload.fastly-edge.com/Builds/Fortnite/CloudDir/",
                    ensureTrailingSlash: true),
            Client = _http,

            // Match the current Fortnite tooling pattern: BuildPatch chunks stay cached
            // in their transport form and CUE4Parse/EpicManifestParser handle decoding.
            CacheChunksAsIs = true,
            Decompressor = CUE4Parse.Compression.Compression.Decompressor
        };
    }

    public async Task<(FBuildPatchAppManifest Manifest, string Version)>
        GetLiveManifestAsync(CancellationToken cancellationToken)
    {
        var direct =
            Environment.GetEnvironmentVariable("NOVASPARX_MANIFEST_URL");

        if (!string.IsNullOrWhiteSpace(direct))
        {
            try
            {
                return await DownloadManifestFromAnyEndpoint(
                    RequireHttpEndpoint(direct),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Configured manifest URL failed; public sources will be tried.");
            }
        }

        // Dilly currently exposes the raw BuildPatch .manifest download URL.
        // The legacy FortniteAPI endpoint is kept as a fallback because it can
        // still supply metadata even when it no longer includes raw bytes.
        var endpoints =
            new List<string>();

        var configuredApi =
            Environment.GetEnvironmentVariable(
                "NOVASPARX_MANIFEST_API");

        if (!string.IsNullOrWhiteSpace(configuredApi))
        {
            try
            {
                endpoints.Add(
                    RequireHttpEndpoint(configuredApi));
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Configured manifest API is invalid; public sources will be tried.");
            }
        }

        endpoints.Add(
            "https://export-service-new.dillyapis.com/v1/manifests");

        endpoints.Add(
            "https://api.fortniteapi.com/v1/manifests");

        Exception? lastError = null;

        foreach (var endpoint in
                 endpoints.Distinct(
                     StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                return await DownloadManifestFromAnyEndpoint(
                    endpoint,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;

                _log.LogWarning(
                    ex,
                    "Fortnite manifest source failed: {Endpoint}",
                    endpoint);
            }
        }

        throw new InvalidOperationException(
            "NovaSparx could not obtain current Fortnite manifest bytes from " +
            "any configured public source.",
            lastError);
    }

    /// <summary>
    /// Optional Fortnite_Studio manifest. It gives NovaSparx another archive source
    /// for UEFN/plugin assets that may not exist in the main Fortnite manifest.
    /// Dilly exposes this same manifest family publicly.
    /// </summary>
    public async Task<(FBuildPatchAppManifest Manifest, string Version)?>
        GetStudioManifestAsync(CancellationToken cancellationToken)
    {
        var endpoint =
            NormalizeHttpEndpoint(
                Environment.GetEnvironmentVariable(
                    "NOVASPARX_STUDIO_MANIFEST_API"),
                "https://export-service-new.dillyapis.com/v1/manifests");

        try
        {
            using var response =
                await _http.GetAsync(
                    endpoint,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
                return null;

            var bytes =
                await ReadHttpBytesBoundedAsync(
                    response,
                    MaxManifestDownloadBytes,
                    "Fortnite Studio manifest source",
                    cancellationToken);

            if (LooksLikeManifest(bytes))
            {
                var direct = FBuildPatchAppManifest.Deserialize(
                    bytes,
                    CreateManifestOptions());
                return (direct, ReadVersion(direct));
            }

            using var doc = JsonDocument.Parse(bytes);

            string? downloadUrl = null;

            Walk(doc.RootElement, element =>
            {
                if (downloadUrl is not null ||
                    element.ValueKind != JsonValueKind.Object)
                    return;

                string? appName = null;
                string? candidateUrl = null;

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                        continue;

                    var value = property.Value.GetString();
                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    var key = property.Name.ToLowerInvariant();

                    if (key is "appname" or "app_name" or "app")
                        appName = value;

                    if (key.Contains("download") || key.Contains("manifest"))
                    {
                        if (Uri.TryCreate(
                            value,
                            UriKind.Absolute,
                            out var uri) &&
                            uri.Scheme is "http" or "https")
                        {
                            candidateUrl = value;
                        }
                    }
                }

                if (appName?.Equals(
                    "Fortnite_Studio",
                    StringComparison.OrdinalIgnoreCase) == true)
                {
                    downloadUrl = candidateUrl;
                }
            });

            if (string.IsNullOrWhiteSpace(downloadUrl))
                return null;

            return await DownloadManifestFromAnyEndpoint(
                RequirePublicHttpsEndpoint(
                    downloadUrl),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Fortnite_Studio manifest source failed.");
            return null;
        }
    }

    public async Task<ExternalTocResult?>
        GetTextureStreamingTocAsync(
            FBuildPatchAppManifest liveManifest,
            CancellationToken cancellationToken)
    {
        try
        {
            var ini = liveManifest.Files.FirstOrDefault(
                file => file.FileName.Equals(
                    "Cloud/IoStoreOnDemand.ini",
                    StringComparison.OrdinalIgnoreCase));

            if (ini is null)
                return null;

            string text;
            await using (var stream = ini.GetStream())
            using (var reader = new StreamReader(stream))
            {
                text = await reader.ReadToEndAsync(cancellationToken);
            }

            var match = TocPathRegex().Match(text);
            if (!match.Success)
                return null;

            var tocPath = match.Groups[1].Value
                .Trim()
                .Trim('"')
                .Replace("\\\"", "", StringComparison.Ordinal)
                .Replace('\\', '/');

            if (string.IsNullOrWhiteSpace(tocPath))
                return null;

            var url = Uri.TryCreate(
                tocPath,
                UriKind.Absolute,
                out var absolute)
                ? absolute.ToString()
                : "https://download.epicgames.com/" +
                  tocPath.TrimStart('/');

            url =
                RequirePublicHttpsEndpoint(
                    url);

            var fileName = Path.GetFileName(
                new Uri(url).AbsolutePath);

            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "IoStoreOnDemand.uondemandtoc";

            var cachePath =
                Path.Combine(TocCache, fileName);

            byte[] bytes;

            var cachedInfo =
                File.Exists(cachePath)
                    ? new FileInfo(cachePath)
                    : null;

            if (
                cachedInfo is not null &&
                cachedInfo.Length > 32 &&
                cachedInfo.Length <=
                    MaxTocDownloadBytes)
            {
                bytes =
                    await File.ReadAllBytesAsync(
                        cachePath,
                        cancellationToken);
            }
            else
            {
                if (
                    cachedInfo is not null &&
                    cachedInfo.Length >
                        MaxTocDownloadBytes)
                {
                    try
                    {
                        File.Delete(
                            cachePath);
                    }
                    catch
                    {
                        // A stale oversized cache entry is ignored.
                    }
                }

                using var response =
                    await _http.GetAsync(
                        url,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return null;

                bytes =
                    await ReadHttpBytesBoundedAsync(
                        response,
                        MaxTocDownloadBytes,
                        "Texture-streaming IoStore TOC",
                        cancellationToken);

                if (bytes.Length < 32)
                    return null;

                await File.WriteAllBytesAsync(
                    cachePath,
                    bytes,
                    cancellationToken);
            }

            return new ExternalTocResult(
                fileName,
                url,
                bytes);
        }
        catch (OperationCanceledException)
            when (
                cancellationToken
                    .IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Texture-streaming IoStore TOC could not be loaded.");
            return null;
        }
    }

    private Task<(FBuildPatchAppManifest Manifest, string Version)>
        DownloadManifestFromAnyEndpoint(
            string url,
            CancellationToken cancellationToken)
    {
        return DownloadManifestFromAnyEndpoint(
            url,
            cancellationToken,
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase),
            depth: 0);
    }

    private async Task<(FBuildPatchAppManifest Manifest, string Version)>
        DownloadManifestFromAnyEndpoint(
            string url,
            CancellationToken cancellationToken,
            HashSet<string> visited,
            int depth)
    {
        if (depth > 5)
        {
            throw new InvalidOperationException(
                "Manifest endpoint recursion limit was reached.");
        }

        url = RequireHttpEndpoint(url);

        if (!visited.Add(url))
        {
            throw new InvalidOperationException(
                "Manifest endpoint loop was detected.");
        }

        using var response =
            await _http.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

        response.EnsureSuccessStatusCode();

        var bytes =
            await ReadHttpBytesBoundedAsync(
                response,
                MaxManifestDownloadBytes,
                "Fortnite manifest source",
                cancellationToken);

        if (LooksLikeManifest(bytes))
        {
            var manifest =
                FBuildPatchAppManifest.Deserialize(
                    bytes,
                    CreateManifestOptions());

            return (manifest, ReadVersion(manifest));
        }

        using var doc = JsonDocument.Parse(bytes);

        var candidates =
            FindManifestCandidates(doc.RootElement);

        foreach (var candidate in
                 candidates.OrderByDescending(x => x.Score))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(candidate.Url) &&
                !candidate.Url.Equals(
                    url,
                    StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return await DownloadManifestFromAnyEndpoint(
                        RequirePublicHttpsEndpoint(
                            candidate.Url),
                        cancellationToken,
                        visited,
                        depth + 1);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogDebug(
                        ex,
                        "Manifest candidate failed: {Url}",
                        candidate.Url);
                }
            }

            if (!string.IsNullOrWhiteSpace(candidate.Id) &&
                depth < 5 &&
                !url.EndsWith(
                    ".manifest",
                    StringComparison.OrdinalIgnoreCase))
            {
                var detail =
                    url.TrimEnd('/') + "/" +
                    Uri.EscapeDataString(candidate.Id);

                try
                {
                    return await DownloadManifestFromAnyEndpoint(
                        detail,
                        cancellationToken,
                        visited,
                        depth + 1);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogDebug(
                        ex,
                        "Manifest detail candidate failed: {Url}",
                        detail);
                }
            }
        }

        throw new InvalidOperationException(
            "NovaSparx received manifest metadata but could not find raw " +
            "Fortnite manifest bytes or a usable .manifest download URL. " +
            "If the public API changed, set NOVASPARX_MANIFEST_URL.");
    }

    private static bool LooksLikeManifest(byte[] bytes)
    {
        if (bytes.Length < 16)
            return false;

        var first =
            bytes.SkipWhile(b => b is 9 or 10 or 13 or 32)
                 .FirstOrDefault();

        return first is not (byte)'{' and not (byte)'[';
    }

    private static string ReadVersion(
        FBuildPatchAppManifest manifest)
    {
        try
        {
            return manifest.Meta?.BuildVersion ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private sealed record ManifestCandidate(
        string? Url,
        string? Id,
        int Score);

    private static List<ManifestCandidate>
        FindManifestCandidates(JsonElement root)
    {
        var list = new List<ManifestCandidate>();

        Walk(root, element =>
        {
            if (element.ValueKind != JsonValueKind.Object)
                return;

            string? url = null;
            string? id = null;
            var score = 0;
            var text =
                element.GetRawText().ToLowerInvariant();

            if (text.Contains("windows"))
                score += 40;

            if (text.Contains("fortnite"))
                score += 25;

            if (text.Contains("live") ||
                text.Contains("latest"))
                score += 10;

            if (text.Contains("android") ||
                text.Contains("ios") ||
                text.Contains("mac"))
                score -= 40;

            if (text.Contains("studio") ||
                text.Contains("uefn"))
                score -= 15;

            foreach (var property in
                     element.EnumerateObject())
            {
                var key =
                    property.Name.ToLowerInvariant();

                if (property.Value.ValueKind ==
                    JsonValueKind.String)
                {
                    var value =
                        property.Value.GetString();

                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    if (Uri.TryCreate(
                        value,
                        UriKind.Absolute,
                        out var uri) &&
                        uri.Scheme is "http" or "https")
                    {
                        var low =
                            value.ToLowerInvariant();

                        if (low.Contains(".manifest") ||
                            key.Contains("download") ||
                            key.Contains("manifest"))
                        {
                            url ??= value;

                            if (low.Contains(".manifest"))
                                score += 60;
                        }
                    }

                    if (key is "manifestid" or "manifest_id")
                    {
                        id = value;
                    }
                    else if (
                        key == "id" &&
                        string.IsNullOrWhiteSpace(id))
                    {
                        id = value;
                    }

                }
                else if (
                    property.Value.ValueKind ==
                    JsonValueKind.Number &&
                    key is "id" or "manifestid" or "manifest_id")
                {
                    id ??=
                        property.Value.GetRawText();
                }
            }

            if (url is not null || id is not null)
                list.Add(
                    new ManifestCandidate(
                        url,
                        id,
                        score));
        });

        return list;
    }

    public async Task<FileUsmapTypeMappingsProvider?>
        GetMappingsAsync(
            CancellationToken cancellationToken)
    {
        var direct =
            Environment.GetEnvironmentVariable(
                "NOVASPARX_MAPPINGS_URL");

        if (!string.IsNullOrWhiteSpace(direct))
            return await DownloadMappings(
                direct,
                cancellationToken);

        var endpoints = new[]
        {
            Environment.GetEnvironmentVariable(
                "NOVASPARX_MAPPINGS_API") ??
            "https://api.fortniteapi.com/v1/mappings",

            "https://api.fortniteapi.com/v1/mappings/legacy"
        };

        foreach (var endpoint in endpoints)
        {
            try
            {
                var safeEndpoint =
                    RequireHttpEndpoint(
                        endpoint);

                using var response =
                    await _http.GetAsync(
                        safeEndpoint,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);

                if (!response.IsSuccessStatusCode)
                    continue;

                using var doc =
                    JsonDocument.Parse(
                        await ReadHttpBytesBoundedAsync(
                            response,
                            MaxMetadataDownloadBytes,
                            "Mappings metadata source",
                            cancellationToken));

                var urls =
                    FindUrls(doc.RootElement)
                        .Where(url =>
                            url.Contains(
                                "usmap",
                                StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(ScoreMappingUrl)
                        .ToArray();

                foreach (var url in urls)
                {
                    var provider =
                        await DownloadMappings(
                            RequirePublicHttpsEndpoint(
                                url),
                            cancellationToken);

                    if (provider is not null)
                        return provider;
                }
            }
            catch (OperationCanceledException)
                when (
                    cancellationToken
                        .IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Mappings source failed: {Endpoint}",
                    endpoint);
            }
        }

        return null;
    }

    private static int ScoreMappingUrl(string url)
    {
        var score = 0;
        var low = url.ToLowerInvariant();

        if (low.EndsWith(".usmap"))
            score += 50;

        if (low.Contains("latest"))
            score += 10;

        if (low.Contains("zstandard") ||
            low.Contains("zstd") ||
            low.EndsWith(".zst"))
            score -= 20;

        return score;
    }

    private async Task<FileUsmapTypeMappingsProvider?>
        DownloadMappings(
            string url,
            CancellationToken cancellationToken)
    {
        url =
            RequireHttpEndpoint(
                url);

        using var response =
            await _http.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

        response.EnsureSuccessStatusCode();

        var bytes =
            await ReadHttpBytesBoundedAsync(
                response,
                MaxMappingsDownloadBytes,
                "Mappings file",
                cancellationToken);

        if (bytes.Length < 32)
            return null;

        if (IsGzip(bytes))
        {
            bytes =
                await DecompressGzipBoundedAsync(
                    bytes,
                    MaxMappingsExpandedBytes,
                    cancellationToken);
        }

        // Keep the adapter deterministic: do not save a compressed .zst blob as .usmap.
        if (IsZstd(bytes))
        {
            _log.LogWarning(
                "Mappings candidate is Zstandard-compressed; " +
                "set NOVASPARX_MAPPINGS_URL to a raw/GZip .usmap source.");
            return null;
        }

        var hash =
            Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes))
                [..16];

        var path =
            Path.Combine(
                MappingsCache,
                $"{hash}.usmap");

        await File.WriteAllBytesAsync(
            path,
            bytes,
            cancellationToken);

        return new FileUsmapTypeMappingsProvider(
            path,
            StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<KeyValuePair<FGuid, FAesKey>>>
        GetAesKeysAsync(
            CancellationToken cancellationToken)
    {
        var endpoint =
            RequireHttpEndpoint(
                Environment.GetEnvironmentVariable(
                    "NOVASPARX_AES_API") ??
                "https://export-service-new.dillyapis.com/v1/aes");

        using var response =
            await _http.GetAsync(
                endpoint,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

        response.EnsureSuccessStatusCode();

        using var doc =
            JsonDocument.Parse(
                await ReadHttpBytesBoundedAsync(
                    response,
                    MaxAesDownloadBytes,
                    "AES source",
                    cancellationToken));

        var result =
            new Dictionary<FGuid, FAesKey>();

        var main =
            FindMainAesKey(doc.RootElement);

        if (main is not null)
            result[new FGuid()] =
                new FAesKey(main);

        Walk(doc.RootElement, element =>
        {
            if (element.ValueKind !=
                JsonValueKind.Object)
                return;

            string? key = null;
            string? guid = null;

            foreach (var property in
                     element.EnumerateObject())
            {
                if (property.Value.ValueKind !=
                    JsonValueKind.String)
                    continue;

                var name =
                    property.Name.ToLowerInvariant();

                var value =
                    property.Value.GetString()?.Trim();

                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if ((name.Contains("key") ||
                     name.Contains("aes")) &&
                    AesRegex().IsMatch(value))
                {
                    key ??= NormalizeAes(value);
                }

                if (name.Contains("guid") &&
                    GuidRegex().IsMatch(value))
                {
                    guid ??= NormalizeGuid(value);
                }
            }

            if (key is null || guid is null)
                return;

            try
            {
                result[new FGuid(guid)] =
                    new FAesKey(key);
            }
            catch
            {
                // Ignore malformed third-party entries.
            }
        });

        return result.ToArray();
    }

    private static string? FindMainAesKey(
        JsonElement root)
    {
        string? found = null;

        Walk(root, element =>
        {
            if (found is not null ||
                element.ValueKind !=
                JsonValueKind.Object)
                return;

            foreach (var property in
                     element.EnumerateObject())
            {
                if (property.Value.ValueKind !=
                    JsonValueKind.String)
                    continue;

                var name =
                    property.Name.ToLowerInvariant();

                var value =
                    property.Value.GetString()?.Trim() ?? "";

                if (!AesRegex().IsMatch(value))
                    continue;

                if (name is "mainkey" or
                    "main_key" or
                    "mainaes" or
                    "mainaeskey" ||
                    (name.Contains("main") &&
                     name.Contains("key")))
                {
                    found = NormalizeAes(value);
                    return;
                }
            }
        });

        return found;
    }

    private static string NormalizeAes(
        string value)
    {
        value = value.Trim();

        return value.StartsWith(
            "0x",
            StringComparison.OrdinalIgnoreCase)
            ? "0x" + value[2..].ToUpperInvariant()
            : value.ToUpperInvariant();
    }

    private static string NormalizeGuid(
        string value)
    {
        return value
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("{", "", StringComparison.Ordinal)
            .Replace("}", "", StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();
    }

    private static IEnumerable<string>
        FindUrls(JsonElement root)
    {
        var set =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        Walk(root, element =>
        {
            if (element.ValueKind !=
                JsonValueKind.String)
                return;

            var value =
                element.GetString();

            if (Uri.TryCreate(
                value,
                UriKind.Absolute,
                out var uri) &&
                uri.Scheme is "http" or "https")
            {
                set.Add(value!);
            }
        });

        return set;
    }

    private static void Walk(
        JsonElement root,
        Action<JsonElement> visitor)
    {
        visitor(root);

        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in
                         root.EnumerateArray())
                    Walk(item, visitor);
                break;

            case JsonValueKind.Object:
                foreach (var property in
                         root.EnumerateObject())
                    Walk(property.Value, visitor);
                break;
        }
    }

    private static bool IsGzip(byte[] bytes) =>
        bytes.Length >= 2 &&
        bytes[0] == 0x1F &&
        bytes[1] == 0x8B;

    private static bool IsZstd(byte[] bytes) =>
        bytes.Length >= 4 &&
        bytes[0] == 0x28 &&
        bytes[1] == 0xB5 &&
        bytes[2] == 0x2F &&
        bytes[3] == 0xFD;

    [GeneratedRegex(
        @"^\s*TocPath\s*=\s*""?([^""\r\n]+)""?\s*$",
        RegexOptions.IgnoreCase |
        RegexOptions.Multiline |
        RegexOptions.CultureInvariant)]
    private static partial Regex TocPathRegex();

    [GeneratedRegex(
        @"^(?:0x)?[0-9a-fA-F]{64}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex AesRegex();

    [GeneratedRegex(
        @"^[{]?[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}[}]?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex GuidRegex();
}

public sealed record ExternalTocResult(
    string Name,
    string Url,
    byte[] Bytes);

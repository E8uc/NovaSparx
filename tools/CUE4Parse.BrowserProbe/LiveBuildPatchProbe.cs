using System.Security.Cryptography;
using System.Text.Json;
using CUE4Parse.Compression;
using EpicManifestParser;
using EpicManifestParser.UE;

internal static class LiveBuildPatchProbe
{
    private const int MaxTocBytes = 2 * 1024 * 1024;
    private const int MaxManifestBytes = 64 * 1024 * 1024;

    public static async Task RunAsync(string baseUrl, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsBrowser())
            throw new PlatformNotSupportedException("Live BuildPatch proof must run inside browser-wasm.");

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var root) ||
            root.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Live BuildPatch proof requires an absolute HTTP base URL.");

        if (!baseUrl.EndsWith("/", StringComparison.Ordinal))
            root = new Uri(baseUrl + "/", UriKind.Absolute);

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(90)
        };

        using var manifestResponse = await http.GetAsync(
            new Uri(root, "manifest"),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        manifestResponse.EnsureSuccessStatusCode();

        var manifestBytes = await ReadBoundedAsync(
            await manifestResponse.Content.ReadAsStreamAsync(cancellationToken),
            MaxManifestBytes,
            cancellationToken);

        if (manifestBytes.Length < 16)
            throw new InvalidDataException("Live manifest response is too small.");

        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "novasparx-browser-buildpatch");

        Directory.CreateDirectory(cacheRoot);
        Directory.CreateDirectory(Path.Combine(cacheRoot, "chunks"));
        Directory.CreateDirectory(Path.Combine(cacheRoot, "manifests"));

        var options = new ManifestParseOptions
        {
            ChunkCacheDirectory = Path.Combine(cacheRoot, "chunks"),
            ManifestCacheDirectory = Path.Combine(cacheRoot, "manifests"),
            ChunkBaseUrl = new Uri(root, "chunk/").ToString(),
            Client = http,
            CacheChunksAsIs = true,
            Decompressor = Compression.Decompressor
        };

        var manifest = FBuildPatchAppManifest.Deserialize(
            manifestBytes,
            options);

        using var fixtureStream =
            typeof(LiveBuildPatchProbe).Assembly.GetManifestResourceStream("real-utoc-fixture.json")
            ?? throw new InvalidOperationException("Real TOC reference fixture was not generated.");

        using var fixture = JsonDocument.Parse(fixtureStream);
        var matched = 0;

        foreach (var item in fixture.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var logicalPath =
                item.GetProperty("logicalPath").GetString()
                ?? throw new InvalidDataException("Fixture logical path is missing.");

            var normalizedLogical = logicalPath.Replace('\\', '/');
            var fileName = normalizedLogical[(normalizedLogical.LastIndexOf('/') + 1)..];

            var candidates = manifest.Files
                .Where(file =>
                {
                    var name = file.FileName.Replace('\\', '/');
                    return name.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase) ||
                           name.Equals(fileName, StringComparison.OrdinalIgnoreCase);
                })
                .ToArray();

            if (candidates.Length != 1)
                throw new InvalidDataException($"Live manifest did not resolve exactly one {fileName} entry.");

            await using var stream = candidates[0].GetStream();

            if (stream.Length < 144 || stream.Length > MaxTocBytes)
                throw new InvalidDataException($"Live {fileName} size is outside the bounded TOC budget.");

            var bytes = await ReadBoundedAsync(
                stream,
                MaxTocBytes,
                cancellationToken);

            var expectedHash =
                item.GetProperty("tocSha256").GetString()
                ?? throw new InvalidDataException("Fixture TOC hash is missing.");

            var actualHash = Hash(bytes);

            if (!actualHash.Equals(expectedHash, StringComparison.Ordinal))
                throw new InvalidDataException($"Browser-fetched {fileName} differs from the desktop reference.");

            matched++;
            Console.WriteLine(
                $"LIVE_BUILDPATCH_BROWSER_OK|{fileName}|{bytes.Length}|{actualHash}");
        }

        if (matched != fixture.RootElement.GetProperty("fixtures").GetArrayLength())
            throw new InvalidDataException("Not every reference TOC was fetched through browser BuildPatch.");

        Console.WriteLine("LIVE_BUILDPATCH_BROWSER_WASM_OK");
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream input,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var read = await input.ReadAsync(
                buffer.AsMemory(0, buffer.Length),
                cancellationToken);

            if (read <= 0)
                break;

            if (output.Length + read > maxBytes)
                throw new InvalidDataException("Live browser fetch exceeded its memory budget.");

            await output.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }

        return output.ToArray();
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));
}

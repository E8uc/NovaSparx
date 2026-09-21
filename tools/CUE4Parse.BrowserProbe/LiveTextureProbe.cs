using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse_Conversion.Textures;
using EpicManifestParser;
using EpicManifestParser.UE;

internal static class LiveTextureProbe
{
    private const int MaxManifestBytes = 64 * 1024 * 1024;
    private const int MaxTocBytes = 2 * 1024 * 1024;
    private const int MaxBlockBytes = 4 * 1024 * 1024;
    private const long MaxSparseBytes = 32L * 1024 * 1024;

    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicParameterlessConstructor,
        typeof(UTexture2D))]
    public static async Task RunAsync(
        string baseUrl,
        CancellationToken cancellationToken,
        string? manifestUrl = null,
        string? chunkBaseUrl = null)
    {
        if (!OperatingSystem.IsBrowser())
            throw new PlatformNotSupportedException(
                "Live Texture proof must run inside browser-wasm.");

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var root) ||
            root.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                "Live Texture proof requires an absolute HTTP base URL.");
        }

        if (!baseUrl.EndsWith("/", StringComparison.Ordinal))
            root = new Uri(baseUrl + "/", UriKind.Absolute);

        var manifestSource =
            string.IsNullOrWhiteSpace(
                manifestUrl)
                ? new Uri(
                    root,
                    "manifest")
                : RequireHttpUri(
                    manifestUrl,
                    "Live Texture manifest");

        var chunkSource =
            string.IsNullOrWhiteSpace(
                chunkBaseUrl)
                ? new Uri(
                    root,
                    "chunk/")
                    .ToString()
                : RequireHttpUri(
                    chunkBaseUrl,
                    "Live Texture chunk base",
                    ensureTrailingSlash: true)
                    .ToString();

        using var referenceStream =
            typeof(LiveTextureProbe).Assembly
                .GetManifestResourceStream(
                    "texture-package-fixture.json")
            ?? throw new InvalidOperationException(
                "Texture reference fixture was not generated.");

        using var reference =
            JsonDocument.Parse(referenceStream);

        var rootFixture =
            reference.RootElement;

        var keys =
            ReadKeys(rootFixture);

        if (keys.Count == 0)
            throw new InvalidDataException(
                "Texture fixture contains no current public AES keys.");

        var mappingBytes =
            ReadBase64(
                rootFixture,
                "mappingsBase64",
                24 * 1024 * 1024);

        if (!Hash(mappingBytes).Equals(
                rootFixture
                    .GetProperty("mappingsSha256")
                    .GetString(),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Texture mappings differ from the desktop reference.");
        }

        using var http =
            new HttpClient
            {
                Timeout =
                    TimeSpan.FromSeconds(90)
            };

        var manifestBytes =
            await GetBytesAsync(
                http,
                manifestSource,
                MaxManifestBytes,
                "Live Fortnite manifest",
                cancellationToken);

        var cacheRoot =
            Path.Combine(
                Path.GetTempPath(),
                "novasparx-live-texture");

        Directory.CreateDirectory(cacheRoot);

        var chunkCache =
            Path.Combine(
                cacheRoot,
                "chunks");

        var manifestCache =
            Path.Combine(
                cacheRoot,
                "manifests");

        Directory.CreateDirectory(chunkCache);
        Directory.CreateDirectory(manifestCache);

        var options =
            new ManifestParseOptions
            {
                ChunkCacheDirectory =
                    chunkCache,
                ManifestCacheDirectory =
                    manifestCache,
                ChunkBaseUrl =
                    chunkSource,
                Client =
                    http,
                CacheChunksAsIs =
                    false,
                Decompressor =
                    CUE4Parse.Compression
                        .Compression
                        .Decompressor
            };

        var manifest =
            FBuildPatchAppManifest.Deserialize(
                manifestBytes,
                options);

        var files =
            manifest.Files.ToDictionary(
                file =>
                    Normalize(
                        file.FileName),
                StringComparer.OrdinalIgnoreCase);

        var globalTocFile =
            FindExactlyOne(
                manifest,
                "global.utoc");

        var targetTocFile =
            FindExactlyOne(
                manifest,
                "pakchunk1002-WindowsClient.utoc");

        var globalToc =
            await ReadFileAsync(
                globalTocFile.GetStream(),
                MaxTocBytes,
                "global.utoc",
                cancellationToken);

        var targetToc =
            await ReadFileAsync(
                targetTocFile.GetStream(),
                MaxTocBytes,
                "pakchunk1002-WindowsClient.utoc",
                cancellationToken);

        var versions =
            new VersionContainer(
                EGame.GAME_UE6_0);

        var sparse =
            new Dictionary<string, SparseBacking>(
                StringComparer.OrdinalIgnoreCase);

        SparseBacking BackingFor(
            string requestedPath)
        {
            var normalized =
                Normalize(
                    requestedPath);

            if (sparse.TryGetValue(
                    normalized,
                    out var existing))
            {
                return existing;
            }

            if (!files.TryGetValue(
                    normalized,
                    out var file))
            {
                throw new FileNotFoundException(
                    "Live BuildPatch manifest does not contain the requested IoStore partition.",
                    normalized);
            }

            using var stream =
                file.GetStream();

            var created =
                new SparseBacking(
                    stream.Length);

            sparse.Add(
                normalized,
                created);

            return created;
        }

        FArchive OpenSparse(
            string requestedPath)
        {
            return new FStreamArchive(
                requestedPath,
                new SparseReadStream(
                    BackingFor(
                        requestedPath)),
                versions);
        }

        using var provider =
            new StreamedFileProvider(
                "Fortnite",
                versions,
                StringComparer.OrdinalIgnoreCase);

        provider.RegisterVfs(
            new FByteArchive(
                targetTocFile.FileName,
                targetToc,
                versions),
            null,
            OpenSparse);

        var targetReader =
            provider.GetArchive(
                Path.GetFileName(
                    targetTocFile.FileName),
                StringComparison.OrdinalIgnoreCase)
            as IoStoreReader
            ?? throw new InvalidDataException(
                "Target IoStore reader was not registered.");

        targetReader.CustomEncryption =
            BrowserIoStoreDecrypt;

        var targetKey =
            FindKey(
                targetReader,
                keys);

        if (targetReader.IsEncrypted)
        {
            if (targetKey is null)
            {
                throw new InvalidDataException(
                    $"No public AES key matched target GUID {targetReader.EncryptionKeyGuid}.");
            }

            var mounted =
                await provider.SubmitKeyAsync(
                    targetReader.EncryptionKeyGuid,
                    targetKey);

            if (mounted < 1)
            {
                throw new InvalidDataException(
                    "Target IoStore directory index did not mount with the matched key.");
            }
        }
        else
        {
            var mounted =
                await provider.MountAsync();

            if (mounted < 1)
            {
                throw new InvalidDataException(
                    "Target IoStore directory index did not mount.");
            }
        }

        var selected =
            rootFixture
                .GetProperty("selected")
                .EnumerateArray()
                .ToArray();

        if (selected.Length != 3)
            throw new InvalidDataException(
                "Live Texture proof requires exactly the three established Texture targets.");

        var targetEntries =
            new List<FIoStoreEntry>(
                selected.Length);

        foreach (var item in selected)
        {
            var path =
                item.GetProperty("path")
                    .GetString()
                ?? throw new InvalidDataException(
                    "Texture fixture path is missing.");

            var entry =
                provider.Files.Values
                    .OfType<FIoStoreEntry>()
                    .SingleOrDefault(
                        file =>
                            file.Path.Equals(
                                path,
                                StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException(
                    "Mounted live target container does not contain the exact Texture path.",
                    path);

            targetEntries.Add(entry);
        }

        var targetPackageIds =
            targetEntries
                .Select(
                    entry =>
                        entry.ChunkId.ChunkId)
                .ToHashSet();

        var targetChunkIds =
            targetReader.TocResource
                .ChunkIds
                .Where(
                    chunk =>
                        targetPackageIds.Contains(
                            chunk.ChunkId))
                .ToList();

        targetChunkIds.Add(
            new FIoChunkId(
                targetReader
                    .TocResource
                    .Header
                    .ContainerId
                    .Id,
                0,
                EIoChunkType5.ContainerHeader));

        await WarmChunksAsync(
            targetReader,
            targetTocFile.FileName,
            targetChunkIds,
            files,
            BackingFor,
            targetKey,
            cancellationToken);

        provider.RegisterVfs(
            new FByteArchive(
                globalTocFile.FileName,
                globalToc,
                versions),
            null,
            OpenSparse);

        var globalReader =
            provider.GetArchive(
                Path.GetFileName(
                    globalTocFile.FileName),
                StringComparison.OrdinalIgnoreCase)
            as IoStoreReader
            ?? throw new InvalidDataException(
                "Global IoStore reader was not registered.");

        globalReader.CustomEncryption =
            BrowserIoStoreDecrypt;

        var globalKey =
            FindKey(
                globalReader,
                keys);

        await WarmChunksAsync(
            globalReader,
            globalTocFile.FileName,
            [
                new FIoChunkId(
                    0,
                    0,
                    EIoChunkType5.ScriptObjects)
            ],
            files,
            BackingFor,
            globalKey,
            cancellationToken);

        await provider.MountAsync();

        if (provider.GlobalData is null)
            throw new InvalidDataException(
                "Live global ScriptObjects did not mount inside browser WASM.");

        provider.MappingsContainer =
            new BrowserMemoryMappings(
                mappingBytes);

        ObjectTypeRegistry.RegisterClass(
            "Texture2D",
            typeof(UTexture2D));

        CUE4Parse.Globals
            .FatalObjectSerializationErrors =
            true;

        TextureDecoder
            .UseAssetRipperTextureDecoder =
            true;

        foreach (var item in selected)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            var path =
                item.GetProperty("path")
                    .GetString()!;

            var file =
                provider.Files.Values
                    .Single(
                        candidate =>
                            candidate.Path.Equals(
                                path,
                                StringComparison.OrdinalIgnoreCase));

            var objectName =
                item.GetProperty(
                        "objectName")
                    .GetString()
                ?? throw new InvalidDataException(
                    "Texture object name is missing.");

            var rootObject =
                provider
                    .LoadPackage(file)
                    .GetExport(
                        objectName);

            if (rootObject is not UTexture2D texture)
            {
                throw new InvalidDataException(
                    $"Live package root is {rootObject.GetType().Name}, export type {rootObject.ExportType}; expected UTexture2D.");
            }

            var mipIndex =
                item.GetProperty(
                        "mipIndex")
                    .GetInt32();

            var mip =
                texture.GetMip(
                    mipIndex)
                ?? throw new InvalidDataException(
                    "Live Texture mip is missing.");

            if (mip.SizeX <= 0 ||
                mip.SizeY <= 0 ||
                (long)mip.SizeX *
                    mip.SizeY >
                256L * 256)
            {
                throw new InvalidDataException(
                    "Live Texture mip exceeds the browser pixel budget.");
            }

            var expectedMipHash =
                item.GetProperty(
                        "mipSha256")
                    .GetString();

            if (!Hash(
                    mip.BulkData.Data)
                .Equals(
                    expectedMipHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Live Texture mip bytes differ from the desktop reference.");
            }

            var decoded =
                texture.DecodeMip(
                    mipIndex)
                ?? throw new InvalidDataException(
                    "Live Texture decoder produced no pixels.");

            var expectedWidth =
                item.GetProperty(
                        "width")
                    .GetInt32();

            var expectedHeight =
                item.GetProperty(
                        "height")
                    .GetInt32();

            var expectedPixels =
                item.GetProperty(
                        "pixelsSha256")
                    .GetString();

            if (decoded.Width !=
                    expectedWidth ||
                decoded.Height !=
                    expectedHeight ||
                decoded.PixelFormat !=
                    EPixelFormat.PF_R8G8B8A8 ||
                !Hash(
                        decoded.Data)
                    .Equals(
                        expectedPixels,
                        StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Live browser pixels disagree with the desktop CUE4Parse reference.");
            }

            var pixelHash =
                Hash(
                    decoded.Data);

            Console.WriteLine(
                $"LIVE_TEXTURE_BROWSER_OK|{path}|{texture.Format}|{mipIndex}|{decoded.Width}x{decoded.Height}|{pixelHash}");

            TexturePackageProbe.Render(
                decoded.Width,
                decoded.Height,
                Convert.ToBase64String(
                    decoded.Data),
                path);
        }

        var sparseBytes =
            sparse.Values
                .Sum(
                    backing =>
                        backing.StoredBytes);

        if (sparseBytes <= 0 ||
            sparseBytes >
            MaxSparseBytes)
        {
            throw new InvalidDataException(
                $"Live sparse UCAS budget invalid: {sparseBytes} bytes.");
        }

        Console.WriteLine(
            $"LIVE_TEXTURE_BROWSER_WASM_OK|sparseBytes={sparseBytes}|partitions={sparse.Count}");
    }

    private static async Task WarmChunksAsync(
        IoStoreReader reader,
        string tocPath,
        IEnumerable<FIoChunkId> chunkIds,
        IReadOnlyDictionary<string, FFileManifest> files,
        Func<string, SparseBacking> backingFor,
        FAesKey? key,
        CancellationToken cancellationToken)
    {
        var blockSize =
            reader.TocResource
                .Header
                .CompressionBlockSize;

        if (blockSize == 0)
            throw new InvalidDataException(
                "IoStore compression block size is zero.");

        var warmed =
            new HashSet<int>();

        foreach (var chunkId in
                 chunkIds.Distinct())
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            if (!reader.TryResolve(
                    chunkId,
                    out var offsetLength))
            {
                throw new KeyNotFoundException(
                    $"IoStore chunk {chunkId} was not found in {reader.Name}.");
            }

            var offset =
                (ulong)offsetLength.Offset;

            var length =
                (ulong)offsetLength.Length;

            if (length == 0)
                continue;

            var firstBlock =
                checked(
                    (int)(
                        offset /
                        blockSize));

            var lastBlock =
                checked(
                    (int)(
                        (
                            offset +
                            length -
                            1
                        ) /
                        blockSize));

            for (var blockIndex =
                     firstBlock;
                 blockIndex <=
                     lastBlock;
                 blockIndex++)
            {
                if (!warmed.Add(
                        blockIndex))
                    continue;

                ref var block =
                    ref reader
                        .TocResource
                        .CompressionBlocks[
                            blockIndex];

                var compressedSize =
                    checked(
                        (int)
                        block
                            .CompressedSize);

                var rawSize =
                    Align16(
                        compressedSize);

                if (rawSize <= 0 ||
                    rawSize >
                    MaxBlockBytes)
                {
                    throw new InvalidDataException(
                        $"IoStore block {blockIndex} exceeds the browser block budget.");
                }

                var partitionSize =
                    reader.TocResource
                        .Header
                        .PartitionSize;

                var absoluteOffset =
                    (ulong)block.Offset;

                var partitionIndex =
                    checked(
                        (int)(
                            absoluteOffset /
                            partitionSize));

                var partitionOffset =
                    checked(
                        (long)(
                            absoluteOffset %
                            partitionSize));

                var partitionPath =
                    PartitionPath(
                        tocPath,
                        partitionIndex);

                var normalized =
                    Normalize(
                        partitionPath);

                if (!files.TryGetValue(
                        normalized,
                        out var partition))
                {
                    throw new FileNotFoundException(
                        "Live BuildPatch manifest does not contain the IoStore UCAS partition.",
                        normalized);
                }

                var backing =
                    backingFor(
                        normalized);

                if (backing.Contains(
                        partitionOffset,
                        rawSize))
                {
                    continue;
                }

                await using var source =
                    partition.GetStream();

                var encrypted =
                    new byte[
                        rawSize];

                var read =
                    await source.ReadAtAsync(
                        partitionOffset,
                        encrypted,
                        0,
                        encrypted.Length,
                        cancellationToken);

                if (read !=
                    encrypted.Length)
                {
                    throw new EndOfStreamException(
                        $"Live UCAS block read {read} of {encrypted.Length} bytes.");
                }

                byte[] plain;

                if (reader.IsEncrypted)
                {
                    if (reader.TocResource
                            .EncryptionMethod !=
                        EIoEncryptionMethod
                            .AES_CTR)
                    {
                        throw new NotSupportedException(
                            $"Live browser proof supports AES-CTR IoStore only; got {reader.TocResource.EncryptionMethod}.");
                    }

                    if (key is null)
                    {
                        throw new InvalidDataException(
                            $"No matched AES key for encrypted reader {reader.Name}.");
                    }

                    plain =
                        BrowserAesCtr.Transform(
                            encrypted,
                            key.Key,
                            reader.TocResource
                                .EncryptionIVs[
                                    blockIndex]
                                .Bytes,
                            cancellationToken);
                }
                else
                {
                    plain =
                        encrypted;
                }

                backing.Add(
                    partitionOffset,
                    plain);
            }
        }
    }

    private static byte[] BrowserIoStoreDecrypt(
        byte[] bytes,
        int beginOffset,
        int count,
        bool isIndex,
        IAesVfsReader reader)
    {
        return BrowserIoStoreDecrypt(
            bytes,
            beginOffset,
            count,
            isIndex,
            reader,
            null);
    }

    private static byte[] BrowserIoStoreDecrypt(
        byte[] bytes,
        int beginOffset,
        int count,
        bool isIndex,
        IAesVfsReader reader,
        object? customData)
    {
        ArgumentNullException
            .ThrowIfNull(
                bytes);

        if (beginOffset < 0 ||
            count < 0 ||
            beginOffset >
            bytes.Length -
                count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count));
        }

        if (!isIndex)
        {
            return beginOffset == 0 &&
                   count ==
                   bytes.Length
                ? bytes
                : bytes.AsSpan(
                        beginOffset,
                        count)
                    .ToArray();
        }

        if (reader is not
                IoStoreReader io ||
            io.TocResource
                .EncryptionMethod !=
            EIoEncryptionMethod
                .AES_CTR)
        {
            throw new NotSupportedException(
                "Browser IoStore index hook supports AES-CTR only.");
        }

        var aes =
            reader.AesKey
            ?? throw new InvalidDataException(
                "Browser IoStore index decrypt has no active AES key.");

        var encrypted =
            bytes.AsSpan(
                    beginOffset,
                    count)
                .ToArray();

        return BrowserAesCtr.Transform(
            encrypted,
            aes.Key,
            io.TocResource
                .EncryptionIVs[^1]
                .Bytes);
    }

    private static IReadOnlyList<
        KeyValuePair<CUE4Parse.UE4.Objects.Core.Misc.FGuid, FAesKey>>
        ReadKeys(
            JsonElement root)
    {
        var keys =
            new Dictionary<
                CUE4Parse.UE4.Objects.Core.Misc.FGuid,
                FAesKey>();

        foreach (var item in
                 root.GetProperty(
                         "publicAesKeys")
                     .EnumerateArray())
        {
            var guid =
                item.GetProperty(
                        "guid")
                    .GetString();

            var key =
                item.GetProperty(
                        "keyHex")
                    .GetString();

            if (string.IsNullOrWhiteSpace(
                    guid) ||
                string.IsNullOrWhiteSpace(
                    key))
            {
                continue;
            }

            keys[new CUE4Parse.UE4.Objects.Core.Misc.FGuid(guid)] =
                new FAesKey(key);
        }

        return keys.ToArray();
    }

    private static FAesKey? FindKey(
        IoStoreReader reader,
        IReadOnlyList<
            KeyValuePair<CUE4Parse.UE4.Objects.Core.Misc.FGuid, FAesKey>>
            keys)
    {
        if (!reader.IsEncrypted)
            return null;

        foreach (var pair in keys)
        {
            if (pair.Key ==
                reader.EncryptionKeyGuid)
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static FFileManifest
        FindExactlyOne(
            FBuildPatchAppManifest manifest,
            string fileName)
    {
        var matches =
            manifest.Files
                .Where(
                    file =>
                        Normalize(
                                file.FileName)
                            .EndsWith(
                                "/" +
                                fileName,
                                StringComparison.OrdinalIgnoreCase) ||
                        Normalize(
                                file.FileName)
                            .Equals(
                                fileName,
                                StringComparison.OrdinalIgnoreCase))
                .ToArray();

        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                $"Expected exactly one live {fileName}, found {matches.Length}.");
        }

        return matches[0];
    }

    private static Uri RequireHttpUri(
        string value,
        string label,
        bool ensureTrailingSlash = false)
    {
        if (
            !Uri.TryCreate(
                value,
                UriKind.Absolute,
                out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                $"{label} requires an absolute HTTP URL.");
        }

        if (
            ensureTrailingSlash &&
            !uri.AbsoluteUri.EndsWith(
                "/",
                StringComparison.Ordinal))
        {
            uri =
                new Uri(
                    uri.AbsoluteUri + "/",
                    UriKind.Absolute);
        }

        return uri;
    }

    private static async Task<byte[]>
        GetBytesAsync(
            HttpClient client,
            Uri uri,
            int maxBytes,
            string label,
            CancellationToken cancellationToken)
    {
        using var response =
            await client.GetAsync(
                uri,
                HttpCompletionOption
                    .ResponseHeadersRead,
                cancellationToken);

        response
            .EnsureSuccessStatusCode();

        var declared =
            response.Content
                .Headers
                .ContentLength;

        if (declared is > 0 &&
            declared >
            maxBytes)
        {
            throw new InvalidDataException(
                $"{label} exceeds the browser byte budget.");
        }

        await using var input =
            await response.Content
                .ReadAsStreamAsync(
                    cancellationToken);

        return await ReadFileAsync(
            input,
            maxBytes,
            label,
            cancellationToken);
    }

    private static async Task<byte[]>
        ReadFileAsync(
            Stream input,
            int maxBytes,
            string label,
            CancellationToken cancellationToken)
    {
        using (input)
        using (var output =
               new MemoryStream())
        {
            var buffer =
                new byte[
                    64 * 1024];

            while (true)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();

                var read =
                    await input.ReadAsync(
                        buffer.AsMemory(),
                        cancellationToken);

                if (read <= 0)
                    break;

                if (output.Length +
                    read >
                    maxBytes)
                {
                    throw new InvalidDataException(
                        $"{label} exceeded the browser byte budget.");
                }

                await output.WriteAsync(
                    buffer.AsMemory(
                        0,
                        read),
                    cancellationToken);
            }

            return output
                .ToArray();
        }
    }

    private static byte[] ReadBase64(
        JsonElement root,
        string property,
        int maxBytes)
    {
        var encoded =
            root.GetProperty(
                    property)
                .GetString()
            ?? throw new InvalidDataException(
                $"Missing {property}.");

        if (encoded.Length >
            (long)(
                maxBytes +
                2) /
            3 *
            4)
        {
            throw new InvalidDataException(
                $"{property} exceeds the browser byte budget.");
        }

        var bytes =
            Convert.FromBase64String(
                encoded);

        if (bytes.Length >
            maxBytes)
        {
            throw new InvalidDataException(
                $"{property} exceeds the browser byte budget.");
        }

        return bytes;
    }

    private static int Align16(
        int value)
    {
        return checked(
            (
                value +
                15
            ) &
            ~15);
    }

    private static string PartitionPath(
        string tocPath,
        int partitionIndex)
    {
        var normalized =
            Normalize(
                tocPath);

        if (!normalized.EndsWith(
                ".utoc",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "IoStore TOC path does not end in .utoc.");
        }

        var stem =
            normalized[
                ..^5];

        return partitionIndex == 0
            ? stem +
              ".ucas"
            : stem +
              "_s" +
              partitionIndex +
              ".ucas";
    }

    private static string Normalize(
        string path)
    {
        return path
            .Replace(
                '\\',
                '/');
    }

    private static string Hash(
        byte[] bytes)
    {
        return Convert.ToHexString(
            SHA256.HashData(
                bytes));
    }
}

internal sealed class BrowserMemoryMappings
    : UsmapTypeMappingsProvider
{
    public BrowserMemoryMappings(
        byte[] bytes)
    {
        Load(bytes);
    }

    public override void Reload()
    {
        throw new NotSupportedException(
            "Browser mappings fixture is immutable.");
    }
}

internal sealed class SparseBacking
{
    private readonly object _gate =
        new();

    private readonly List<SparseSegment>
        _segments =
            [];

    public SparseBacking(
        long length)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(
                nameof(length));

        Length =
            length;
    }

    public long Length { get; }

    public long StoredBytes
    {
        get
        {
            lock (_gate)
            {
                return _segments
                    .Sum(
                        segment =>
                            (long)
                            segment.Data
                                .Length);
            }
        }
    }

    public bool Contains(
        long offset,
        int count)
    {
        lock (_gate)
        {
            return _segments
                .Any(
                    segment =>
                        offset >=
                            segment.Offset &&
                        offset +
                            count <=
                        segment.Offset +
                            segment.Data
                                .Length);
        }
    }

    public void Add(
        long offset,
        byte[] data)
    {
        if (offset < 0 ||
            data.Length == 0 ||
            offset +
                data.Length >
            Length)
        {
            throw new InvalidDataException(
                "Sparse UCAS segment is outside the partition bounds.");
        }

        lock (_gate)
        {
            if (Contains(
                    offset,
                    data.Length))
            {
                return;
            }

            foreach (var existing in
                     _segments)
            {
                var end =
                    offset +
                    data.Length;

                var existingEnd =
                    existing.Offset +
                    existing.Data
                        .Length;

                if (offset <
                        existingEnd &&
                    existing.Offset <
                        end)
                {
                    throw new InvalidDataException(
                        "Sparse UCAS segments overlap unexpectedly.");
                }
            }

            _segments.Add(
                new SparseSegment(
                    offset,
                    data));

            _segments.Sort(
                static (left, right) =>
                    left.Offset
                        .CompareTo(
                            right.Offset));
        }
    }

    public int Read(
        long position,
        byte[] buffer,
        int offset,
        int count)
    {
        if (position < 0 ||
            offset < 0 ||
            count < 0 ||
            offset >
            buffer.Length -
                count)
        {
            throw new ArgumentOutOfRangeException();
        }

        if (count == 0)
            return 0;

        if (position >=
            Length)
            return 0;

        var allowed =
            checked(
                (int)
                Math.Min(
                    count,
                    Length -
                    position));

        var copied =
            0;

        lock (_gate)
        {
            while (copied <
                   allowed)
            {
                var current =
                    position +
                    copied;

                var segment =
                    _segments
                        .FirstOrDefault(
                            item =>
                                current >=
                                    item.Offset &&
                                current <
                                    item.Offset +
                                    item.Data
                                        .Length)
                    ?? throw new InvalidDataException(
                        $"CUE4Parse requested unprefetched UCAS bytes at 0x{current:X}.");

                var sourceOffset =
                    checked(
                        (int)(
                            current -
                            segment
                                .Offset));

                var take =
                    Math.Min(
                        allowed -
                        copied,
                        segment.Data
                            .Length -
                        sourceOffset);

                Buffer.BlockCopy(
                    segment.Data,
                    sourceOffset,
                    buffer,
                    offset +
                        copied,
                    take);

                copied +=
                    take;
            }
        }

        return copied;
    }

    private sealed record SparseSegment(
        long Offset,
        byte[] Data);
}

internal sealed class SparseReadStream
    : Stream,
      ICloneable
{
    private readonly SparseBacking
        _backing;

    private long _position;

    public SparseReadStream(
        SparseBacking backing)
    {
        _backing =
            backing;
    }

    public override bool CanRead =>
        true;

    public override bool CanSeek =>
        true;

    public override bool CanWrite =>
        false;

    public override long Length =>
        _backing.Length;

    public override long Position
    {
        get =>
            _position;
        set
        {
            if (value < 0 ||
                value >
                Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value));
            }

            _position =
                value;
        }
    }

    public override int Read(
        byte[] buffer,
        int offset,
        int count)
    {
        var read =
            _backing.Read(
                _position,
                buffer,
                offset,
                count);

        _position +=
            read;

        return read;
    }

    public override long Seek(
        long offset,
        SeekOrigin origin)
    {
        var next =
            origin switch
            {
                SeekOrigin.Begin =>
                    offset,
                SeekOrigin.Current =>
                    _position +
                    offset,
                SeekOrigin.End =>
                    Length +
                    offset,
                _ =>
                    throw new ArgumentOutOfRangeException(
                        nameof(origin))
            };

        Position =
            next;

        return _position;
    }

    public object Clone()
    {
        return new SparseReadStream(
            _backing)
        {
            Position =
                Position
        };
    }

    public override void Flush()
    {
    }

    public override void SetLength(
        long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(
        byte[] buffer,
        int offset,
        int count)
    {
        throw new NotSupportedException();
    }
}

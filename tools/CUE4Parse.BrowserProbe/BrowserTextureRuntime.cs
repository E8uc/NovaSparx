using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text.Json;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse_Conversion.Textures;
using EpicManifestParser;
using EpicManifestParser.UE;

internal static class BrowserTextureRuntime
{
    private const int MaxManifestBytes = 64 * 1024 * 1024;
    private const int MaxMetadataBytes = 4 * 1024 * 1024;
    private const int MaxAesBytes = 2 * 1024 * 1024;
    private const int MaxMappingsBytes = 32 * 1024 * 1024;
    private const int MaxMappingsExpandedBytes = 48 * 1024 * 1024;
    private const int MaxTocBytes = 2 * 1024 * 1024;
    private const int MaxBlockBytes = 4 * 1024 * 1024;
    private const long MaxSparseBytes = 64L * 1024 * 1024;
    private const int DefaultPreviewSize = 1024;
    private const int MaxPreviewSize = 2048;

    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicParameterlessConstructor,
        typeof(UTexture2D))]
    public static async Task RunAsync(
        string assetPath,
        string containerToc,
        string manifestUrl,
        string chunkBaseUrl,
        string mappingsApiUrl,
        string aesApiUrl,
        int maxPreviewSize,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsBrowser())
            throw new PlatformNotSupportedException(
                "NovaSparx Texture runtime requires browser-wasm.");

        var cleanPath =
            NormalizeAssetPath(
                assetPath);

        var cleanToc =
            Normalize(
                containerToc)
                .TrimStart('/');

        if (string.IsNullOrWhiteSpace(cleanPath))
            throw new ArgumentException(
                "NovaSparx Texture runtime requires an exact asset path.",
                nameof(assetPath));

        if (!cleanPath.EndsWith(
                ".uasset",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "NovaSparx Texture runtime accepts exact .uasset Texture paths only.");
        }

        if (string.IsNullOrWhiteSpace(cleanToc) ||
            !cleanToc.EndsWith(
                ".utoc",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "NovaSparx Texture runtime requires an exact IoStore .utoc path.");
        }

        maxPreviewSize =
            Math.Clamp(
                maxPreviewSize <= 0
                    ? DefaultPreviewSize
                    : maxPreviewSize,
                64,
                MaxPreviewSize);

        var manifestSource =
            RequireHttpUri(
                manifestUrl,
                "Fortnite manifest");

        var chunkSource =
            RequireHttpUri(
                chunkBaseUrl,
                "Fortnite BuildPatch chunk base",
                ensureTrailingSlash: true)
                .ToString();

        var mappingsSource =
            RequireHttpUri(
                mappingsApiUrl,
                "Fortnite mappings metadata");

        var aesSource =
            RequireHttpUri(
                aesApiUrl,
                "Fortnite AES metadata");

        using var http =
            new HttpClient
            {
                Timeout =
                    TimeSpan.FromSeconds(
                        120)
            };

        var keys =
            await GetAesKeysAsync(
                http,
                aesSource,
                cancellationToken);

        if (keys.Count == 0)
            throw new InvalidDataException(
                "The live AES source returned no usable Fortnite keys.");

        var mappingBytes =
            await GetMappingsBytesAsync(
                http,
                mappingsSource,
                cancellationToken);

        var manifestBytes =
            await GetBytesAsync(
                http,
                manifestSource,
                MaxManifestBytes,
                "Fortnite BuildPatch manifest",
                cancellationToken);

        var cacheRoot =
            Path.Combine(
                Path.GetTempPath(),
                "novasparx-browser-texture-runtime");

        Directory.CreateDirectory(
            cacheRoot);

        var chunkCache =
            Path.Combine(
                cacheRoot,
                "chunks");

        var manifestCache =
            Path.Combine(
                cacheRoot,
                "manifests");

        Directory.CreateDirectory(
            chunkCache);

        Directory.CreateDirectory(
            manifestCache);

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
            FBuildPatchAppManifest
                .Deserialize(
                    manifestBytes,
                    options);

        var files =
            manifest.Files
                .GroupBy(
                    file =>
                        Normalize(
                            file.FileName)
                            .TrimStart('/'),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group =>
                        group.Key,
                    group =>
                        group.First(),
                    StringComparer.OrdinalIgnoreCase);

        var globalTocFile =
            FindToc(
                manifest,
                "global.utoc");

        var targetTocFile =
            FindToc(
                manifest,
                cleanToc);

        var globalToc =
            await ReadFileAsync(
                globalTocFile
                    .GetStream(),
                MaxTocBytes,
                "global.utoc",
                cancellationToken);

        var targetToc =
            await ReadFileAsync(
                targetTocFile
                    .GetStream(),
                MaxTocBytes,
                targetTocFile.FileName,
                cancellationToken);

        var versions =
            new VersionContainer(
                EGame.GAME_UE6_0);

        var sparse =
            new Dictionary<
                string,
                SparseBacking>(
                    StringComparer.OrdinalIgnoreCase);

        SparseBacking BackingFor(
            string requestedPath)
        {
            var normalized =
                Normalize(
                    requestedPath)
                    .TrimStart('/');

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
                    "The live Fortnite manifest does not contain the requested IoStore partition.",
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
                "The target IoStore reader was not registered.");

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
                    $"No live AES key matched target container GUID {targetReader.EncryptionKeyGuid}.");
            }

            var mounted =
                await provider
                    .SubmitKeyAsync(
                        targetReader
                            .EncryptionKeyGuid,
                        targetKey);

            if (mounted < 1)
            {
                throw new InvalidDataException(
                    "The target IoStore directory index did not mount.");
            }
        }
        else
        {
            var mounted =
                await provider
                    .MountAsync();

            if (mounted < 1)
            {
                throw new InvalidDataException(
                    "The target IoStore directory index did not mount.");
            }
        }

        cancellationToken
            .ThrowIfCancellationRequested();

        var targetEntry =
            provider.Files.Values
                .OfType<FIoStoreEntry>()
                .SingleOrDefault(
                    file =>
                        NormalizeAssetPath(
                                file.Path)
                            .Equals(
                                cleanPath,
                                StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException(
                "The mounted IoStore container does not contain the exact Texture path.",
                cleanPath);

        var targetChunkIds =
            targetReader.TocResource
                .ChunkIds
                .Where(
                    chunk =>
                        chunk.ChunkId ==
                        targetEntry
                            .ChunkId
                            .ChunkId)
                .ToList();

        targetChunkIds.Add(
            new FIoChunkId(
                targetReader
                    .TocResource
                    .Header
                    .ContainerId
                    .Id,
                0,
                EIoChunkType5
                    .ContainerHeader));

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
                "The global IoStore reader was not registered.");

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
                    EIoChunkType5
                        .ScriptObjects)
            ],
            files,
            BackingFor,
            globalKey,
            cancellationToken);

        if (globalReader.IsEncrypted &&
            globalKey is not null)
        {
            await provider
                .SubmitKeyAsync(
                    globalReader
                        .EncryptionKeyGuid,
                    globalKey);
        }

        await provider
            .MountAsync();

        if (provider.GlobalData is null)
            throw new InvalidDataException(
                "The live global ScriptObjects chunk did not mount.");

        provider.MappingsContainer =
            new BrowserMemoryMappings(
                mappingBytes);

        ObjectTypeRegistry.RegisterClass(
            "Texture2D",
            typeof(UTexture2D));

        if (Activator.CreateInstance(
                typeof(UTexture2D))
            is not UTexture2D)
        {
            throw new InvalidOperationException(
                "Texture2D constructor is unavailable after browser trimming.");
        }

        CUE4Parse.Globals
            .FatalObjectSerializationErrors =
            true;

        TextureDecoder
            .UseAssetRipperTextureDecoder =
            true;

        cancellationToken
            .ThrowIfCancellationRequested();

        var objectName =
            Path.GetFileNameWithoutExtension(
                cleanPath);

        var rootObject =
            provider
                .LoadPackage(
                    targetEntry)
                .GetExport(
                    objectName);

        if (rootObject is not UTexture2D texture)
        {
            throw new InvalidDataException(
                $"The exact package root is {rootObject.GetType().Name}, export type {rootObject.ExportType}; expected UTexture2D.");
        }

        var mipIndex =
            SelectPreviewMip(
                texture,
                maxPreviewSize);

        if (mipIndex < 0)
            throw new InvalidDataException(
                "The Texture has no browser-safe mip payload.");

        var mip =
            texture.GetMip(
                mipIndex)
            ?? throw new InvalidDataException(
                "The selected Texture mip payload is unavailable.");

        var pixelCount =
            (long)mip.SizeX *
            mip.SizeY;

        if (mip.SizeX <= 0 ||
            mip.SizeY <= 0 ||
            pixelCount <= 0 ||
            pixelCount >
                (long)MaxPreviewSize *
                MaxPreviewSize)
        {
            throw new InvalidDataException(
                "The selected Texture mip exceeds the browser pixel budget.");
        }

        var decoded =
            texture.DecodeMip(
                mipIndex)
            ?? throw new InvalidDataException(
                "The browser Texture decoder produced no pixels.");

        cancellationToken
            .ThrowIfCancellationRequested();

        var rgba =
            ToRgba(
                decoded.PixelFormat,
                decoded.Data,
                decoded.Width,
                decoded.Height);

        if (rgba.Length !=
            checked(
                decoded.Width *
                decoded.Height *
                4))
        {
            throw new InvalidDataException(
                "The decoded Texture does not contain a complete RGBA frame.");
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
                $"The sparse IoStore working set is outside the browser memory budget: {sparseBytes} bytes.");
        }

        Console.WriteLine(
            $"RUNTIME_TEXTURE_BROWSER_OK|{cleanPath}|{targetReader.Name}|{texture.Format}|{mipIndex}|{decoded.Width}x{decoded.Height}|sparse={sparseBytes}");

        TexturePackageProbe.Render(
            decoded.Width,
            decoded.Height,
            Convert.ToBase64String(
                rgba),
            cleanPath);
    }

    private static int SelectPreviewMip(
        UTexture2D texture,
        int maxPreviewSize)
    {
        for (
            var index = 0;
            index <
                texture.PlatformData
                    .Mips
                    .Length;
            index++)
        {
            var raw =
                texture.PlatformData
                    .Mips[
                        index];

            if (raw.SizeX <= 0 ||
                raw.SizeY <= 0 ||
                raw.SizeX >
                    maxPreviewSize ||
                raw.SizeY >
                    maxPreviewSize)
            {
                continue;
            }

            var mip =
                texture.GetMip(
                    index);

            if (mip is not null)
                return index;
        }

        return -1;
    }

    private static byte[] ToRgba(
        EPixelFormat pixelFormat,
        byte[] data,
        int width,
        int height)
    {
        var expected =
            checked(
                width *
                height *
                4);

        if (data.Length != expected)
        {
            throw new NotSupportedException(
                $"Decoded pixel format {pixelFormat} is not a 4-byte browser frame.");
        }

        if (pixelFormat ==
            EPixelFormat
                .PF_R8G8B8A8)
        {
            return data;
        }

        if (pixelFormat ==
            EPixelFormat
                .PF_B8G8R8A8)
        {
            var rgba =
                data.ToArray();

            for (
                var offset = 0;
                offset <
                    rgba.Length;
                offset += 4)
            {
                (
                    rgba[offset],
                    rgba[
                        offset +
                        2]
                ) =
                (
                    rgba[
                        offset +
                        2],
                    rgba[offset]
                );
            }

            return rgba;
        }

        throw new NotSupportedException(
            $"Decoded browser Texture format {pixelFormat} is not supported yet.");
    }

    private static async Task WarmChunksAsync(
        IoStoreReader reader,
        string tocPath,
        IEnumerable<FIoChunkId> chunkIds,
        IReadOnlyDictionary<
            string,
            FFileManifest> files,
        Func<
            string,
            SparseBacking> backingFor,
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
                (ulong)
                offsetLength.Offset;

            var length =
                (ulong)
                offsetLength.Length;

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

            for (
                var blockIndex =
                    firstBlock;
                blockIndex <=
                    lastBlock;
                blockIndex++)
            {
                if (!warmed.Add(
                        blockIndex))
                {
                    continue;
                }

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

                if (partitionSize == 0)
                    throw new InvalidDataException(
                        "IoStore partition size is zero.");

                var absoluteOffset =
                    (ulong)
                    block.Offset;

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
                        partitionPath)
                        .TrimStart('/');

                if (!files.TryGetValue(
                        normalized,
                        out var partition))
                {
                    throw new FileNotFoundException(
                        "The live manifest does not contain the required IoStore UCAS partition.",
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
                    partition
                        .GetStream();

                var encrypted =
                    new byte[
                        rawSize];

                var read =
                    await source
                        .ReadAtAsync(
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
                    if (
                        reader.TocResource
                            .EncryptionMethod !=
                        EIoEncryptionMethod
                            .AES_CTR)
                    {
                        throw new NotSupportedException(
                            $"Browser IoStore supports AES-CTR only; got {reader.TocResource.EncryptionMethod}.");
                    }

                    if (key is null)
                    {
                        throw new InvalidDataException(
                            $"No live AES key matched encrypted reader {reader.Name}.");
                    }

                    plain =
                        BrowserAesCtr
                            .Transform(
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

                if (
                    backing.StoredBytes >
                    MaxSparseBytes)
                {
                    throw new InvalidDataException(
                        "The browser sparse UCAS budget was exceeded.");
                }
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

        if (
            beginOffset < 0 ||
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

        if (
            reader is not
                IoStoreReader io ||
            io.TocResource
                .EncryptionMethod !=
            EIoEncryptionMethod
                .AES_CTR)
        {
            throw new NotSupportedException(
                "Browser IoStore index decryption supports AES-CTR only.");
        }

        var aes =
            reader.AesKey
            ?? throw new InvalidDataException(
                "Browser IoStore index decryption has no active AES key.");

        return BrowserAesCtr
            .Transform(
                bytes.AsSpan(
                        beginOffset,
                        count)
                    .ToArray(),
                aes.Key,
                io.TocResource
                    .EncryptionIVs[^1]
                    .Bytes);
    }

    private static async Task<
        IReadOnlyList<
            KeyValuePair<
                CUE4Parse.UE4.Objects.Core.Misc.FGuid,
                FAesKey>>>
        GetAesKeysAsync(
            HttpClient client,
            Uri endpoint,
            CancellationToken cancellationToken)
    {
        var bytes =
            await GetBytesAsync(
                client,
                endpoint,
                MaxAesBytes,
                "Fortnite AES metadata",
                cancellationToken);

        using var document =
            JsonDocument.Parse(
                bytes);

        var result =
            new Dictionary<
                CUE4Parse.UE4.Objects.Core.Misc.FGuid,
                FAesKey>();

        var main =
            FindMainAesKey(
                document.RootElement);

        if (main is not null)
        {
            result[
                new CUE4Parse.UE4.Objects.Core.Misc.FGuid()] =
                new FAesKey(
                    main);
        }

        Walk(
            document.RootElement,
            element =>
            {
                if (element.ValueKind !=
                    JsonValueKind.Object)
                {
                    return;
                }

                string? key =
                    null;

                string? guid =
                    null;

                foreach (
                    var property in
                    element
                        .EnumerateObject())
                {
                    if (
                        property.Value
                            .ValueKind !=
                        JsonValueKind.String)
                    {
                        continue;
                    }

                    var name =
                        property.Name
                            .ToLowerInvariant();

                    var value =
                        property.Value
                            .GetString()
                            ?.Trim();

                    if (string.IsNullOrWhiteSpace(
                            value))
                    {
                        continue;
                    }

                    if (
                        (
                            name.Contains(
                                "key") ||
                            name.Contains(
                                "aes")
                        ) &&
                        LooksLikeAes(
                            value))
                    {
                        key ??=
                            NormalizeAes(
                                value);
                    }

                    if (
                        name.Contains(
                            "guid") &&
                        LooksLikeGuid(
                            value))
                    {
                        guid ??=
                            NormalizeGuid(
                                value);
                    }
                }

                if (
                    key is null ||
                    guid is null)
                {
                    return;
                }

                try
                {
                    result[
                        new CUE4Parse.UE4.Objects.Core.Misc.FGuid(
                            guid)] =
                        new FAesKey(
                            key);
                }
                catch
                {
                }
            });

        return result
            .ToArray();
    }

    private static string? FindMainAesKey(
        JsonElement root)
    {
        string? found =
            null;

        Walk(
            root,
            element =>
            {
                if (
                    found is not null ||
                    element.ValueKind !=
                        JsonValueKind.Object)
                {
                    return;
                }

                foreach (
                    var property in
                    element
                        .EnumerateObject())
                {
                    if (
                        property.Value
                            .ValueKind !=
                        JsonValueKind.String)
                    {
                        continue;
                    }

                    var name =
                        property.Name
                            .ToLowerInvariant();

                    var value =
                        property.Value
                            .GetString()
                            ?.Trim() ??
                        string.Empty;

                    if (!LooksLikeAes(
                            value))
                    {
                        continue;
                    }

                    if (
                        name is
                            "mainkey" or
                            "main_key" or
                            "mainaes" or
                            "mainaeskey" ||
                        (
                            name.Contains(
                                "main") &&
                            name.Contains(
                                "key")
                        ))
                    {
                        found =
                            NormalizeAes(
                                value);

                        return;
                    }
                }
            });

        return found;
    }

    private static FAesKey? FindKey(
        IoStoreReader reader,
        IReadOnlyList<
            KeyValuePair<
                CUE4Parse.UE4.Objects.Core.Misc.FGuid,
                FAesKey>> keys)
    {
        if (!reader.IsEncrypted)
            return null;

        foreach (var pair in keys)
        {
            if (
                pair.Key ==
                reader
                    .EncryptionKeyGuid)
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static async Task<byte[]>
        GetMappingsBytesAsync(
            HttpClient client,
            Uri endpoint,
            CancellationToken cancellationToken)
    {
        if (
            endpoint.AbsolutePath
                .Contains(
                    "usmap",
                    StringComparison.OrdinalIgnoreCase))
        {
            return await DownloadMappingsAsync(
                client,
                endpoint,
                cancellationToken);
        }

        var metadata =
            await GetBytesAsync(
                client,
                endpoint,
                MaxMetadataBytes,
                "Fortnite mappings metadata",
                cancellationToken);

        using var document =
            JsonDocument.Parse(
                metadata);

        var urls =
            FindUrls(
                    document.RootElement)
                .Where(
                    url =>
                        url.Contains(
                            "usmap",
                            StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(
                    ScoreMappingUrl)
                .ToArray();

        Exception? lastError =
            null;

        foreach (var url in urls)
        {
            try
            {
                return await DownloadMappingsAsync(
                    client,
                    RequireHttpUri(
                        url,
                        "Fortnite mappings file"),
                    cancellationToken);
            }
            catch (
                OperationCanceledException)
                when (
                    cancellationToken
                        .IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError =
                    ex;
            }
        }

        throw new InvalidOperationException(
            "No browser-compatible Fortnite mappings file was available.",
            lastError);
    }

    private static async Task<byte[]>
        DownloadMappingsAsync(
            HttpClient client,
            Uri endpoint,
            CancellationToken cancellationToken)
    {
        var bytes =
            await GetBytesAsync(
                client,
                endpoint,
                MaxMappingsBytes,
                "Fortnite mappings file",
                cancellationToken);

        if (
            bytes.Length >= 2 &&
            bytes[0] == 0x1f &&
            bytes[1] == 0x8b)
        {
            bytes =
                await DecompressGzipBoundedAsync(
                    bytes,
                    MaxMappingsExpandedBytes,
                    cancellationToken);
        }

        if (
            bytes.Length >= 4 &&
            bytes[0] == 0x28 &&
            bytes[1] == 0xb5 &&
            bytes[2] == 0x2f &&
            bytes[3] == 0xfd)
        {
            throw new NotSupportedException(
                "Zstandard-compressed mappings are not accepted by the browser runtime.");
        }

        if (bytes.Length < 32)
            throw new InvalidDataException(
                "Fortnite mappings payload is too small.");

        return bytes;
    }

    private static async Task<byte[]>
        DecompressGzipBoundedAsync(
            byte[] bytes,
            int maxBytes,
            CancellationToken cancellationToken)
    {
        await using var input =
            new MemoryStream(
                bytes,
                writable: false);

        await using var gzip =
            new GZipStream(
                input,
                CompressionMode.Decompress,
                leaveOpen: false);

        return await ReadFileAsync(
            gzip,
            maxBytes,
            "Expanded Fortnite mappings",
            cancellationToken);
    }

    private static IEnumerable<string>
        FindUrls(
            JsonElement root)
    {
        var values =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        Walk(
            root,
            element =>
            {
                if (
                    element.ValueKind !=
                    JsonValueKind.String)
                {
                    return;
                }

                var value =
                    element.GetString();

                if (
                    Uri.TryCreate(
                        value,
                        UriKind.Absolute,
                        out var uri) &&
                    uri.Scheme is
                        "http" or
                        "https")
                {
                    values.Add(
                        value!);
                }
            });

        return values;
    }

    private static int ScoreMappingUrl(
        string value)
    {
        var score =
            0;

        var lower =
            value.ToLowerInvariant();

        if (lower.EndsWith(
                ".usmap"))
        {
            score +=
                50;
        }

        if (lower.Contains(
                "latest"))
        {
            score +=
                10;
        }

        if (
            lower.Contains(
                "zstandard") ||
            lower.Contains(
                "zstd") ||
            lower.EndsWith(
                ".zst"))
        {
            score -=
                20;
        }

        return score;
    }

    private static void Walk(
        JsonElement root,
        Action<JsonElement> visitor)
    {
        visitor(
            root);

        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (
                    var item in
                    root.EnumerateArray())
                {
                    Walk(
                        item,
                        visitor);
                }
                break;

            case JsonValueKind.Object:
                foreach (
                    var property in
                    root.EnumerateObject())
                {
                    Walk(
                        property.Value,
                        visitor);
                }
                break;
        }
    }

    private static bool LooksLikeAes(
        string value)
    {
        var clean =
            value.Trim();

        if (clean.StartsWith(
                "0x",
                StringComparison.OrdinalIgnoreCase))
        {
            clean =
                clean[2..];
        }

        return clean.Length == 64 &&
               clean.All(
                   Uri.IsHexDigit);
    }

    private static bool LooksLikeGuid(
        string value)
    {
        var clean =
            NormalizeGuid(
                value);

        return clean.Length == 32 &&
               clean.All(
                   Uri.IsHexDigit);
    }

    private static string NormalizeAes(
        string value)
    {
        value =
            value.Trim();

        return value.StartsWith(
                "0x",
                StringComparison.OrdinalIgnoreCase)
            ? "0x" +
              value[2..]
                  .ToUpperInvariant()
            : value
                .ToUpperInvariant();
    }

    private static string NormalizeGuid(
        string value)
    {
        return value
            .Replace(
                "-",
                string.Empty,
                StringComparison.Ordinal)
            .Replace(
                "{",
                string.Empty,
                StringComparison.Ordinal)
            .Replace(
                "}",
                string.Empty,
                StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();
    }

    private static FFileManifest FindToc(
        FBuildPatchAppManifest manifest,
        string requestedPath)
    {
        var normalized =
            Normalize(
                requestedPath)
                .TrimStart('/');

        var exact =
            manifest.Files
                .Where(
                    file =>
                        Normalize(
                                file.FileName)
                            .TrimStart('/')
                            .Equals(
                                normalized,
                                StringComparison.OrdinalIgnoreCase))
                .ToArray();

        if (exact.Length == 1)
            return exact[0];

        var fileName =
            Path.GetFileName(
                normalized);

        var byName =
            manifest.Files
                .Where(
                    file =>
                        Path.GetFileName(
                                Normalize(
                                    file.FileName))
                            .Equals(
                                fileName,
                                StringComparison.OrdinalIgnoreCase))
                .ToArray();

        if (byName.Length != 1)
        {
            throw new InvalidDataException(
                $"Expected one live IoStore TOC for {requestedPath}; found {byName.Length}.");
        }

        return byName[0];
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
            uri.Scheme is not
                ("http" or
                 "https"))
        {
            throw new InvalidOperationException(
                $"{label} requires an absolute HTTP URL.");
        }

        if (
            ensureTrailingSlash &&
            !uri.AbsoluteUri
                .EndsWith(
                    "/",
                    StringComparison.Ordinal))
        {
            uri =
                new Uri(
                    uri.AbsoluteUri +
                    "/",
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

        if (
            declared is > 0 &&
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

                if (
                    output.Length +
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

    private static string NormalizeAssetPath(
        string path)
    {
        path =
            Normalize(
                path)
                .Trim();

        if (
            path.StartsWith(
                "Texture2D'",
                StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith(
                "'",
                StringComparison.Ordinal))
        {
            path =
                path[
                    10..^1];
        }

        var objectDot =
            path.LastIndexOf(
                '.');

        if (
            objectDot >
            path.LastIndexOf(
                '/') &&
            !path.EndsWith(
                ".uasset",
                StringComparison.OrdinalIgnoreCase))
        {
            path =
                path[
                    ..objectDot];
        }

        path =
            path.TrimStart('/');

        if (!Path.HasExtension(
                path))
        {
            path +=
                ".uasset";
        }

        return path;
    }

    private static string Normalize(
        string path)
    {
        return (
            path ??
            string.Empty)
            .Replace(
                '\\',
                '/');
    }
}

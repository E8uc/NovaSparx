// Offline CI preprocessing only. Bounded TOCs and single compression blocks,
// never complete UCAS downloads; no Back4App or manual database updates.
using System.Security.Cryptography;
using System.Text.Json;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using Microsoft.Extensions.Logging.Abstractions;
using NovaSparx.Backend;

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
using var traffic = new TrafficHandler();
using var client = new HttpClient(traffic) { Timeout = TimeSpan.FromSeconds(45) };
var sources = new PublicFortniteSources(client, NullLogger<PublicFortniteSources>.Instance);
var (manifest, version) = await sources.GetLiveManifestAsync(timeout.Token);
var keys = await sources.GetAesKeysAsync(timeout.Token);
var fixtures = new List<object>();
// Container identities supplied in the live key listing; exact manifest match
// required. No hardcoded build or keys, and no substitute fixture on failure.
foreach (var name in new[] { "pakchunk1017-WindowsClient", "pakchunk1002-WindowsClient" })
{
    var file = manifest.Files.Single(f => f.FileName.EndsWith($"/{name}.utoc", StringComparison.OrdinalIgnoreCase));
    await using var stream = file.GetStream();
    if (stream.Length < 144 || stream.Length > 2 * 1024 * 1024)
        throw new InvalidDataException("TOC exceeds the 2 MiB fixture budget");
    var tocBytes = new byte[checked((int)stream.Length)];
    await stream.ReadExactlyAsync(tocBytes, timeout.Token);
    using var archive = new FByteArchive(file.FileName, tocBytes, new VersionContainer(EGame.GAME_UE6_0));
    var toc = new FIoStoreTocResource(archive, EIoStoreTocReadOptions.ReadDirectoryIndex);
    var key = keys.Single(pair => pair.Key == toc.Header.EncryptionKeyGuid).Value;
    if (toc.EncryptionMethod != EIoEncryptionMethod.AES_CTR)
        throw new InvalidDataException($"This CTR fixture requires CTR metadata, got {toc.EncryptionMethod}");
    var encryptedIndex = toc.GetDirectoryIndexBuffer() ?? throw new InvalidDataException("No directory index");
    var indexIv = toc.EncryptionIVs[^1].Bytes;
    var indexPlain = encryptedIndex.ToArray().CryptCtr(0, encryptedIndex.Length, key, indexIv);
    using var indexArchive = new FByteArchive("decrypted-index", indexPlain);
    var mountPoint = indexArchive.ReadFString();
    if (!mountPoint.StartsWith("../../../", StringComparison.Ordinal))
        throw new InvalidDataException("Desktop CUE4Parse decrypted an invalid mount point");

    // Prefer an actual Oodle block when present. Each range is exactly one TOC
    // compression block. The BuildPatch transport can fetch its enclosing chunk.
    var blockIndex = Array.FindIndex(toc.CompressionBlocks, b => toc.CompressionMethods[b.CompressionMethodIndex] == CompressionMethod.Oodle);
    if (blockIndex < 0) blockIndex = 0;
    var block = toc.CompressionBlocks[blockIndex];
    if (block.CompressedSize == 0 || block.CompressedSize > 256 * 1024 || block.UncompressedSize > 256 * 1024)
        throw new InvalidDataException("Compression block exceeds fixture budget");
    var partition = checked((ulong)block.Offset / toc.Header.PartitionSize);
    var offset = checked((long)((ulong)block.Offset % toc.Header.PartitionSize));
    var casName = partition == 0 ? $"{name}.ucas" : $"{name}_s{partition}.ucas";
    var casFile = manifest.Files.Single(f => f.FileName.EndsWith("/" + casName, StringComparison.OrdinalIgnoreCase));
    await using var cas = casFile.GetStream();
    if (offset < 0 || offset > cas.Length - block.CompressedSize)
        throw new InvalidDataException("Block range exceeds its UCAS partition");
    cas.Position = offset;
    var encryptedBlock = new byte[checked((int)block.CompressedSize)];
    await cas.ReadExactlyAsync(encryptedBlock, timeout.Token);
    var blockIv = toc.EncryptionIVs[blockIndex].Bytes;
    var decryptedBlock = encryptedBlock.ToArray().CryptCtr(0, encryptedBlock.Length, key, blockIv);
    var method = toc.CompressionMethods[block.CompressionMethodIndex];
    var decoded = Compression.Decompress(decryptedBlock, checked((int)block.UncompressedSize), method);
    var fixture = new
    {
        logicalPath = file.FileName, tocBase64 = Convert.ToBase64String(tocBytes), tocSha256 = Hash(tocBytes),
        parserGame = "GAME_UE6_0", encryptionMethod = "AES_CTR",
        encryptionKeyGuid = toc.Header.EncryptionKeyGuid.ToString(), publicKeyHex = Convert.ToHexString(key.Key),
        indexIvHex = Convert.ToHexString(indexIv), indexSha256 = Hash(indexPlain), mountPoint,
        block = new
        {
            index = blockIndex, path = casFile.FileName, offset, compressedSize = encryptedBlock.Length,
            uncompressedSize = decoded.Length, method = method.ToString(), ivHex = Convert.ToHexString(blockIv),
            encryptedBase64 = Convert.ToBase64String(encryptedBlock), encryptedSha256 = Hash(encryptedBlock),
            decryptedSha256 = Hash(decryptedBlock), decodedSha256 = Hash(decoded)
        }
    };
    fixtures.Add(fixture);
    Console.WriteLine($"REAL_FORTNITE_BLOCK {file.FileName} build={version} tocBytes={tocBytes.Length} indexHash={fixture.indexSha256} block={blockIndex} method={method} compressed={encryptedBlock.Length} decoded={decoded.Length} decodedHash={fixture.block.decodedSha256}");
}
var output = new
{
    source = "Current public BuildPatch manifest / EpicManifestParser, desktop CUE4Parse 1.2.2.202609",
    manifestBuild = version, aesSource = "https://export-service-new.dillyapis.com/v1/aes",
    databaseBuildEquivalence = "unverified; manual database unchanged", fixtures,
    networkRequests = traffic.Requests, httpDeclaredResponseBytes = traffic.DeclaredBytes,
    networkAccounting = "Content-Length sum, excludes responses without Content-Length; includes manifests and containing BuildPatch chunks, not just logical ranges",
    assetParsingProven = false
};
var destination = args.Length > 0 ? args[0] : "fixture.json";
await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }), timeout.Token);
static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
sealed class TrafficHandler : DelegatingHandler
{
    public long Requests;
    public long DeclaredBytes;
    public TrafficHandler() : base(new HttpClientHandler()) { }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Requests);
        var response = await base.SendAsync(request, cancellationToken);
        Interlocked.Add(ref DeclaredBytes, response.Content.Headers.ContentLength ?? 0);
        return response;
    }
}

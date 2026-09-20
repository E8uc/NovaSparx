// Offline CI preprocessing only. Does not run in Back4App or update the manual
// asset database. Reads 144 bytes of one real TOC; never opens a UCAS stream.
using System.Security.Cryptography;
using System.Text.Json;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Readers;
using Microsoft.Extensions.Logging.Abstractions;
using NovaSparx.Backend;

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
var sources = new PublicFortniteSources(client, NullLogger<PublicFortniteSources>.Instance);
var (manifest, version) = await sources.GetLiveManifestAsync(timeout.Token);
// A small keyed container supplied by the user, resolved against the LIVE manifest.
// Missing container/key is a real failure; never substitute unrelated bytes.
var file = manifest.Files.Single(f => f.FileName.EndsWith("/pakchunk1017-WindowsClient.utoc", StringComparison.OrdinalIgnoreCase));
var keys = await sources.GetAesKeysAsync(timeout.Token);
await using var stream = file.GetStream();
var headerBytes = new byte[144];
await stream.ReadExactlyAsync(headerBytes, timeout.Token);
using var archive = new FByteArchive(file.FileName, headerBytes);
var header = new FIoStoreTocHeader(archive);
if (!keys.TryGetValue(header.EncryptionKeyGuid, out var key))
    throw new InvalidOperationException($"No current AES key for TOC GUID {header.EncryptionKeyGuid}");
var output = new
{
    source = "Current public Fortnite BuildPatch manifest via PublicFortniteSources / EpicManifestParser",
    manifestBuild = version,
    aesSource = "https://export-service-new.dillyapis.com/v1/aes",
    encryptionKeyGuid = header.EncryptionKeyGuid.ToString(),
    keyResolved = true,
    decryptionProven = false,
    databaseBuildEquivalence = "unverified; manual database unchanged",
    logicalPath = file.FileName,
    logicalBytesRead = headerBytes.Length,
    ucasBytesRead = 0,
    transportBytes = "BuildPatch may fetch the containing compressed transport chunk; not just 144 network bytes",
    headerBase64 = Convert.ToBase64String(headerBytes),
    sha256 = Convert.ToHexString(SHA256.HashData(headerBytes)),
    expected = new
    {
        version = (byte)header.Version, headerSize = header.TocHeaderSize,
        entries = header.TocEntryCount, compressionBlocks = header.TocCompressedBlockEntryCount,
        compressionBlockSize = header.CompressionBlockSize, directoryIndexSize = header.DirectoryIndexSize
    },
    assetParsingProven = false
};
var destination = args.Length > 0 ? args[0] : "fixture.json";
await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }), timeout.Token);
Console.WriteLine($"REAL_UTOC_FIXTURE {file.FileName} {version} sha256={output.sha256}");

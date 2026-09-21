using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.Compression;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

Console.WriteLine("NovaSparx CUE4Parse browser probe");

if (!OperatingSystem.IsBrowser())
{
    Console.Error.WriteLine("Probe is not running on the browser-wasm runtime.");
    return 2;
}

var texturePathArgument = args.FirstOrDefault(
    argument => argument.StartsWith(
        "--resolve-texture-path=",
        StringComparison.Ordinal));

if (texturePathArgument is not null)
{
    static string RequiredArgument(
        string[] arguments,
        string prefix)
    {
        var value =
            arguments.FirstOrDefault(
                argument =>
                    argument.StartsWith(
                        prefix,
                        StringComparison.Ordinal));

        if (value is null)
        {
            throw new ArgumentException(
                $"Missing required browser Texture runtime argument: {prefix}");
        }

        var result =
            value[
                prefix.Length..];

        if (string.IsNullOrWhiteSpace(
                result))
        {
            throw new ArgumentException(
                $"Browser Texture runtime argument is empty: {prefix}");
        }

        return result;
    }

    var assetPath =
        texturePathArgument[
            "--resolve-texture-path=".Length..];

    var containerToc =
        RequiredArgument(
            args,
            "--resolve-texture-toc=");

    var manifestUrl =
        RequiredArgument(
            args,
            "--resolve-texture-manifest-url=");

    var chunkBaseUrl =
        RequiredArgument(
            args,
            "--resolve-texture-chunk-base=");

    var mappingsApiUrl =
        RequiredArgument(
            args,
            "--resolve-texture-mappings-api=");

    var aesApiUrl =
        RequiredArgument(
            args,
            "--resolve-texture-aes-api=");

    var maxSizeArgument =
        args.FirstOrDefault(
            argument =>
                argument.StartsWith(
                    "--resolve-texture-max-size=",
                    StringComparison.Ordinal));

    var maxPreviewSize =
        1024;

    if (
        maxSizeArgument is not null &&
        !int.TryParse(
            maxSizeArgument[
                "--resolve-texture-max-size=".Length..],
            out maxPreviewSize))
    {
        throw new ArgumentException(
            "Invalid browser Texture max preview size.");
    }

    using var timeout =
        new CancellationTokenSource(
            TimeSpan.FromSeconds(
                300));

    await BrowserTextureRuntime.RunAsync(
        assetPath,
        containerToc,
        manifestUrl,
        chunkBaseUrl,
        mappingsApiUrl,
        aesApiUrl,
        maxPreviewSize,
        timeout.Token);

    return 0;
}

var liveTextureArgument = args.FirstOrDefault(
    argument => argument.StartsWith(
        "--live-texture-base=",
        StringComparison.Ordinal));

if (liveTextureArgument is not null)
{
    var baseUrl = liveTextureArgument[
        "--live-texture-base=".Length..];

    using var timeout =
        new CancellationTokenSource(
            TimeSpan.FromSeconds(210));

    var manifestArgument =
        args.FirstOrDefault(
            argument =>
                argument.StartsWith(
                    "--live-texture-manifest-url=",
                    StringComparison.Ordinal));

    var chunkBaseArgument =
        args.FirstOrDefault(
            argument =>
                argument.StartsWith(
                    "--live-texture-chunk-base=",
                    StringComparison.Ordinal));

    var manifestUrl =
        manifestArgument is null
            ? null
            : manifestArgument[
                "--live-texture-manifest-url=".Length..];

    var chunkBaseUrl =
        chunkBaseArgument is null
            ? null
            : chunkBaseArgument[
                "--live-texture-chunk-base=".Length..];

    await LiveTextureProbe.RunAsync(
        baseUrl,
        timeout.Token,
        manifestUrl,
        chunkBaseUrl);

    return 0;
}

var liveBuildPatchArgument = args.FirstOrDefault(
    argument => argument.StartsWith(
        "--live-buildpatch-base=",
        StringComparison.Ordinal));

if (liveBuildPatchArgument is not null)
{
    var baseUrl = liveBuildPatchArgument[
        "--live-buildpatch-base=".Length..];

    using var timeout =
        new CancellationTokenSource(
            TimeSpan.FromSeconds(110));

    await LiveBuildPatchProbe.RunAsync(
        baseUrl,
        timeout.Token);

    return 0;
}

if (args.Contains("--reject-partial-block"))
{
    // Assert this failure at the real JS/WASM boundary, in a fresh browser run.
    BrowserAesEcb.Decrypt(new byte[16], 0, 15, new byte[32]);
    return 3;
}

if (args.Contains("--cancel-ctr"))
{
    using var cancelledCtr = new CancellationTokenSource();
    cancelledCtr.Cancel();
    BrowserAesCtr.Transform(new byte[17], new byte[32], new byte[12], cancelledCtr.Token);
    return 4; // A cancelled request must never return successfully.
}

Console.WriteLine(typeof(IoStoreReader).Assembly.FullName);
Console.WriteLine(typeof(IoStoreReader).FullName);
Console.WriteLine(typeof(FIoStoreTocResource).FullName);

// Compatibility vectors, explicitly not Fortnite assets. Exercise code rather
// than only typeof: loading an assembly misses platform-specific native calls.
using (var archive = new FByteArchive("little-endian-vector", [0x78, 0x56, 0x34, 0x12]))
{
    if (archive.Read<uint>() != 0x12345678 || archive.Position != 4)
        throw new InvalidOperationException("CUE4Parse archive runtime read failed");
    Console.WriteLine("CUE4PARSE_STAGE|archive-read|supported");
}

Probe("aes-256-ecb", () =>
{
    // FIPS-197 AES-256 known-answer vector. Uses the real CUE4Parse AES path.
    var encrypted = Convert.FromHexString("8EA2B7CA516745BFEAFC49904B496089");
    var key = new FAesKey("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
    var decrypted = CUE4Parse.Encryption.Aes.Aes.Decrypt(encrypted, key);
    if (Convert.ToHexString(decrypted) != "00112233445566778899AABBCCDDEEFF")
        throw new InvalidOperationException("AES known-answer mismatch");
});

Probe("zlib", () =>
{
    var expected = Encoding.UTF8.GetBytes("Unreal archive compatibility vector");
    var compressed = Convert.FromBase64String("eJwLzStKTcxRSCxKzsgsS1VIzs8tSCzJTMrMySypVChLTS7JLwIA8lENtw==");
    var decoded = Compression.Decompress(compressed, expected.Length, CompressionMethod.Zlib);
    if (!decoded.SequenceEqual(expected)) throw new InvalidOperationException("Zlib known-answer mismatch");
});

// This stage is required, not merely reported. A bad adapter must fail CI.
var cipher = Convert.FromHexString("8EA2B7CA516745BFEAFC49904B496089");
var aesKey = Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
var plain = BrowserAesEcb.Decrypt(cipher, 0, cipher.Length, aesKey);
if (Convert.ToHexString(plain) != "00112233445566778899AABBCCDDEEFF")
    throw new InvalidOperationException("Managed browser AES vector mismatch");
// Verify offset handling, immutable input, cancellation and alignment rejection.
var padded = new byte[48];
cipher.CopyTo(padded, 16);
if (!BrowserAesEcb.Decrypt(padded, 16, 16, aesKey).SequenceEqual(plain) || !padded.Skip(16).Take(16).SequenceEqual(cipher))
    throw new InvalidOperationException("Managed AES range/input preservation failed");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try { BrowserAesEcb.Decrypt(cipher, 0, 16, aesKey, cancelled.Token); throw new InvalidOperationException("AES ignored cancellation"); }
    catch (OperationCanceledException) { }
}
Console.WriteLine("CUE4PARSE_STAGE|managed-aes-256-ecb|supported");

using (var fixtureStream = typeof(BrowserAesEcb).Assembly.GetManifestResourceStream("real-utoc-fixture.json")
    ?? throw new InvalidOperationException("Real TOC fixture was not generated"))
using (var fixture = JsonDocument.Parse(fixtureStream))
{
    foreach (var item in fixture.RootElement.GetProperty("fixtures").EnumerateArray())
    {
        var bytes = Convert.FromBase64String(item.GetProperty("tocBase64").GetString()!);
        if (bytes.Length < 144 || bytes.Length > 2 * 1024 * 1024 || Hash(bytes) != item.GetProperty("tocSha256").GetString())
            throw new InvalidDataException("TOC bytes/hash disagree with desktop fixture");
        using var archive = new FByteArchive(item.GetProperty("logicalPath").GetString()!, bytes, new VersionContainer(EGame.GAME_UE6_0));
        var toc = new FIoStoreTocResource(archive, EIoStoreTocReadOptions.ReadDirectoryIndex);
        if (toc.EncryptionMethod != EIoEncryptionMethod.AES_CTR || toc.Header.EncryptionKeyGuid.ToString() != item.GetProperty("encryptionKeyGuid").GetString())
            throw new InvalidDataException("Browser TOC encryption metadata disagrees with desktop");
        var key = Convert.FromHexString(item.GetProperty("publicKeyHex").GetString()!);
        var indexIv = toc.EncryptionIVs[^1].Bytes;
        if (Convert.ToHexString(indexIv) != item.GetProperty("indexIvHex").GetString())
            throw new InvalidDataException("Browser index IV disagrees with desktop");
        var indexPlain = BrowserAesCtr.Transform(toc.GetDirectoryIndexBuffer()!, key, indexIv);
        if (Hash(indexPlain) != item.GetProperty("indexSha256").GetString())
            throw new InvalidDataException("Real index CTR bytes disagree with desktop CUE4Parse");
        using var indexArchive = new FByteArchive("index", indexPlain);
        if (indexArchive.ReadFString() != item.GetProperty("mountPoint").GetString())
            throw new InvalidDataException("Index mount point mismatch");
        Console.WriteLine($"CUE4PARSE_STAGE|real-toc-ctr-index|supported|{archive.Name}|{Hash(indexPlain)}");

        var expected = item.GetProperty("block");
        var blockIndex = expected.GetProperty("index").GetInt32();
        var block = toc.CompressionBlocks[blockIndex];
        var encrypted = Convert.FromBase64String(expected.GetProperty("encryptedBase64").GetString()!);
        if (encrypted.Length > 256 * 1024 || block.UncompressedSize > 256 * 1024 || encrypted.Length != block.CompressedSize ||
            Hash(encrypted) != expected.GetProperty("encryptedSha256").GetString() ||
            block.UncompressedSize != expected.GetProperty("uncompressedSize").GetUInt32() ||
            (long)((ulong)block.Offset % toc.Header.PartitionSize) != expected.GetProperty("offset").GetInt64())
            throw new InvalidDataException("Browser compression range disagrees with desktop");
        var blockIv = toc.EncryptionIVs[blockIndex].Bytes;
        if (Convert.ToHexString(blockIv) != expected.GetProperty("ivHex").GetString())
            throw new InvalidDataException("Browser block IV mismatch");
        var decrypted = BrowserAesCtr.Transform(encrypted, key, blockIv);
        if (Hash(decrypted) != expected.GetProperty("decryptedSha256").GetString() || Hash(encrypted) != expected.GetProperty("encryptedSha256").GetString())
            throw new InvalidDataException("Real block CTR bytes mismatch or input mutation");
        // Prefixes include non-aligned CTR tails; each must equal the desktop result.
        foreach (var length in new[] { 1, 15, 17, Math.Min(4097, encrypted.Length) }.Where(n => n <= encrypted.Length))
            if (!BrowserAesCtr.Transform(encrypted[..length], key, blockIv).SequenceEqual(decrypted[..length]))
                throw new InvalidDataException("CTR partial-block output mismatch");
        var method = toc.CompressionMethods[block.CompressionMethodIndex];
        if (method.ToString() != expected.GetProperty("method").GetString()) throw new InvalidDataException("Compression method mismatch");
        var decoded = Compression.Decompress(decrypted, checked((int)block.UncompressedSize), method);
        if (Hash(decoded) != expected.GetProperty("decodedSha256").GetString())
            throw new InvalidDataException("Real block decompressed bytes disagree with desktop CUE4Parse");
        Console.WriteLine($"CUE4PARSE_STAGE|real-ucas-block|supported|{archive.Name}|{method}|{decoded.Length}|{Hash(decoded)}");
    }
}

TexturePackageProbe.RunIfPresent();

Console.WriteLine("CUE4PARSE_ASSET_PARSING_UNPROVEN");
Console.WriteLine("CUE4PARSE_BROWSER_WASM_OK");

return 0;

static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

static void Probe(string name, Action action)
{
    try { action(); Console.WriteLine($"CUE4PARSE_STAGE|{name}|supported"); }
    catch (Exception error)
    {
        // The probe reports compatibility gaps; it never enables a feature.
        var cause = error.GetBaseException();
        Console.WriteLine($"CUE4PARSE_STAGE|{name}|unsupported|{cause.GetType().Name}: {cause.Message}");
    }
}

using CUE4Parse.UE4.IO;
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

if (args.Contains("--reject-partial-block"))
{
    // Assert this failure at the real JS/WASM boundary, in a fresh browser run.
    BrowserAesEcb.Decrypt(new byte[16], 0, 15, new byte[32]);
    return 3;
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
    var root = fixture.RootElement;
    var bytes = Convert.FromBase64String(root.GetProperty("headerBase64").GetString()!);
    if (bytes.Length != 144 || Convert.ToHexString(SHA256.HashData(bytes)) != root.GetProperty("sha256").GetString())
        throw new InvalidOperationException("Real TOC bytes/hash disagree with desktop fixture");
    using var archive = new FByteArchive(root.GetProperty("logicalPath").GetString()!, bytes);
    var header = new FIoStoreTocHeader(archive);
    var expected = root.GetProperty("expected");
    if ((byte)header.Version != expected.GetProperty("version").GetByte() ||
        header.TocHeaderSize != expected.GetProperty("headerSize").GetUInt32() ||
        header.TocEntryCount != expected.GetProperty("entries").GetUInt32() ||
        header.TocCompressedBlockEntryCount != expected.GetProperty("compressionBlocks").GetUInt32() ||
        header.CompressionBlockSize != expected.GetProperty("compressionBlockSize").GetUInt32() ||
        header.DirectoryIndexSize != expected.GetProperty("directoryIndexSize").GetUInt32())
        throw new InvalidOperationException("Browser TOC fields disagree with desktop CUE4Parse");
    Console.WriteLine($"CUE4PARSE_STAGE|real-utoc-header|supported|{root.GetProperty("logicalPath").GetString()}|{root.GetProperty("sha256").GetString()}");
}

Console.WriteLine("CUE4PARSE_ASSET_PARSING_UNPROVEN");
Console.WriteLine("CUE4PARSE_BROWSER_WASM_OK");

return 0;

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

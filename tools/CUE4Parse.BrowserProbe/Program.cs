using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.Compression;
using System.Text;

Console.WriteLine("NovaSparx CUE4Parse browser probe");

if (!OperatingSystem.IsBrowser())
{
    Console.Error.WriteLine("Probe is not running on the browser-wasm runtime.");
    return 2;
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

// Offline reference capture, never a hosted endpoint. No external rendered images.
using System.Security.Cryptography;
using System.Text.Json;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;
using Microsoft.Extensions.Logging.Abstractions;
using NovaSparx.Backend;

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
var sources = new PublicFortniteSources(client, NullLogger<PublicFortniteSources>.Instance);
var (manifest, build) = await sources.GetLiveManifestAsync(timeout.Token);
var keys = await sources.GetAesKeysAsync(timeout.Token);
var versions = new VersionContainer(EGame.GAME_UE6_0);
using var provider = new StreamedFileProvider("Fortnite", versions, StringComparer.OrdinalIgnoreCase);
var files = manifest.Files.ToDictionary(f => f.FileName.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
var tocs = new Dictionary<string, byte[]>();
var budget = new ReadBudget();
foreach (var name in new[] { "global.utoc", "pakchunk1002-WindowsClient.utoc" })
{
    var file = manifest.Files.Single(f => f.FileName.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase));
    await using var stream = file.GetStream();
    if (stream.Length > 2 * 1024 * 1024) throw new InvalidDataException("TOC fixture exceeds 2 MiB");
    var bytes = new byte[checked((int)stream.Length)];
    await stream.ReadExactlyAsync(bytes, timeout.Token);
    tocs[name] = bytes;
    provider.RegisterVfs(new FByteArchive(file.FileName, bytes, versions), null,
        path => new FStreamArchive(path, new BoundedReads(files[path.Replace('\\', '/')].GetStream(), budget, timeout.Token), versions));
}
await provider.MountAsync();
await provider.SubmitKeysAsync(keys);
if (provider.GlobalData is null) throw new InvalidDataException("Global script data failed to mount");
var mappings = await sources.GetMappingsAsync(timeout.Token) ?? throw new InvalidDataException("No mappings");
provider.MappingsContainer = mappings;
var mappingBytes = await File.ReadAllBytesAsync(Path.Combine(sources.MappingsCache, mappings.FileName), timeout.Token);
if (mappingBytes.Length > 24 * 1024 * 1024) throw new InvalidDataException("Mappings exceed fixture budget");
var global = (IoStoreReader)provider.GetArchive("global.utoc");
var scripts = global.Read(new FIoChunkId(0, 0, EIoChunkType5.ScriptObjects));
TextureDecoder.UseAssetRipperTextureDecoder = true;
var failures = new List<object>();
object? selected = null;
foreach (var file in provider.Files.Values.Where(f => f.Path.Contains("/T_", StringComparison.OrdinalIgnoreCase) && f.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Size).Take(24))
{
    timeout.Token.ThrowIfCancellationRequested();
    try
    {
        if (provider.LoadPackage(file).GetExport(Path.GetFileNameWithoutExtension(file.Path)) is not UTexture2D texture) throw new InvalidDataException("Root is not Texture2D");
        if (texture.PlatformData.VTData is not null) throw new NotSupportedException("Virtual texture excluded from initial package fixture");
        if (texture.Format is not (EPixelFormat.PF_DXT1 or EPixelFormat.PF_DXT3 or EPixelFormat.PF_DXT5))
            throw new NotSupportedException($"Initial fixture excludes {texture.Format}");
        var mipIndex = texture.GetMipIndexByMaxSize(256);
        var mip = texture.GetMip(mipIndex) ?? throw new InvalidDataException("No resident mip");
        if (mip.SizeX <= 0 || mip.SizeY <= 0 || (long)mip.SizeX * mip.SizeY > 256 * 256)
            throw new InvalidDataException("Selected mip exceeds 256x256 pixel budget");
        var decoded = texture.DecodeMip(mipIndex) ?? throw new InvalidDataException("No decoded pixels");
        if (decoded.PixelFormat != EPixelFormat.PF_R8G8B8A8) throw new InvalidDataException("Expected RGBA8");
        provider.Files.FindPayloads(file, out var uexp, out var ubulks, out var uptnls, true);
        if (file.Size + (uexp?.Size ?? 0) + ubulks.Sum(f => f.Size) + uptnls.Sum(f => f.Size) > 8 * 1024 * 1024)
            throw new InvalidDataException("Package payloads exceed 8 MiB");
        var parts = provider.SavePackage(file).Select(p => new { path = p.Key, bytesBase64 = Convert.ToBase64String(p.Value), sha256 = Hash(p.Value) }).ToArray();
        selected = new { path = file.Path, rootClass = texture.GetType().Name, objectName = texture.Name, mipIndex,
            width = decoded.Width, height = decoded.Height, format = texture.Format.ToString(),
            mipSha256 = Hash(mip.BulkData.Data), pixelsSha256 = Hash(decoded.Data), parts,
            rgbaBase64 = Convert.ToBase64String(decoded.Data) };
        Console.WriteLine($"REAL_TEXTURE_REFERENCE path={file.Path} class={texture.GetType().Name} mip={mipIndex} size={decoded.Width}x{decoded.Height} format={texture.Format} pixels={Hash(decoded.Data)}");
        break;
    }
    catch (Exception error)
    {
        failures.Add(new { path = file.Path, error = error.GetBaseException().Message });
        Console.WriteLine($"TEXTURE_REFERENCE_FAILURE {file.Path}: {error.GetBaseException().Message}");
    }
}
if (selected is null) throw new InvalidDataException("No real Texture2D fixture could be extracted; see per-path failures");
var output = new { build, databaseBuildEquivalence = "unverified", source = "Live BuildPatch bytes parsed by desktop CUE4Parse",
    parserGame = "GAME_UE6_0", selected, failures,
    globalTocBase64 = Convert.ToBase64String(tocs["global.utoc"]),
    scriptObjectsBase64 = Convert.ToBase64String(scripts), scriptObjectsSha256 = Hash(scripts),
    mappingsBase64 = Convert.ToBase64String(mappingBytes), mappingsSha256 = Hash(mappingBytes),
    logicalCasBytesRead = budget.Bytes, browserPackageParsingProven = false };
await File.WriteAllTextAsync(args[0], JsonSerializer.Serialize(output), timeout.Token);
static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
sealed class ReadBudget { public long Bytes; }
sealed class BoundedReads(Stream inner, ReadBudget budget, CancellationToken token) : Stream
{
    public override int Read(byte[] buffer, int offset, int count)
    {
        token.ThrowIfCancellationRequested();
        if (count > 4 * 1024 * 1024 || Interlocked.Add(ref budget.Bytes, count) > 64 * 1024 * 1024)
            throw new InvalidDataException("Reference capture exceeds bounded read budget");
        return inner.Read(buffer, offset, count);
    }
    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}

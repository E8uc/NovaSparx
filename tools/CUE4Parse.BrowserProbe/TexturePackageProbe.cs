using System.Runtime.InteropServices.JavaScript;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;

internal static partial class TexturePackageProbe
{
    [JSImport("render", "texture-view")]
    internal static partial void Render(int width, int height, string rgba, string path);

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, typeof(UTexture2D))]
    public static void RunIfPresent()
    {
        using var resource = typeof(TexturePackageProbe).Assembly.GetManifestResourceStream("texture-package-fixture.json");
        if (resource is null) return;
        // CUE4Parse constructs registered classes through Activator. Keep the
        // actual constructor under WASM trimming; do not force the asset type.
        ObjectTypeRegistry.RegisterClass("Texture2D", typeof(UTexture2D));
        if (Activator.CreateInstance(typeof(UTexture2D)) is not UTexture2D)
            throw new InvalidOperationException("Texture2D constructor unavailable in browser runtime");
        CUE4Parse.Globals.FatalObjectSerializationErrors = true;
        using var document = JsonDocument.Parse(resource);
        var root = document.RootElement;
        var item = root.GetProperty("selected");
        var versions = new VersionContainer(EGame.GAME_UE6_0);
        var scripts = ReadBytes(root, "scriptObjectsBase64", 16 * 1024 * 1024);
        var mappings = ReadBytes(root, "mappingsBase64", 24 * 1024 * 1024);
        if (Hash(scripts) != root.GetProperty("scriptObjectsSha256").GetString() || Hash(mappings) != root.GetProperty("mappingsSha256").GetString())
            throw new InvalidDataException("Global data/mappings differ from captured reference");
        using var globals = new ScriptObjectsReader(ReadBytes(root, "globalTocBase64", 2 * 1024 * 1024), scripts, versions);
        using var provider = new PackageFixtureProvider(new IoGlobalData(globals), versions);
        provider.MappingsContainer = new MemoryMappings(mappings);
        var parts = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in item.GetProperty("parts").EnumerateArray())
        {
            var bytes = ReadBytes(part, "bytesBase64", 8 * 1024 * 1024);
            if (Hash(bytes) != part.GetProperty("sha256").GetString()) throw new InvalidDataException("Package part hash mismatch");
            parts.Add(part.GetProperty("path").GetString()!, bytes);
            if (parts.Values.Sum(p => (long)p.Length) > 8 * 1024 * 1024) throw new InvalidDataException("Package budget exceeded");
        }
        var path = item.GetProperty("path").GetString()!;
        FArchive? Payload(string extension)
        {
            var candidates = parts.Where(p => p.Key.EndsWith(extension, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length > 1) throw new InvalidDataException("Ambiguous package payload");
            return candidates.Length == 0 ? null : new FByteArchive(candidates[0].Key, candidates[0].Value, versions);
        }
        using var uasset = new FByteArchive(path, parts[path], versions);
        using var ubulk = Payload(".ubulk");
        using var uptnl = Payload(".uptnl");
        IPackage package = new IoPackage(uasset, null, ubulk, uptnl, provider);
        var rootObject = package.GetExport(item.GetProperty("objectName").GetString()!);
        if (rootObject is not UTexture2D texture)
            throw new InvalidDataException($"Real package root is {rootObject.GetType().Name}, export type {rootObject.ExportType}; expected UTexture2D");
        if (texture.Format.ToString() != item.GetProperty("format").GetString()) throw new InvalidDataException("Texture format mismatch");
        var mipIndex = item.GetProperty("mipIndex").GetInt32();
        var mip = texture.GetMip(mipIndex) ?? throw new InvalidDataException("Missing real texture mip");
        if (mip.SizeX <= 0 || mip.SizeY <= 0 || (long)mip.SizeX * mip.SizeY > 256 * 256 || Hash(mip.BulkData.Data) != item.GetProperty("mipSha256").GetString())
            throw new InvalidDataException("Parsed mip differs from desktop or exceeds pixel budget");
        TextureDecoder.UseAssetRipperTextureDecoder = true;
        var decoded = texture.DecodeMip(mipIndex) ?? throw new InvalidDataException("Browser texture decoder produced no pixels");
        if (decoded.Width != item.GetProperty("width").GetInt32() || decoded.Height != item.GetProperty("height").GetInt32() ||
            decoded.PixelFormat != EPixelFormat.PF_R8G8B8A8 || Hash(decoded.Data) != item.GetProperty("pixelsSha256").GetString())
            throw new InvalidDataException("Browser decoded pixels disagree with desktop CUE4Parse");
        Console.WriteLine($"REAL_TEXTURE_BROWSER_OK|{path}|{texture.Format}|{mipIndex}|{decoded.Width}x{decoded.Height}|{Hash(decoded.Data)}");
        Render(decoded.Width, decoded.Height, Convert.ToBase64String(decoded.Data), path);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static byte[] ReadBytes(JsonElement root, string key, int limit)
    {
        var encoded = root.GetProperty(key).GetString()!;
        if (encoded.Length > (long)(limit + 2) / 3 * 4) throw new InvalidDataException("Encoded fixture budget exceeded");
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length > limit) throw new InvalidDataException("Fixture byte budget exceeded");
        return bytes;
    }
}
// Replays the captured, already decompressed global ScriptObjects chunk. No
// fabricated names/classes and no remote calls. Transport is tested separately.
file sealed class ScriptObjectsReader : IoStoreReader
{
    private readonly byte[] _scripts;
    public ScriptObjectsReader(byte[] toc, byte[] scripts, VersionContainer versions)
        : base(new FByteArchive("global.utoc", toc, versions), path => new FByteArchive(path, [], versions)) => _scripts = scripts;
    public override byte[] Read(FIoChunkId chunkId) => chunkId.Equals(new FIoChunkId(0, 0, EIoChunkType5.ScriptObjects))
        ? _scripts : throw new InvalidDataException("Uncaptured global chunk requested");
}
file sealed class PackageFixtureProvider : StreamedFileProvider, IVfsFileProvider
{
    private readonly IoGlobalData _globals;
    IoGlobalData? IVfsFileProvider.GlobalData => _globals;
    public PackageFixtureProvider(IoGlobalData globals, VersionContainer versions) : base("Fortnite", versions, StringComparer.OrdinalIgnoreCase) => _globals = globals;
}
file sealed class MemoryMappings : UsmapTypeMappingsProvider
{
    public MemoryMappings(byte[] bytes) => Load(bytes);
    public override void Reload() => throw new NotSupportedException("Immutable fixture mappings");
}

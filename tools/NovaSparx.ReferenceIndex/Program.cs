using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.AssetRegistry.Objects;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Versions;
using Microsoft.Extensions.Logging.Abstractions;
using NovaSparx.Backend;

var outputDirectory =
    args.Length > 0
        ? Path.GetFullPath(args[0])
        : Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../web/reference-index"));

Directory.CreateDirectory(outputDirectory);

Environment.SetEnvironmentVariable(
    "NOVASPARX_CACHE_DIR",
    Path.Combine(
        Path.GetTempPath(),
        "novasparx-reference-index"));

using var http =
    new HttpClient
    {
        Timeout =
            TimeSpan.FromMinutes(15)
    };

var sources =
    new PublicFortniteSources(
        http,
        NullLogger<PublicFortniteSources>.Instance);

using var timeout =
    new CancellationTokenSource(
        TimeSpan.FromMinutes(25));

Console.WriteLine(
    "Downloading current Fortnite manifest metadata...");

var (
    manifest,
    version) =
    await sources.GetLiveManifestAsync(
        timeout.Token);

var versions =
    new VersionContainer(
        EGame.GAME_UE6_0);

using var provider =
    new NovaHybridFileProvider(
        new DirectoryInfo(
            sources.TocCache),
        versions)
    {
        LoadOnDemandTocs =
            true,

        ReadNaniteData =
            false,

        ReadShaderMaps =
            false,

        ReadScriptData =
            false,

        UseLazyPackageSerialization =
            true
    };

provider.OnDemandOptions =
    new IoStoreOnDemandOptions
    {
        ChunkHostUri =
            sources.GetOnDemandHostUri(),

        ChunkCacheDirectory =
            new DirectoryInfo(
                sources.ChunkCache),

        Timeout =
            TimeSpan.FromSeconds(
                120)
    };

Console.WriteLine(
    $"Registering live Fortnite archives for {version}...");

await provider.RegisterManifestAsync(
    manifest,
    "Fortnite",
    timeout.Token);

try
{
    var studio =
        await sources.GetStudioManifestAsync(
            timeout.Token);

    if (studio is not null)
    {
        var (
            studioManifest,
            studioVersion) =
            studio.Value;

        Console.WriteLine(
            $"Registering Fortnite_Studio archives for {studioVersion}...");

        await provider.RegisterManifestAsync(
            studioManifest,
            "Fortnite_Studio",
            timeout.Token);
    }
}
catch (Exception ex)
{
    Console.WriteLine(
        $"Fortnite_Studio manifest warning: {ex.Message}");
}

provider.Initialize();

try
{
    var keys =
        await sources.GetAesKeysAsync(
            timeout.Token);

    foreach (
        var pair in keys)
    {
        await provider.SubmitKeyAsync(
            pair.Key,
            pair.Value);
    }
}
catch (Exception ex)
{
    Console.WriteLine(
        $"AES key source warning: {ex.Message}");
}

Console.WriteLine(
    "Mounting Fortnite archives to locate AssetRegistry.bin...");

await provider.MountAsync();

try
{
    provider.LoadVirtualPaths();
}
catch (Exception ex)
{
    Console.WriteLine(
        $"Virtual path warning: {ex.Message}");
}

try
{
    provider.PostMount();
}
catch (Exception ex)
{
    Console.WriteLine(
        $"Post-mount warning: {ex.Message}");
}

var locationOutputDirectory =
    Path.Combine(
        Directory.GetParent(
            outputDirectory)
            ?.FullName
        ?? outputDirectory,
        "location-index");

var packageLocations =
    new SortedDictionary<
        string,
        string>(
            StringComparer.Ordinal);

var locationContainers =
    new HashSet<string>(
        StringComparer.OrdinalIgnoreCase);

foreach (
    var entry in provider.Files
        .Values
        .OfType<FIoStoreEntry>())
{
    if (
        !entry.IsPackageData ||
        !(
            entry.Path.EndsWith(
                ".uasset",
                StringComparison.OrdinalIgnoreCase) ||
            entry.Path.EndsWith(
                ".umap",
                StringComparison.OrdinalIgnoreCase)
        )
    )
    {
        continue;
    }

    var path =
        entry.Path
            .Replace('\\', '/')
            .TrimStart('/')
            .ToLowerInvariant();

    var tocPath =
        entry.IoStoreReader.Path
            .Replace('\\', '/')
            .TrimStart('/');

    if (
        string.IsNullOrWhiteSpace(path) ||
        string.IsNullOrWhiteSpace(tocPath))
    {
        continue;
    }

    packageLocations[path] =
        tocPath;

    locationContainers.Add(
        tocPath);
}

static byte LocationShard(
    string value)
{
    uint hash =
        2166136261;

    foreach (
        var item in Encoding.UTF8
            .GetBytes(
                value
                    .ToLowerInvariant()))
    {
        hash ^=
            item;

        hash =
            unchecked(
                hash *
                16777619);
    }

    return (byte)(
        hash &
        0xff);
}

static async Task<(
    int Entries,
    int Shards,
    long Bytes)>
WriteLocationIndexAsync(
    string root,
    IReadOnlyDictionary<
        string,
        string> source,
    string version,
    int containerCount,
    CancellationToken cancellationToken)
{
    var temporary =
        root +
        ".tmp";

    if (
        Directory.Exists(
            temporary))
    {
        Directory.Delete(
            temporary,
            recursive: true);
    }

    Directory.CreateDirectory(
        temporary);

    var buckets =
        new Dictionary<
            byte,
            SortedDictionary<
                string,
                string>>();

    foreach (
        var pair in source)
    {
        var shard =
            LocationShard(
                pair.Key);

        if (
            !buckets.TryGetValue(
                shard,
                out var values))
        {
            values =
                new SortedDictionary<
                    string,
                    string>(
                        StringComparer.Ordinal);

            buckets[
                shard] =
                values;
        }

        values[
            pair.Key] =
            pair.Value;
    }

    var options =
        new JsonSerializerOptions
        {
            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase,
            WriteIndented =
                false
        };

    long totalBytes = 0;

    foreach (
        var pair in buckets)
    {
        var path =
            Path.Combine(
                temporary,
                $"{pair.Key:x2}.json.gz");

        await using var file =
            new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);

        await using var gzip =
            new GZipStream(
                file,
                CompressionLevel.SmallestSize);

        var payload =
            new Dictionary<
                string,
                object?>
            {
                ["schema"] =
                    "novasparx.asset-locations.v1",

                ["valueProperty"] =
                    "toc",

                ["items"] =
                    pair.Value
            };

        await JsonSerializer
            .SerializeAsync(
                gzip,
                payload,
                options,
                cancellationToken);

        await gzip.FlushAsync(
            cancellationToken);

        totalBytes +=
            file.Length;
    }

    var manifest =
        new
        {
            schema =
                "novasparx.asset-locations.v1",

            builtAt =
                DateTimeOffset.UtcNow,

            fortniteVersion =
                version,

            hash =
                "fnv1a32-low-byte",

            entries =
                source.Count,

            containers =
                containerCount,

            shards =
                buckets.Count,

            bytes =
                totalBytes,

            path =
                "{shard}.json.gz"
        };

    await File.WriteAllTextAsync(
        Path.Combine(
            temporary,
            "manifest.json"),
        JsonSerializer.Serialize(
            manifest,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy =
                    JsonNamingPolicy.CamelCase,
                WriteIndented =
                    true
            }),
        cancellationToken);

    if (
        Directory.Exists(
            root))
    {
        Directory.Delete(
            root,
            recursive: true);
    }

    Directory.Move(
        temporary,
        root);

    return (
        source.Count,
        buckets.Count,
        totalBytes);
}

if (
    packageLocations.Count <
        1)
{
    throw new InvalidDataException(
        "Mounted Fortnite IoStore indexes exposed no package locations.");
}

var locationStats =
    await WriteLocationIndexAsync(
        locationOutputDirectory,
        packageLocations,
        version,
        locationContainers.Count,
        timeout.Token);

Console.WriteLine(
    $"ASSET_LOCATION_INDEX|packages={locationStats.Entries}|containers={locationContainers.Count}|shards={locationStats.Shards}|bytes={locationStats.Bytes}");

var registryEntry =
    provider.Files
        .Where(
            pair =>
                pair.Key
                    .Replace('\\', '/')
                    .EndsWith(
                        "AssetRegistry.bin",
                        StringComparison.OrdinalIgnoreCase))
        .OrderBy(
            pair =>
                string.Equals(
                    pair.Key
                        .Replace('\\', '/'),
                    "FortniteGame/AssetRegistry.bin",
                    StringComparison.OrdinalIgnoreCase)
                    ? 0
                    : 1)
        .ThenBy(
            pair =>
                pair.Key.Length)
        .Select(
            pair =>
                pair.Value)
        .FirstOrDefault();

if (registryEntry is null)
{
    Console.WriteLine(
        "Current live Fortnite delivery does not expose AssetRegistry.bin through the mounted provider. " +
        "Writing an unavailable manifest; runtime JSON verification remains the fallback.");

    if (Directory.Exists(outputDirectory))
    {
        Directory.Delete(
            outputDirectory,
            recursive: true);
    }

    Directory.CreateDirectory(
        outputDirectory);

    var unavailable =
        new
        {
            schema =
                "novasparx.asset-references.v1",

            builtAt =
                DateTimeOffset.UtcNow,

            fortniteVersion =
                version,

            available =
                false,

            reason =
                "AssetRegistry.bin is not exposed by the current live manifest/provider.",

            hash =
                "fnv1a32-low-byte",

            meshToBlueprints =
                new
                {
                    entries = 0,
                    bytes = 0L,
                    path =
                        "mesh/{shard}.json.gz"
                },

            blueprintToMeshes =
                new
                {
                    entries = 0,
                    bytes = 0L,
                    path =
                        "blueprint/{shard}.json.gz"
                }
        };

    await File.WriteAllTextAsync(
        Path.Combine(
            outputDirectory,
            "manifest.json"),
        JsonSerializer.Serialize(
            unavailable,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy =
                    JsonNamingPolicy.CamelCase,
                WriteIndented =
                    true
            }),
        timeout.Token);

    return;
}

Console.WriteLine(
    $"Parsing {registryEntry.Path}...");

using var archive =
    registryEntry.CreateReader();

var registry =
    new FAssetRegistryState(
        archive);

var classByPackage =
    registry
        .PreallocatedAssetDataBuffers
        .GroupBy(
            asset =>
                asset.PackageName.Text,
            StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            group =>
                group.Key,
            group =>
                group
                    .Select(
                        asset =>
                            asset.AssetClass.Text)
                    .Where(
                        value =>
                            !string.IsNullOrWhiteSpace(
                                value))
                    .ToHashSet(
                        StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

static string ClassLeaf(
    string value)
{
    value =
        (value ?? string.Empty)
            .Trim()
            .Trim('\'', '"');

    var separator =
        Math.Max(
            value.LastIndexOf('/'),
            value.LastIndexOf('.'));

    if (
        separator >= 0 &&
        separator + 1 <
            value.Length)
    {
        value =
            value[
                (separator + 1)..];
    }

    if (
        value.Length > 1 &&
        value[0] == 'U' &&
        char.IsUpper(value[1]))
    {
        value =
            value[1..];
    }

    return value;
}

static bool IsMeshClass(
    IReadOnlyCollection<string>? classes) =>
    classes is not null &&
    classes.Any(
        value =>
        {
            var type =
                ClassLeaf(value);

            return
                type.Equals(
                    "StaticMesh",
                    StringComparison.OrdinalIgnoreCase) ||
                type.Equals(
                    "SkeletalMesh",
                    StringComparison.OrdinalIgnoreCase);
        });

static bool IsBlueprintClass(
    IReadOnlyCollection<string>? classes) =>
    classes is not null &&
    classes.Any(
        value =>
        {
            var type =
                ClassLeaf(value);

            return type.Equals(
                       "Blueprint",
                       StringComparison.OrdinalIgnoreCase) ||
                   type.Equals(
                       "WidgetBlueprint",
                       StringComparison.OrdinalIgnoreCase) ||
                   type.Equals(
                       "AnimBlueprint",
                       StringComparison.OrdinalIgnoreCase) ||
                   type.Equals(
                       "ControlRigBlueprint",
                       StringComparison.OrdinalIgnoreCase) ||
                   type.Equals(
                       "BlueprintGeneratedClass",
                       StringComparison.OrdinalIgnoreCase) ||
                   type.Equals(
                       "WidgetBlueprintGeneratedClass",
                       StringComparison.OrdinalIgnoreCase) ||
                   type.Equals(
                       "AnimBlueprintGeneratedClass",
                       StringComparison.OrdinalIgnoreCase) ||
                   type.Equals(
                       "ControlRigBlueprintGeneratedClass",
                       StringComparison.OrdinalIgnoreCase);
        });

var nodes =
    registry
        .PreallocatedDependsNodeDataBuffers;

var meshToBlueprints =
    new Dictionary<
        string,
        SortedSet<string>>(
            StringComparer.OrdinalIgnoreCase);

var blueprintToMeshes =
    new Dictionary<
        string,
        SortedSet<string>>(
            StringComparer.OrdinalIgnoreCase);

for (
    var index = 0;
    index < nodes.Length;
    index++)
{
    var node =
        nodes[index];

    var meshPackage =
        node.Identifier
            ?.PackageName
            .Text;

    if (
        string.IsNullOrWhiteSpace(
            meshPackage) ||
        !classByPackage.TryGetValue(
            meshPackage,
            out var meshClasses) ||
        !IsMeshClass(
            meshClasses))
    {
        continue;
    }

    foreach (
        var referencerIndex
        in node.Referencers)
    {
        if (
            referencerIndex < 0 ||
            referencerIndex >=
                nodes.Length)
        {
            continue;
        }

        var blueprintPackage =
            nodes[
                referencerIndex]
                .Identifier
                ?.PackageName
                .Text;

        if (
            string.IsNullOrWhiteSpace(
                blueprintPackage) ||
            !classByPackage.TryGetValue(
                blueprintPackage,
                out var blueprintClasses) ||
            !IsBlueprintClass(
                blueprintClasses))
        {
            continue;
        }

        if (
            !meshToBlueprints.TryGetValue(
                meshPackage,
                out var blueprintSet))
        {
            blueprintSet =
                new SortedSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            meshToBlueprints[
                meshPackage] =
                blueprintSet;
        }

        blueprintSet.Add(
            blueprintPackage);

        if (
            !blueprintToMeshes.TryGetValue(
                blueprintPackage,
                out var meshSet))
        {
            meshSet =
                new SortedSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            blueprintToMeshes[
                blueprintPackage] =
                meshSet;
        }

        meshSet.Add(
            meshPackage);
    }
}

Console.WriteLine(
    $"Verified {meshToBlueprints.Count:N0} mesh packages with Blueprint referencers.");

var temporary =
    outputDirectory +
    ".tmp";

if (Directory.Exists(temporary))
    Directory.Delete(
        temporary,
        recursive: true);

Directory.CreateDirectory(
    temporary);

var meshShardDirectory =
    Path.Combine(
        temporary,
        "mesh");

var blueprintShardDirectory =
    Path.Combine(
        temporary,
        "blueprint");

Directory.CreateDirectory(
    meshShardDirectory);

Directory.CreateDirectory(
    blueprintShardDirectory);

var jsonOptions =
    new JsonSerializerOptions
    {
        PropertyNamingPolicy =
            JsonNamingPolicy.CamelCase,
        WriteIndented =
            false
    };

static byte FnvShard(
    string value)
{
    uint hash =
        2166136261;

    foreach (
        var item
        in Encoding.UTF8.GetBytes(
            value.ToLowerInvariant()))
    {
        hash ^=
            item;

        hash =
            unchecked(
                hash *
                16777619);
    }

    return (byte)(
        hash &
        0xff);
}

static async Task<(
    int Entries,
    long Bytes)>
WriteShardsAsync(
    string root,
    IReadOnlyDictionary<
        string,
        SortedSet<string>> source,
    string valueProperty,
    JsonSerializerOptions options,
    CancellationToken cancellationToken)
{
    var buckets =
        new Dictionary<
            byte,
            SortedDictionary<
                string,
                string[]>>();

    foreach (
        var pair in source)
    {
        var key =
            pair.Key
                .ToLowerInvariant();

        var shard =
            FnvShard(
                key);

        if (
            !buckets.TryGetValue(
                shard,
                out var values))
        {
            values =
                new SortedDictionary<
                    string,
                    string[]>(
                        StringComparer.Ordinal);

            buckets[
                shard] =
                values;
        }

        values[key] =
            pair.Value
                .ToArray();
    }

    long totalBytes = 0;

    foreach (
        var pair in buckets)
    {
        var path =
            Path.Combine(
                root,
                $"{pair.Key:x2}.json.gz");

        await using var file =
            new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);

        await using var gzip =
            new GZipStream(
                file,
                CompressionLevel.SmallestSize);

        var payload =
            new Dictionary<
                string,
                object?>
            {
                ["schema"] =
                    "novasparx.asset-references.v1",

                ["valueProperty"] =
                    valueProperty,

                ["items"] =
                    pair.Value
            };

        await JsonSerializer
            .SerializeAsync(
                gzip,
                payload,
                options,
                cancellationToken);

        await gzip.FlushAsync(
            cancellationToken);

        totalBytes +=
            file.Length;
    }

    return (
        source.Count,
        totalBytes);
}

var meshStats =
    await WriteShardsAsync(
        meshShardDirectory,
        meshToBlueprints,
        "blueprints",
        jsonOptions,
        timeout.Token);

var blueprintStats =
    await WriteShardsAsync(
        blueprintShardDirectory,
        blueprintToMeshes,
        "meshes",
        jsonOptions,
        timeout.Token);

var manifestOutput =
    new
    {
        schema =
            "novasparx.asset-references.v1",

        builtAt =
            DateTimeOffset.UtcNow,

        fortniteVersion =
            version,

        hash =
            "fnv1a32-low-byte",

        meshToBlueprints =
            new
            {
                entries =
                    meshStats.Entries,

                bytes =
                    meshStats.Bytes,

                path =
                    "mesh/{shard}.json.gz"
            },

        blueprintToMeshes =
            new
            {
                entries =
                    blueprintStats.Entries,

                bytes =
                    blueprintStats.Bytes,

                path =
                    "blueprint/{shard}.json.gz"
            }
    };

await File.WriteAllTextAsync(
    Path.Combine(
        temporary,
        "manifest.json"),
    JsonSerializer.Serialize(
        manifestOutput,
        new JsonSerializerOptions
        {
            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase,
            WriteIndented =
                true
        }),
    timeout.Token);

if (Directory.Exists(outputDirectory))
{
    Directory.Delete(
        outputDirectory,
        recursive: true);
}

Directory.Move(
    temporary,
    outputDirectory);

Console.WriteLine(
    $"Reference index written to {outputDirectory}.");

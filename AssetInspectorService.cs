using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;

namespace NovaSparx.Backend;

/// <summary>
/// Universal evidence inspector used by FNAA Description and References.
///
/// Dedicated material/mesh resolvers are used when available. Every other
/// UObject still gets compact real property metadata and explicit path
/// references from AssetReferenceScanner, so Blueprint/Niagara/etc. do not
/// fall back to name-only guessing merely because they are not visual meshes.
/// </summary>
public sealed class AssetInspectorService
{
    private readonly LiveProviderService _provider;
    private readonly MeshResolverService _meshes;
    private readonly ILogger<AssetInspectorService> _log;

    public AssetInspectorService(
        LiveProviderService provider,
        MeshResolverService meshes,
        ILogger<AssetInspectorService> log)
    {
        _provider = provider;
        _meshes = meshes;
        _log = log;
    }

    public async Task<AssetInspection?>
        InspectAsync(
            string rawPath,
            CancellationToken cancellationToken)
    {
        var canonical =
            AssetPathResolver.Canonicalize(
                rawPath);

        if (canonical.Length == 0)
            return null;

        var loaded =
            await _provider.LoadObjectAsync(
                rawPath,
                cancellationToken);

        if (loaded is null)
            return null;

        var value =
            loaded.Value.Object;

        var scan =
            AssetReferenceScanner.Scan(
                value);

        var facts =
            new Dictionary<string, object?>(
                scan.Facts,
                StringComparer.OrdinalIgnoreCase);

        var references =
            new List<AssetReference>();

        var referenceSet =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        void AddReferences(
            IEnumerable<AssetReference>? source)
        {
            if (source is null)
                return;

            foreach (var reference in source)
            {
                if (string.IsNullOrWhiteSpace(
                        reference.Path))
                {
                    continue;
                }

                // "self" is useful internally but is not a dependency.
                if (reference.Kind.Equals(
                        "self",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var key =
                    $"{reference.Kind}|{reference.Path}";

                if (referenceSet.Add(key))
                    references.Add(reference);
            }
        }

        AddReferences(
            scan.References);

        PreviewMaterial? material =
            null;

        PreviewMaterial[] materials =
            [];

        var materialFidelity =
            "unknown";

        var assetType =
            AssetTypeEvidence.Of(
                value);

        if (value is UUnrealMaterial unrealMaterial)
        {
            try
            {
                cancellationToken
                    .ThrowIfCancellationRequested();

                material =
                    MaterialResolver.Resolve(
                        unrealMaterial);

                cancellationToken
                    .ThrowIfCancellationRequested();

                materials =
                    [material];

                materialFidelity =
                    material.Fidelity;

                AddReferences(
                    MaterialResolver.CollectReferences(
                        unrealMaterial));

                facts["opacityMode"] =
                    material.OpacityMode;

                facts["twoSided"] =
                    material.TwoSided;

                facts["roughness"] =
                    material.Roughness;

                facts["metallic"] =
                    material.Metallic;

                facts["specular"] =
                    material.Specular;

                facts["materialEvidence"] =
                    material.Evidence;
            }
            catch (OperationCanceledException)
                when (
                    cancellationToken
                        .IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogDebug(
                    ex,
                    "Material inspection failed for {Path}.",
                    canonical);
            }
        }

        if (value is UStaticMesh or USkeletalMesh)
        {
            try
            {
                var envelope =
                    await _meshes.ResolveLoadedAsync(
                        value,
                        canonical,
                        loaded.Value.ResolvedPath,
                        cancellationToken);

                if (envelope is not null)
                {
                    materials =
                        envelope.Manifest.Materials;

                    materialFidelity =
                        envelope.Manifest.MaterialFidelity;

                    AddReferences(
                        envelope.Manifest.References);

                    facts["lod"] =
                        envelope.Manifest.Lod;

                    facts["nanite"] =
                        envelope.Manifest.IsNanite;

                    facts["vertices"] =
                        envelope.Manifest.Geometry
                            .Positions.Length / 3;

                    facts["triangles"] =
                        envelope.Manifest.Geometry
                            .Indices.Length / 3;

                    facts["sections"] =
                        envelope.Manifest.Sections.Length;

                    facts["renderablePreview"] =
                        true;

                    facts["previewSchema"] =
                        envelope.Schema;
                }
            }
            catch (OperationCanceledException)
                when (
                    cancellationToken
                        .IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Inspection should remain useful even if a huge or malformed
                // mesh cannot fit the browser preview budget.
                facts["renderablePreview"] =
                    false;

                facts["previewError"] =
                    "Mesh preview is unavailable for this asset.";

                _log.LogDebug(
                    ex,
                    "Mesh inspection preview pass failed for {Path}.",
                    canonical);
            }
        }

        if (value is USkeletalMesh skeletalMesh)
        {
            facts["bones"] =
                skeletalMesh
                    .ReferenceSkeleton
                    .FinalRefBoneInfo
                    .Length;

            facts["sourceLods"] =
                skeletalMesh
                    .LODModels
                    ?.Length ?? 0;

            facts["previewPose"] =
                "imported-reference-pose";
        }

        var metadataFamily = assetType switch
        {
            "NiagaraSystem" or "NiagaraEmitter" or "ParticleSystem" => "Niagara",
            "Blueprint" or "BlueprintGeneratedClass" or "WidgetBlueprint" or
                "WidgetBlueprintGeneratedClass" or "AnimBlueprint" or
                "AnimBlueprintGeneratedClass" => "Blueprint",
            "AnimSequence" or "AnimSequenceBase" or "AnimMontage" or "AnimComposite" or
                "AnimationAsset" or "BlendSpace" or "BlendSpace1D" or "PoseAsset" => "Animation",
            "SoundWave" or "SoundCue" or "SoundWaveProcedural" or
                "MetaSound" or "MetaSoundSource" or "MetaSoundPatch" => "Audio",
            _ => null
        };
        if (metadataFamily is not null)
            facts["metadataFamily"] = metadataFamily;
        if (metadataFamily == "Niagara")
            facts["visualPreviewPolicy"] = "metadata-only-until-deterministic-vfx-renderer";

        var health =
            _provider.Health();

        var source =
            health.TextureStreamingReady
                ? "novasparx-hybrid-live+texture-streaming"
                : "novasparx-hybrid-live";

        return new AssetInspection(
            State:
                "ready",

            Path:
                canonical,

            ResolvedPath:
                loaded.Value.ResolvedPath,

            AssetType:
                assetType,

            Source:
                source,

            MaterialFidelity:
                materialFidelity,

            Material:
                material,

            Materials:
                materials,

            References:
                references
                    .Take(240)
                    .ToArray(),

            Facts:
                facts);
    }

}

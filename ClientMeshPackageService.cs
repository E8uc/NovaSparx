using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace NovaSparx.Backend;

/// <summary>
/// Encodes CUE4Parse mesh output into a compact browser-first payload.
/// NovaSparx extracts the Fortnite asset; the visitor's own browser renders it.
/// </summary>
public sealed class ClientMeshPackageService
{
    public const string ContentType = "application/vnd.novasparx.mesh-v1";
    public const string Schema = "novasparx.client-mesh.v1";

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("NSMESH1\0");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly int MaxPackageBytes = int.TryParse(
        Environment.GetEnvironmentVariable("NOVASPARX_CLIENT_PACKAGE_MAX_BYTES"), out var bytes)
        ? Math.Clamp(bytes, 8 * 1024 * 1024, 60 * 1024 * 1024)
        : 48 * 1024 * 1024;

    private readonly MeshResolverService _meshes;

    public ClientMeshPackageService(MeshResolverService meshes) => _meshes = meshes;

    public async Task<ClientMeshPackageResult?> BuildAsync(
        string rawPath,
        CancellationToken cancellationToken)
    {
        var resolved = await _meshes.ResolveAsync(rawPath, cancellationToken);
        if (resolved is null) return null;

        var geometry = resolved.Manifest.Geometry;
        ValidateGeometry(geometry);

        var arrays = BuildArrayViews(geometry, out var payloadLength);
        var materials = resolved.Manifest.Materials.Select(SanitizeMaterial).ToArray();
        var texturePaths = materials
            .SelectMany(m => new[] {
                m.BaseColorTexture, m.NormalTexture, m.EmissiveTexture,
                m.OpacityTexture, m.PackedTexture
            })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => AssetPathResolver.Canonicalize(path!))
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var header = new
        {
            schema = Schema,
            version = "1.0.0",
            asset = new
            {
                path = resolved.Manifest.Path,
                resolvedPath = resolved.ResolvedPath,
                assetType = resolved.AssetType,
                source = resolved.Source,
                backendVersion = resolved.Version,
                manifestVersion = resolved.ManifestVersion,
                lod = resolved.Manifest.Lod,
                isNanite = resolved.Manifest.IsNanite,
                materialFidelity = resolved.Manifest.MaterialFidelity
            },
            bounds = ComputeBounds(geometry.Positions),
            arrays,
            sections = resolved.Manifest.Sections,
            materials,
            texturePaths,
            counts = new
            {
                vertices = geometry.Positions.Length / 3,
                triangles = geometry.Indices.Length / 3,
                sections = resolved.Manifest.Sections.Length,
                materials = resolved.Manifest.Materials.Length
            }
        };

        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
        var paddedHeaderLength = Align4(headerBytes.Length);
        var totalLength = checked(16 + paddedHeaderLength + payloadLength);

        if (totalLength > MaxPackageBytes)
        {
            throw new InvalidOperationException(
                $"The client mesh package is too large ({totalLength:N0} bytes). " +
                "A smaller Fortnite LOD is required for this asset.");
        }

        using var stream = new MemoryStream(totalLength);
        stream.Write(Magic);
        WriteInt32(stream, headerBytes.Length);
        WriteInt32(stream, paddedHeaderLength);
        stream.Write(headerBytes);
        if (paddedHeaderLength > headerBytes.Length)
            stream.Write(new byte[paddedHeaderLength - headerBytes.Length]);

        WriteArray(stream, geometry.Positions);
        WriteArray(stream, geometry.Normals);
        WriteArray(stream, geometry.Tangents);
        WriteArray(stream, geometry.Uv0);
        if (geometry.Colors is not null) WriteArray(stream, geometry.Colors);
        WriteArray(stream, geometry.Indices);

        if (stream.Length != totalLength)
            throw new InvalidOperationException("NovaSparx produced an inconsistent mesh package.");

        byte[] packageBytes;
        if (stream.TryGetBuffer(out var segment) &&
            segment.Array is not null && segment.Offset == 0 && segment.Count == segment.Array.Length)
            packageBytes = segment.Array;
        else
            packageBytes = stream.ToArray();

        return new ClientMeshPackageResult(
            resolved.Manifest.Path,
            resolved.ResolvedPath,
            resolved.AssetType,
            packageBytes,
            geometry.Positions.Length / 3,
            geometry.Indices.Length / 3);
    }

    private static Dictionary<string, BufferView> BuildArrayViews(
        PreviewGeometry geometry,
        out int payloadLength)
    {
        var result = new Dictionary<string, BufferView>(StringComparer.Ordinal);
        var offset = 0;

        void Add(string name, int scalarCount, int components, int scalarBytes, string type)
        {
            var byteLength = checked(scalarCount * scalarBytes);
            result[name] = new BufferView(
                offset, byteLength, scalarCount / components, components, type);
            offset = checked(offset + byteLength);
        }

        Add("positions", geometry.Positions.Length, 3, sizeof(float), "f32");
        Add("normals", geometry.Normals.Length, 3, sizeof(float), "f32");
        Add("tangents", geometry.Tangents.Length, 4, sizeof(float), "f32");
        Add("uv0", geometry.Uv0.Length, 2, sizeof(float), "f32");
        if (geometry.Colors is not null)
            Add("colors", geometry.Colors.Length, 4, sizeof(float), "f32");
        Add("indices", geometry.Indices.Length, 1, sizeof(uint), "u32");

        payloadLength = offset;
        return result;
    }

    private static void ValidateGeometry(PreviewGeometry geometry)
    {
        if (geometry.Positions.Length == 0 || geometry.Positions.Length % 3 != 0)
            throw new InvalidOperationException("Mesh positions are empty or malformed.");

        var vertices = geometry.Positions.Length / 3;
        if (geometry.Normals.Length != vertices * 3 ||
            geometry.Tangents.Length != vertices * 4 ||
            geometry.Uv0.Length != vertices * 2 ||
            (geometry.Colors is not null && geometry.Colors.Length != vertices * 4))
            throw new InvalidOperationException("Mesh vertex streams do not have matching lengths.");

        if (geometry.Indices.Length == 0 || geometry.Indices.Length % 3 != 0)
            throw new InvalidOperationException("Mesh indices are empty or malformed.");
    }

    private static ClientMaterialHeader SanitizeMaterial(PreviewMaterial material) => new(
        material.Name,
        material.Path,
        SafeVector(material.BaseColor, [1f, 1f, 1f, 1f], 4),
        Safe01(material.Roughness, 0.62f),
        Safe01(material.Metallic, 0f),
        material.TwoSided,
        SafeVector(material.EmissiveColor, [0f, 0f, 0f, 1f], 4),
        Safe01(material.Specular, 0.5f),
        Safe01(material.Opacity, 1f),
        string.IsNullOrWhiteSpace(material.OpacityMode) ? "opaque" : material.OpacityMode,
        Safe01(material.OpacityCutoff, 0.333f),
        material.UseVertexColor,
        SafeVector(material.UvScale, [1f, 1f], 2),
        SafeVector(material.UvOffset, [0f, 0f], 2),
        CleanPath(material.BaseColorTexture),
        CleanPath(material.NormalTexture),
        CleanPath(material.EmissiveTexture),
        CleanPath(material.OpacityTexture),
        CleanPath(material.PackedTexture),
        material.PackedChannels ?? new PackedChannels(),
        material.Fidelity,
        material.Evidence);

    private static string? CleanPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var path = AssetPathResolver.Canonicalize(value);
        return path.Length == 0 ? null : path;
    }

    private static float Safe01(float value, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : fallback;

    private static float[] SafeVector(float[]? value, float[] fallback, int length)
    {
        if (value is null || value.Length < length) return fallback.ToArray();
        var output = new float[length];
        for (var i = 0; i < length; i++)
            output[i] = float.IsFinite(value[i]) ? value[i] : fallback[i];
        return output;
    }

    private static ClientBounds ComputeBounds(float[] positions)
    {
        var min = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
        var max = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };

        for (var i = 0; i + 2 < positions.Length; i += 3)
        {
            var x = positions[i]; var y = positions[i + 1]; var z = positions[i + 2];
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) continue;
            min[0] = Math.Min(min[0], x); min[1] = Math.Min(min[1], y); min[2] = Math.Min(min[2], z);
            max[0] = Math.Max(max[0], x); max[1] = Math.Max(max[1], y); max[2] = Math.Max(max[2], z);
        }

        if (!float.IsFinite(min[0]))
            return new ClientBounds([0f, 0f, 0f], [0f, 0f, 0f], [0f, 0f, 0f], 1f);

        var center = new[] {
            (min[0] + max[0]) * 0.5f,
            (min[1] + max[1]) * 0.5f,
            (min[2] + max[2]) * 0.5f
        };
        var dx = max[0] - center[0]; var dy = max[1] - center[1]; var dz = max[2] - center[2];
        var radius = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (!float.IsFinite(radius) || radius <= 0.0001f) radius = 1f;
        return new ClientBounds(min, max, center, radius);
    }

    private static int Align4(int value) => checked((value + 3) & ~3);

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteArray(Stream stream, float[] values) =>
        stream.Write(MemoryMarshal.AsBytes(values.AsSpan()));

    private static void WriteArray(Stream stream, uint[] values) =>
        stream.Write(MemoryMarshal.AsBytes(values.AsSpan()));

    private sealed record BufferView(int ByteOffset, int ByteLength, int Count, int Components, string Type);
    private sealed record ClientBounds(float[] Min, float[] Max, float[] Center, float Radius);
    private sealed record ClientMaterialHeader(
        string Name, string? Path, float[] BaseColor, float Roughness, float Metallic, bool TwoSided,
        float[] EmissiveColor, float Specular, float Opacity, string OpacityMode, float OpacityCutoff,
        bool UseVertexColor, float[] UvScale, float[] UvOffset, string? BaseColorTexture,
        string? NormalTexture, string? EmissiveTexture, string? OpacityTexture, string? PackedTexture,
        PackedChannels PackedChannels, string Fidelity, string Evidence);
}

public sealed record ClientMeshPackageResult(
    string Path,
    string ResolvedPath,
    string AssetType,
    byte[] Bytes,
    int VertexCount,
    int TriangleCount);

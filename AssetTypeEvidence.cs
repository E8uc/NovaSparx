using CUE4Parse.UE4.Assets.Exports;

namespace NovaSparx.Backend;

/// <summary>Keep the Unreal export class, even for exports parsed as UObject.</summary>
public static class AssetTypeEvidence
{
    public static string Of(UObject value)
    {
        var type = value.ExportType;
        if (string.IsNullOrWhiteSpace(type))
            return "Unknown";

        // ExportType falls back to the CLR name only when Class is absent.
        return value.Class is null && type.Length > 1 &&
               type[0] == 'U' && char.IsUpper(type[1])
            ? type[1..]
            : type;
    }
}

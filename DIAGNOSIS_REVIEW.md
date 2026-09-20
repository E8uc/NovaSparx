# Diagnosis companion changes — 19 September 2026

Based only on main `3ed95eb752ceebb9482abe3d931fa42e9b71423d`.

- `AssetTypeEvidence.cs`: use actual Unreal `UObject.ExportType`; CLR fallback normalization applies only when Class is absent.
- `AssetInspectorService.cs` and `LiveProviderService.cs`: share that evidence instead of duplicate CLR type guesses. Metadata family matching is explicit rather than substring-based.
- `AssetReferenceScanner.cs`: preserve export type separately from runtime type.
- `AssetPathResolver.cs`: accept generic typed Unreal paths, including MetaSound and `/Script/Engine.Class` wrappers.
- `tools/NovaSparx.ReferenceIndex/Program.cs`: exact mesh and Blueprint class matches; a StaticMeshComponent or similarly named unrelated class is not a mesh asset.

No parser, preview, renderer, audio, UEFN, dependency-version or automatic database update is implemented here. See the companion Fortnite-agent DIAGNOSIS_REVIEW.md for cross-repository findings, corpus and validation.

Local JavaScript syntax, JSON, browser format, memory and transport/security self-tests pass. Local .NET compilation could not run because SDK download was blocked by network policy. Existing pull-request CI must compile both backend and reference-index tool before merge. No full live end-to-end certification is claimed.

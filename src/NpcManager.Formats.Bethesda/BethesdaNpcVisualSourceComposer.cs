using System.Collections.Immutable;
using System.Drawing;
using System.Security.Cryptography;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Reopens the exact reviewed Skyrim provider order and composes one
/// read-only NPC appearance graph. All preview materialization is new,
/// K-local output; no source plugin or asset is modified.
/// </summary>
public sealed partial class BethesdaNpcVisualSourceComposer(
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : INpcVisualSourceComposer
{
    private const int MaximumPlugins = 256;

    public async ValueTask<NpcVisualSourceComposeResult> ComposeSourceAsync(
        NpcVisualPreviewComposeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!ValidateRequest(request, diagnostics))
            return RefusedSource(diagnostics);

        PackageOverlay? overlay = await ReadPackageOverlayAsync(
            request.PackageOverlay, diagnostics, cancellationToken);
        if (HasErrors(diagnostics))
            return RefusedSource(diagnostics);

        ImmutableArray<PluginAuthority> plugins =
            await ReadPluginAuthoritiesAsync(
                request.Intake,
                overlay,
                diagnostics,
                cancellationToken);
        if (HasErrors(diagnostics) || plugins.IsDefaultOrEmpty)
            return RefusedSource(diagnostics);

        RecordGraph? graph = ReadRecordGraph(
            plugins, diagnostics, cancellationToken);
        if (graph is null || HasErrors(diagnostics))
            return RefusedSource(diagnostics);

        FormKey npcKey = ToFormKey(
            request.Identity.OwnerPlugin,
            request.Identity.FormId);
        if (!graph.Npcs.TryGetValue(
                npcKey, out ProviderRecord<Npc>? npcRecord) ||
            npcRecord.Record.IsDeleted)
        {
            diagnostics.Add(Error(
                "npc-preview-npc-missing",
                $"NPC {request.Identity.OwnerPlugin}|{request.Identity.FormId} is missing or deleted in the reviewed closure."));
            return RefusedSource(diagnostics);
        }
        if (npcRecord.Provider !=
            request.Identity.WinningProvider)
        {
            diagnostics.Add(Error(
                "npc-preview-npc-provider-mismatch",
                $"NPC winner is '{npcRecord.Provider}', not the selected '{request.Identity.WinningProvider}'."));
            return RefusedSource(diagnostics);
        }

        Npc npc = npcRecord.Record;
        if (npc.Race.FormKey.IsNull ||
            !graph.Races.TryGetValue(
                npc.Race.FormKey,
                out ProviderRecord<Race>? raceRecord) ||
            raceRecord.Record.IsDeleted)
        {
            diagnostics.Add(Error(
                "npc-preview-race-missing",
                "The selected NPC does not resolve to one winning live race."));
            return RefusedSource(diagnostics);
        }
        NpcVisualPreviewRoute route = DetectRoute(
            raceRecord.Provider, raceRecord.Record);
        NpcSex sex = npc.Configuration.Flags.HasFlag(
            NpcConfiguration.Flag.Female)
            ? NpcSex.Female
            : NpcSex.Male;
        string? hairColor = ResolveHairColor(
            npc, graph, diagnostics);
        string? skinTint = npc.TextureLighting is { } tint
            ? Hex(tint)
            : null;
        float skinTintAlpha =
            npc.TextureLighting is { } textureLighting
                ? textureLighting.A / 255f
                : 1f;

        string assetRootValue = Path.Combine(
            request.OutputRoot.Value, "assets", "Data");
        Directory.CreateDirectory(assetRootValue);
        var resolver = new PreviewAssetResolver(
            request.Intake.DataRoot,
            overlay,
            new WorkspacePath(assetRootValue),
            diagnostics,
            policy,
            labRoot);
        var assets = ImmutableArray.CreateBuilder<NpcVisualAsset>();
        var materialized = new Dictionary<string, NpcVisualAsset>(
            StringComparer.OrdinalIgnoreCase);

        string providerName =
            npcRecord.Provider.Value;
        string faceGenOwner =
            npc.FormKey.ModKey.FileName.String;
        string faceGeomPath =
            $"meshes/actors/character/FaceGenData/FaceGeom/{faceGenOwner}/{npc.FormKey.ID:X8}.nif";
        string faceTintPath =
            $"textures/actors/character/FaceGenData/FaceTint/{faceGenOwner}/{npc.FormKey.ID:X8}.dds";

        NpcVisualAsset? faceTintAsset =
            await MaterializeSimpleAsync(
                resolver,
                faceTintPath,
                NpcVisualAssetRole.FaceTint,
                materialized,
                diagnostics,
                required: true,
                cancellationToken);
        NpcVisualAsset? faceGeomAsset =
            await MaterializeNifAsync(
                resolver,
                faceGeomPath,
                NpcVisualAssetRole.FaceGeom,
                materialized,
                diagnostics,
                required: true,
                cancellationToken);
        if (faceTintAsset is null ||
            faceGeomAsset is null ||
            HasErrors(diagnostics))
            return RefusedSource(diagnostics);

        assets.Add(faceGeomAsset);
        assets.Add(faceTintAsset);

        FormKey? skinArmor = npc.WornArmor.FormKeyNullable ??
                             raceRecord.Record.Skin.FormKeyNullable;
        ImmutableHashSet<FormKey> compatibleArmorRaces =
            raceRecord.Record.ArmorRace.FormKeyNullable is
                { } armorRace &&
            !armorRace.IsNull
                ? ImmutableHashSet.Create(
                    raceRecord.Record.FormKey,
                    armorRace)
                : ImmutableHashSet.Create(
                    raceRecord.Record.FormKey);
        if (request.Options.RenderBody)
        {
            if (skinArmor is null)
            {
                diagnostics.Add(Error(
                    "npc-preview-body-skin-missing",
                    "The selected NPC and race do not resolve a body skin armor."));
            }
            else
            {
                await AddArmorMeshesAsync(
                    skinArmor.Value,
                    compatibleArmorRaces,
                    sex,
                    NpcVisualAssetRole.Body,
                    graph,
                    resolver,
                    materialized,
                    assets,
                    diagnostics,
                    cancellationToken);
            }
        }

        bool hasOutfit = npc.DefaultOutfit.FormKeyNullable is { };
        if (request.Options.RenderOutfit &&
            npc.DefaultOutfit.FormKeyNullable is { } outfitKey)
        {
            if (!graph.Outfits.TryGetValue(
                    outfitKey,
                    out ProviderRecord<Outfit>? outfit) ||
                outfit.Record.IsDeleted)
            {
                diagnostics.Add(Error(
                    "npc-preview-outfit-missing",
                    $"Default outfit {outfitKey} is unresolved."));
            }
            else
            {
                foreach (IFormLinkGetter<IOutfitTargetGetter> item in
                         outfit.Record.Items ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!graph.Armors.ContainsKey(item.FormKey))
                    {
                        diagnostics.Add(Warning(
                            "npc-preview-outfit-item-unsupported",
                            $"Outfit item {item.FormKey} is not a direct ARMO and cannot be composed deterministically."));
                        continue;
                    }
                    await AddArmorMeshesAsync(
                        item.FormKey,
                        compatibleArmorRaces,
                        sex,
                        NpcVisualAssetRole.Outfit,
                        graph,
                        resolver,
                        materialized,
                        assets,
                        diagnostics,
                        cancellationToken);
                }
            }
        }

        SuppressSkinAssetsHiddenByOutfit(
            assets,
            diagnostics);

        foreach (NpcVisualAsset texture in materialized.Values
                     .Where(item =>
                         item.Role == NpcVisualAssetRole.Texture)
                     .OrderBy(item => item.AssetPath.Value,
                         StringComparer.OrdinalIgnoreCase))
            assets.Add(texture);

        if (route == NpcVisualPreviewRoute.Cotr &&
            !faceGeomAsset.Materials.Any(material =>
                material.TextureSlots.Any(slot =>
                    slot.Slot == 0) &&
                material.TextureSlots.Any(slot =>
                    slot.Slot == 6 &&
                    string.Equals(
                        slot.AssetPath.Value,
                        NormalizeAssetPath(faceTintPath),
                        StringComparison.OrdinalIgnoreCase))))
        {
            diagnostics.Add(Error(
                "npc-preview-cotr-face-material-route",
                "COtR preview requires a qualified face diffuse in slot 0 and the canonical actor FaceTint in slot 6."));
        }
        if (HasErrors(diagnostics))
            return RefusedSource(diagnostics);

        ImmutableArray<NpcVisualMorph> morphs =
            await ReadBodyGenMorphsAsync(
                providerName,
                npc.FormKey.ID,
                resolver,
                diagnostics,
                cancellationToken);
        await AddBodyMorphTriAssetsAsync(
            morphs,
            resolver,
            materialized,
            assets,
            diagnostics,
            cancellationToken);
        if (HasErrors(diagnostics))
            return RefusedSource(diagnostics);
        string raceText =
            $"{raceRecord.Record.FormKey.ModKey}|0x{raceRecord.Record.FormKey.ID:X8}";
        var source = new NpcVisualSourceGraph(
            route,
            request.Identity,
            sex,
            npc.Weight,
            raceText,
            hairColor,
            skinTint,
            assets
                .DistinctBy(item =>
                    (item.Role, item.AssetPath.Value),
                    NpcVisualAssetKeyComparer.Instance)
                .ToImmutableArray(),
            morphs,
            hasOutfit,
            diagnostics.ToImmutable(),
            skinTintAlpha);
        return new NpcVisualSourceComposeResult(
            true, source, diagnostics.ToImmutable());
    }

    private static void SuppressSkinAssetsHiddenByOutfit(
        ImmutableArray<NpcVisualAsset>.Builder assets,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        uint occupiedSlots = assets
            .Where(item =>
                item.Role == NpcVisualAssetRole.Outfit)
            .Aggregate(
                0u,
                (mask, item) => mask | item.BipedSlotMask);
        if (occupiedSlots == 0)
            return;

        int removed = 0;
        for (int index = assets.Count - 1; index >= 0; index--)
        {
            NpcVisualAsset asset = assets[index];
            if (asset.Role is not (
                    NpcVisualAssetRole.Body or
                    NpcVisualAssetRole.Hands or
                    NpcVisualAssetRole.Feet) ||
                asset.BipedSlotMask == 0 ||
                (asset.BipedSlotMask & occupiedSlots) == 0)
                continue;
            assets.RemoveAt(index);
            removed++;
        }
        if (removed > 0)
            diagnostics.Add(new Diagnostic(
                "npc-preview-biped-skin-suppressed",
                DiagnosticSeverity.Info,
                $"Suppressed {removed} naked skin mesh asset(s) hidden by the declared outfit's biped slots 0x{occupiedSlots:X8}."));
    }

    private static RecordGraph? ReadRecordGraph(
        ImmutableArray<PluginAuthority> plugins,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var npcs = new Dictionary<FormKey, ProviderRecord<Npc>>();
        var races = new Dictionary<FormKey, ProviderRecord<Race>>();
        var outfits = new Dictionary<FormKey, ProviderRecord<Outfit>>();
        var armors = new Dictionary<FormKey, ProviderRecord<Armor>>();
        var addons = new Dictionary<FormKey, ProviderRecord<ArmorAddon>>();
        var colors = new Dictionary<FormKey, ProviderRecord<ColorRecord>>();
        var textures = new Dictionary<FormKey, ProviderRecord<TextureSet>>();
        try
        {
            foreach (PluginAuthority authority in plugins)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var mod = SkyrimMod.CreateFromBinaryOverlay(
                    new ModPath(
                        ModKey.FromNameAndExtension(
                            authority.Plugin.Value),
                        new FilePath(authority.Path.Value)),
                    SkyrimRelease.SkyrimSE);
                foreach (INpcGetter item in mod.Npcs)
                    npcs[item.FormKey] = new(
                        authority.Plugin, item.DeepCopy());
                foreach (IRaceGetter item in mod.Races)
                    races[item.FormKey] = new(
                        authority.Plugin, item.DeepCopy());
                foreach (IOutfitGetter item in mod.Outfits)
                    outfits[item.FormKey] = new(
                        authority.Plugin, item.DeepCopy());
                foreach (IArmorGetter item in mod.Armors)
                    armors[item.FormKey] = new(
                        authority.Plugin, item.DeepCopy());
                foreach (IArmorAddonGetter item in mod.ArmorAddons)
                    addons[item.FormKey] = new(
                        authority.Plugin, item.DeepCopy());
                foreach (IColorRecordGetter item in mod.Colors)
                    colors[item.FormKey] = new(
                        authority.Plugin, item.DeepCopy());
                foreach (ITextureSetGetter item in mod.TextureSets)
                    textures[item.FormKey] = new(
                        authority.Plugin, item.DeepCopy());
            }
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                ArgumentException)
        {
            diagnostics.Add(Error(
                "npc-preview-plugin-read-failed",
                exception.Message));
            return null;
        }
        return new(
            npcs, races, outfits, armors, addons, colors, textures);
    }

    private static NpcVisualPreviewRoute DetectRoute(
        PluginName raceProvider,
        Race race)
    {
        string identity =
            $"{race.FormKey.ModKey}|{raceProvider}|{race.EditorID}";
        if (identity.Contains(
                "UBE", StringComparison.OrdinalIgnoreCase))
            return NpcVisualPreviewRoute.Ube;
        if (identity.Contains(
                "COR_AllRace", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains(
                "DZN_", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains(
                "COtR", StringComparison.OrdinalIgnoreCase))
            return NpcVisualPreviewRoute.Cotr;
        return NpcVisualPreviewRoute.Cbbe3Ba;
    }

    private static string? ResolveHairColor(
        Npc npc,
        RecordGraph graph,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (npc.HairColor.FormKeyNullable is not { } key)
        {
            diagnostics.Add(Warning(
                "npc-preview-hair-color-missing",
                "The selected NPC has no HCLF hair-color reference."));
            return null;
        }
        if (!graph.Colors.TryGetValue(
                key, out ProviderRecord<ColorRecord>? record) ||
            record.Record.IsDeleted)
        {
            diagnostics.Add(Warning(
                "npc-preview-hair-color-unresolved",
                $"Hair color {key} is unresolved."));
            return null;
        }
        return Hex(record.Record.Color);
    }

    private bool ValidateRequest(
        NpcVisualPreviewComposeRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Intake.Edition !=
            GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error(
                "npc-preview-intake-edition",
                "A reviewed Skyrim SE/AE intake is required."));
        if (request.Intake.RuntimeAuthority)
            diagnostics.Add(Error(
                "npc-preview-intake-authority",
                "Copied intake cannot claim Skyrim runtime authority."));
        if (!request.Intake.DataRoot.IsUnder(labRoot) ||
            !Directory.Exists(request.Intake.DataRoot.Value))
            diagnostics.Add(Error(
                "npc-preview-data-root",
                "The reviewed copied Data root must be an existing K-local directory."));
        else
            diagnostics.AddRange(policy.EvaluateReadRoot(
                labRoot, request.Intake.DataRoot));
        if (!request.OutputRoot.IsUnder(labRoot) ||
            !Directory.Exists(request.OutputRoot.Value))
            diagnostics.Add(Error(
                "npc-preview-output-root",
                "The composer requires its new K-local output root to exist."));
        if (!string.Equals(
                request.Identity.Signature,
                "NPC_",
                StringComparison.Ordinal))
            diagnostics.Add(Error(
                "npc-preview-identity-signature",
                "Only an NPC_ identity can be composed."));
        if (request.Intake.Plugins.Length > MaximumPlugins)
            diagnostics.Add(Error(
                "npc-preview-plugin-count",
                $"At most {MaximumPlugins} reviewed plugins are accepted."));
        return !HasErrors(diagnostics);
    }

    private static FormKey ToFormKey(
        PluginName plugin,
        FormId formId) =>
        new(
            ModKey.FromNameAndExtension(plugin.Value),
            formId.Value);

    private static string Hex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string NormalizeAssetPath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static Diagnostic Warning(string code, string message) =>
        new(code, DiagnosticSeverity.Warning, message);

    private static NpcVisualSourceComposeResult RefusedSource(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private sealed record PluginAuthority(
        PluginName Plugin,
        WorkspacePath Path,
        Sha256Hash Sha256);

    private sealed record PackageOverlay(
        WorkspacePath Root,
        WorkspacePath DataRoot,
        ImmutableArray<PackageFile> Files);

    private sealed record PackageFile(
        string Kind,
        string RelativePath,
        long ByteLength,
        Sha256Hash Sha256,
        WorkspacePath FullPath);

    private sealed record ProviderRecord<T>(
        PluginName Provider,
        T Record);

    private sealed record RecordGraph(
        Dictionary<FormKey, ProviderRecord<Npc>> Npcs,
        Dictionary<FormKey, ProviderRecord<Race>> Races,
        Dictionary<FormKey, ProviderRecord<Outfit>> Outfits,
        Dictionary<FormKey, ProviderRecord<Armor>> Armors,
        Dictionary<FormKey, ProviderRecord<ArmorAddon>> ArmorAddons,
        Dictionary<FormKey, ProviderRecord<ColorRecord>> Colors,
        Dictionary<FormKey, ProviderRecord<TextureSet>> TextureSets);

    private sealed class NpcVisualAssetKeyComparer :
        IEqualityComparer<(NpcVisualAssetRole Role, string Path)>
    {
        public static NpcVisualAssetKeyComparer Instance { get; } =
            new();

        public bool Equals(
            (NpcVisualAssetRole Role, string Path) x,
            (NpcVisualAssetRole Role, string Path) y) =>
            x.Role == y.Role &&
            string.Equals(
                x.Path, y.Path,
                StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(
            (NpcVisualAssetRole Role, string Path) value) =>
            HashCode.Combine(
                value.Role,
                StringComparer.OrdinalIgnoreCase.GetHashCode(
                    value.Path));
    }
}

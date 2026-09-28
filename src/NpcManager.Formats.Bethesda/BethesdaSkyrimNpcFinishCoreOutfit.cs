using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

internal sealed record FinishCoreOutfitSelection(FormKey Race, ImmutableArray<FormKey> Items,
    ImmutableArray<Armor> Armors,
    ImmutableArray<ArmorAddon> ExcludedArmatures)
{
    internal ImmutableArray<Armor> ArmorsToClone => Armors.Where(armor =>
        armor.Armature.Any(link => ExcludedArmatures.Any(row => row.FormKey == link.FormKey)))
        .DistinctBy(row => row.FormKey).ToImmutableArray();
}

internal static class BethesdaSkyrimNpcFinishCoreOutfit
{
    internal const string ExcludedCode = "finish-core-outfit-armature-race-excluded";

    internal static FinishCoreOutfitSelection Read(ISkyrimModGetter source, FormKey race,
        SkyrimNpcFinishCoreRequest request, ImmutableArray<string> effectiveMasterOrder = default)
    {
        if (race.IsNull || race.ID is 0 or > 0xFFFFFF)
            throw new InvalidDataException("finish-core-outfit-reference: actor race requires a nonzero local 24-bit FormID.");
        var providers = new Dictionary<ModKey, SkyrimMod>();
        ISkyrimModGetter Provider(FormKey key)
        {
            if (key.IsNull || key.ID > 0xFFFFFF)
                throw new InvalidDataException("finish-core-outfit-reference: outfit closure requires non-null local FormIDs.");
            if (key.ModKey == source.ModKey) return source;
            if (providers.TryGetValue(key.ModKey, out var cached)) return cached;
            string? path;
            string? hash;
            long? length;
            if (key.ModKey == ModKey.FromNameAndExtension("Skyrim.esm") &&
                request.SandboxAuthority.CopiedMaster is { } master &&
                request.SandboxAuthority.CopiedMasterSha256 is { } masterHash)
            {
                path = master.Value;
                hash = masterHash.Value;
                length = null;
            }
            else
            {
                var bindings = request.Authorities.Providers.Where(row => string.Equals(row.Plugin?.Value, key.ModKey.ToString(), StringComparison.OrdinalIgnoreCase)).ToArray();
                var additional = request.Authorities.AdditionalMasters.Where(row => string.Equals(row.Plugin.Value, key.ModKey.ToString(), StringComparison.OrdinalIgnoreCase)).ToArray();
                if (bindings.Length > 1 || additional.Length > 1)
                    throw new InvalidDataException($"finish-core-outfit-provider: ambiguous copied provider for {key.ModKey}.");
                path = bindings.FirstOrDefault()?.Path?.Value ?? additional.FirstOrDefault()?.Path.Value;
                hash = bindings.FirstOrDefault()?.Sha256?.Value ?? additional.FirstOrDefault()?.Sha256.Value;
                length = bindings.FirstOrDefault()?.ByteLength ?? additional.FirstOrDefault()?.ByteLength;
            }
            if (path is null || hash is null)
                (path, hash, length) = PackageProvider(request, key.ModKey);
            if (path is null || hash is null || !File.Exists(path) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"finish-core-outfit-provider: {key.ModKey} requires an ordinary hash-bound copied provider.");
            using var bound = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] bytes = new byte[checked((int)bound.Length)];
            bound.ReadExactly(bytes);
            if ((length is { } size && size != bytes.LongLength) ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"finish-core-outfit-provider-hash: copied provider {key.ModKey} changed.");
            var mod = SkyrimMod.CreateFromBinary(new ModPath(key.ModKey, new FilePath(path)), SkyrimRelease.SkyrimSE);
            providers.Add(key.ModKey, mod);
            return mod;
        }

        string[] sourceMasters = source.ModHeader.MasterReferences.Select(row => row.Master.ToString()).ToArray();
        string[] masterOrder = effectiveMasterOrder.IsDefault ? sourceMasters : effectiveMasterOrder.ToArray();
        if (!masterOrder.Take(sourceMasters.Length).SequenceEqual(sourceMasters, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("finish-core-outfit-master-order: admitted master order differs from the retained source prefix.");
        ModKey[] copiedProviders = request.Authorities.Providers.Where(row => row.Plugin is not null)
            .Select(row => ModKey.FromNameAndExtension(row.Plugin!.Value.Value))
            .Concat(request.Authorities.AdditionalMasters.Select(row => ModKey.FromNameAndExtension(row.Plugin.Value)))
            .Where(key => effectiveMasterOrder.IsDefault || masterOrder.Contains(key.ToString(), StringComparer.OrdinalIgnoreCase))
            .Distinct().ToArray();
        T? ResolveOptional<T>(FormKey key, string signature, Func<ISkyrimModGetter, IEnumerable<T>> records)
            where T : class, ISkyrimMajorRecordGetter
        {
            T Exact(T[] rows) => rows is [var record] && !record.IsDeleted ? record :
                throw new InvalidDataException($"finish-core-outfit-record: {signature} {Reference(key)} must resolve exactly once and may not be deleted.");
            T[] local = records(source).Where(row => row.FormKey == key).Take(2).ToArray();
            if (local.Length != 0) return Exact(local);
            var candidates = new List<(T Record, int Order)>();
            foreach (ModKey provider in copiedProviders.Prepend(key.ModKey).Distinct())
            {
                if (provider == source.ModKey) continue;
                T[] rows = records(Provider(new FormKey(provider, key.ID))).Where(row => row.FormKey == key).Take(2).ToArray();
                if (rows.Length == 0) continue;
                if (rows.Length != 1) return Exact(rows);
                int order = Array.FindIndex(masterOrder, value => string.Equals(value, provider.ToString(), StringComparison.OrdinalIgnoreCase));
                candidates.Add((rows[0], order));
            }
            if (candidates.Count > 1 && candidates.Any(row => row.Order < 0))
                throw new InvalidDataException($"finish-core-outfit-override-ambiguous: {signature} {Reference(key)} has copied overrides without declared load precedence.");
            return candidates.Count == 0 ? null : Exact(candidates.OrderByDescending(row => row.Order).Take(1).Select(row => row.Record).ToArray());
        }
        T Resolve<T>(FormKey key, string signature, Func<ISkyrimModGetter, IEnumerable<T>> records)
            where T : class, ISkyrimMajorRecordGetter => ResolveOptional(key, signature, records) ??
                throw new InvalidDataException($"finish-core-outfit-record: {signature} {Reference(key)} must resolve exactly once and may not be deleted.");

        ImmutableArray<FormKey> items;
        if (request.OutfitPolicy.Policy == SkyrimNpcFinishCoreOutfitPolicy.ExistingOutfit)
        {
            FormKey? key = request.OutfitPolicy.ExistingOutfit is { } existing
                ? Key(existing)
                : SourceDefaultOutfit(source, request);
            items = key is null ? [] : (Resolve(key.Value, "OTFT", mod => mod.Outfits).Items ?? [])
                .Select(row => row.FormKey).ToImmutableArray();
        }
        else items = request.OutfitPolicy.ArmorItems.Select(Key).ToImmutableArray();
        var armors = ImmutableArray.CreateBuilder<Armor>();
        var excluded = ImmutableArray.CreateBuilder<ArmorAddon>();
        foreach (FormKey key in items)
        {
            var armor = ResolveOptional(key, "ARMO", mod => mod.Armors);
            if (armor is null)
            {
                _ = Resolve(key, "LVLI", mod => mod.LeveledItems);
                continue;
            }
            armors.Add((Armor)armor.DeepCopy());
            foreach (var link in armor.Armature)
            {
                var arma = Resolve(link.FormKey, "ARMA", mod => mod.ArmorAddons);
                if (arma.Race.FormKey != race && !arma.AdditionalRaces.Any(row => row.FormKey == race) &&
                    !excluded.Any(row => row.FormKey == arma.FormKey))
                    excluded.Add((ArmorAddon)arma.DeepCopy());
            }
        }
        return new(race, items, armors.ToImmutable(), excluded.ToImmutable());
    }

    internal static Diagnostic Exclusion(ArmorAddon armature, FormKey race) => new(ExcludedCode,
        DiagnosticSeverity.Error, $"ARMA {Reference(armature.FormKey)} excludes actor race {Reference(race)}. " +
        "Use npc finish analyze with request outfitRacePolicy=clone to review output-owned armor and armature clones.");

    internal static ImmutableArray<FormReference> Compose(SkyrimMod destination, ISkyrimModGetter source,
        FormKey race, SkyrimNpcFinishCoreProposal proposal)
    {
        var selection = Read(source, race, proposal.Request!, proposal.MasterOrder);
        var armaIds = Allocations(proposal, "ARMA");
        var armorIds = Allocations(proposal, "ARMO");
        if (selection.ExcludedArmatures.Length != armaIds.Length || selection.ArmorsToClone.Length != armorIds.Length)
            throw new InvalidDataException("finish-core-outfit-clone-plan: allocated clones differ from the admitted outfit closure.");
        var armatures = new Dictionary<FormKey, FormKey>();
        for (int i = 0; i < armaIds.Length; i++)
        {
            var template = selection.ExcludedArmatures[i];
            var clone = destination.ArmorAddons.DuplicateInAsNewRecord(template, new FormKey(destination.ModKey, armaIds[i]));
            clone.EditorID = $"{proposal.Request!.Actor.EditorId!.Value.Value}_Armature_{armaIds[i]:X8}";
            clone.AdditionalRaces.Add(race);
            armatures.Add(template.FormKey, clone.FormKey);
        }
        var armors = new Dictionary<FormKey, FormKey>();
        for (int i = 0; i < armorIds.Length; i++)
        {
            var template = selection.ArmorsToClone[i];
            var clone = destination.Armors.DuplicateInAsNewRecord(template, new FormKey(destination.ModKey, armorIds[i]));
            clone.EditorID = $"{proposal.Request!.Actor.EditorId!.Value.Value}_Armor_{armorIds[i]:X8}";
            for (int linkIndex = 0; linkIndex < clone.Armature.Count; linkIndex++)
                if (armatures.TryGetValue(clone.Armature[linkIndex].FormKey, out var replacement))
                    clone.Armature[linkIndex] = new FormLink<IArmorAddonGetter>(replacement);
            armors.Add(template.FormKey, clone.FormKey);
        }
        return selection.Items.Select(row => Reference(armors.GetValueOrDefault(row, row))).ToImmutableArray();
    }

    internal static ImmutableArray<FormReference> Verify(SkyrimMod output, ISkyrimModGetter source,
        FormKey race, SkyrimNpcFinishCoreProposal proposal, ImmutableArray<Diagnostic>.Builder diagnostics, byte[] outputBytes)
    {
        var expected = new SkyrimMod(output.ModKey, SkyrimRelease.SkyrimSE);
        foreach (var master in output.ModHeader.MasterReferences)
            expected.ModHeader.MasterReferences.Add(new MasterReference { Master = master.Master });
        var items = Compose(expected, source, race, proposal);
        // ArmorAddon.Equals can reject byte-identical records and even their own DeepCopy.
        // Compare every encoded clone field against the independently reopened raw output instead.
        using var stream = new MemoryStream();
        expected.WriteToBinary(stream, new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck, MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck, NextFormID = NextFormIDOption.NoCheck
        });
        var actual = BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(outputBytes).NonTes4Records;
        foreach (var record in BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(stream.ToArray()).NonTes4Records)
        {
            var matches = actual.Where(row => row.Signature == record.Signature && row.RawFormId == record.RawFormId).ToArray();
            if (matches.Length != 1 || !record.Bytes.AsSpan().SequenceEqual(matches[0].Bytes))
                diagnostics.Add(new Diagnostic(record.Signature == "ARMA" ? "finish-core-verify-outfit-armature" : "finish-core-verify-outfit-armor",
                    DiagnosticSeverity.Error, $"Output {record.Signature} {output.ModKey}|0x{record.RawFormId & 0xFFFFFF:X8} " +
                    $"differs from the copied record and reviewed race/link changes for {Reference(race)}."));
        }
        return items;
    }

    private static uint[] Allocations(SkyrimNpcFinishCoreProposal proposal, string signature) =>
        proposal.NewRecords.Where(row => row.StartsWith(signature + " ", StringComparison.Ordinal))
            .Select(row => FormId.TryParse(row[(signature.Length + 1)..], out var id) ? id.Value :
                throw new InvalidDataException("finish-core-outfit-clone-plan: invalid clone identity.")).ToArray();

    internal static FormKey Key(FormReference reference) => reference.FormId.Value is > 0 and <= 0xFFFFFF
        ? new(ModKey.FromNameAndExtension(reference.Plugin.Value), reference.FormId.Value)
        : throw new InvalidDataException($"finish-core-outfit-reference: {reference} requires a nonzero local 24-bit FormID.");
    internal static FormReference Reference(FormKey key) => new(new PluginName(key.ModKey.ToString()), new FormId(key.ID));

    private static FormKey? SourceDefaultOutfit(ISkyrimModGetter source, SkyrimNpcFinishCoreRequest request)
    {
        if (request.Actor.FormId is not { } formId) return null;
        INpcGetter[] actors = source.Npcs.Where(row => row.FormKey == new FormKey(source.ModKey, formId.Value)).Take(2).ToArray();
        if (actors.Length != 1)
            throw new InvalidDataException("finish-core-outfit-source-npc: source NPC must resolve exactly once before preserving its default outfit.");
        return actors[0].DefaultOutfit.FormKeyNullable;
    }

    private static (string? Path, string? Hash, long? Length) PackageProvider(
        SkyrimNpcFinishCoreRequest request, ModKey provider)
    {
        if (request.Source.PackageRoot is not { } root ||
            request.Source.PackageManifest is not { } manifest ||
            request.Source.PackageManifestSha256 is not { } manifestHash ||
            !manifest.IsUnder(root) || !File.Exists(manifest.Value) ||
            (File.GetAttributes(manifest.Value) & FileAttributes.ReparsePoint) != 0)
            return default;
        byte[] manifestBytes = File.ReadAllBytes(manifest.Value);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(manifestBytes)), manifestHash.Value,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("finish-core-outfit-provider-hash: source package manifest changed.");
        using JsonDocument document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions
        {
            AllowDuplicateProperties = false,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });
        if (!document.RootElement.TryGetProperty("artifacts", out JsonElement artifacts) ||
            artifacts.ValueKind != JsonValueKind.Array)
            return default;
        var matches = new List<(string Path, string Hash, long Length)>();
        foreach (JsonElement row in artifacts.EnumerateArray())
        {
            if (!row.TryGetProperty("relativePath", out JsonElement relativeNode) ||
                relativeNode.ValueKind != JsonValueKind.String ||
                !row.TryGetProperty("sha256", out JsonElement hashNode) ||
                hashNode.ValueKind != JsonValueKind.String ||
                !row.TryGetProperty("byteLength", out JsonElement lengthNode) ||
                !lengthNode.TryGetInt64(out long byteLength))
                continue;
            var relative = new AssetPath(relativeNode.GetString()!);
            if (!string.Equals(Path.GetFileName(relative.Value), provider.ToString(), StringComparison.OrdinalIgnoreCase))
                continue;
            string candidate = Path.GetFullPath(Path.Combine(root.Value,
                relative.Value.Replace('/', Path.DirectorySeparatorChar)));
            if (!new WorkspacePath(candidate).IsUnder(root))
                throw new InvalidDataException("finish-core-outfit-provider: source package provider escaped its package root.");
            matches.Add((candidate, hashNode.GetString()!, byteLength));
        }
        return matches switch
        {
            [] => default,
            [var match] => match,
            _ => throw new InvalidDataException($"finish-core-outfit-provider: ambiguous source package provider for {provider}.")
        };
    }
}

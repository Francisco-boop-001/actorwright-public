using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class ActorAssemblyPreflightDocumentLoader
{
    public async ValueTask<ActorAssemblyDocumentLoadResult<ActorAssemblyOwnerEvidence>> LoadOwnerEvidenceAsync(
        ActorAssemblyBoundFile file, CancellationToken cancellationToken) =>
        await LoadParsedAsync(file, ParseOwnerEvidence, cancellationToken);

    public async ValueTask<ActorAssemblyDocumentLoadResult<ActorAssemblyOutfitInventory>> LoadOutfitInventoryAsync(
        ActorAssemblyBoundFile file, CancellationToken cancellationToken) =>
        await LoadParsedAsync(file, ParseInventory, cancellationToken);

    public async ValueTask<ActorAssemblyDocumentLoadResult<ActorAssemblyOutfitBuildReceipt>> LoadReceiptAsync(
        ActorAssemblyBoundFile file, CancellationToken cancellationToken) =>
        await LoadParsedAsync(file, ParseReceipt, cancellationToken);

    public async ValueTask<ActorAssemblyDocumentLoadResult<ActorAssemblyRuntimeChannelReview>> LoadRuntimeReviewAsync(
        ActorAssemblyBoundFile file, CancellationToken cancellationToken) =>
        await LoadParsedAsync(file, ParseRuntimeReview, cancellationToken);

    public async ValueTask<ActorAssemblyDocumentLoadResult<ActorAssemblyReviewedCompositePolicy>> LoadCompositePolicyAsync(
        ActorAssemblyBoundFile file, CancellationToken cancellationToken) =>
        await LoadParsedAsync(file, ParseCompositePolicy, cancellationToken);

    private async ValueTask<ActorAssemblyDocumentLoadResult<T>> LoadParsedAsync<T>(
        ActorAssemblyBoundFile file, Func<JsonElement, T> parser, CancellationToken cancellationToken)
        where T : class
    {
        var raw = await ReadBoundJsonAsync(file.Path, file.Sha256, cancellationToken);
        if (raw.Disposition != ActorAssemblyDocumentDisposition.Loaded || raw.Document is null)
            return new(raw.Disposition, null, raw.ActualSha256, raw.SecurityRefusal, raw.Diagnostics);
        try
        {
            var document = parser(raw.Document.RootElement);
            return new(ActorAssemblyDocumentDisposition.Loaded, document, raw.ActualSha256, false, raw.Diagnostics);
        }
        catch (UnsafeDocumentException exception)
        {
            return new(ActorAssemblyDocumentDisposition.SecurityRefused, null, raw.ActualSha256, true,
                raw.Diagnostics.Add(new Diagnostic("actor-assembly-security-refused", DiagnosticSeverity.Error, exception.Message)));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
        {
            return new(ActorAssemblyDocumentDisposition.Invalid, null, raw.ActualSha256, false,
                raw.Diagnostics.Add(new Diagnostic("actor-assembly-evidence-invalid", DiagnosticSeverity.Error, exception.Message)));
        }
        finally { raw.Document.Dispose(); }
    }

    private ActorAssemblyOwnerEvidence ParseOwnerEvidence(JsonElement root)
    {
        RequireObject(root, "owner evidence");
        RequireProperty(root, "schemaVersion", "owner evidence");
        RequireProperty(root, "artifactKind", "owner evidence");
        RequireProperty(root, "edition", "owner evidence");
        RequireProperty(root, "baseNpc", "owner evidence");
        var schema = RequiredInt(root, "schemaVersion");
        if (schema != 1) throw new FormatException("Evidence schemaVersion must be 1.");
        if (!GameEditionExtensions.TryParseWireName(RequiredString(root, "edition"), out var edition) || edition != GameEdition.SkyrimSpecialEdition)
            throw new FormatException("Evidence edition must be skyrimse.");
        var baseNpc = ParseActorIdentity(root.GetProperty("baseNpc"));
        var kind = RequiredString(root, "artifactKind");
        return kind switch
        {
            "actor-assembly-obody-evidence" => ParseOBody(root, schema, edition, baseNpc),
            "actor-assembly-bodygen-evidence" => ParseBodyGen(root, schema, edition, baseNpc),
            "actor-assembly-runtime-script-evidence" => ParseRuntimeScript(root, schema, edition, baseNpc),
            "actor-assembly-composite-components" => ParseCompositeComponents(root, schema, edition, baseNpc),
            _ => throw new FormatException("Unknown Actor Assembly owner-evidence artifact kind.")
        };
    }

    private ActorAssemblyOBodyEvidence ParseOBody(JsonElement root, int schema, GameEdition edition, ActorAssemblyActorIdentity baseNpc)
    {
        RequireProperties(root, "schemaVersion", "artifactKind", "edition", "baseNpc", "preset", "assignments", "appliedOutputs");
        var assignments = ParseBoundFileArray(root.GetProperty("assignments"), "assignments", 1, 256);
        return new(schema, RequiredString(root, "artifactKind"), edition, baseNpc,
            ParseBoundFile(root.GetProperty("preset"), "preset"), assignments,
            ParseAppliedOutputs(root.GetProperty("appliedOutputs")));
    }

    private ActorAssemblyBodyGenEvidence ParseBodyGen(JsonElement root, int schema, GameEdition edition, ActorAssemblyActorIdentity baseNpc)
    {
        RequireProperties(root, "schemaVersion", "artifactKind", "edition", "baseNpc", "sources", "channels", "appliedOutputs");
        var sources = root.GetProperty("sources");
        RequireObject(sources, "sources");
        RequireProperties(sources, "assignment", "templates", "morphs");
        return new(schema, RequiredString(root, "artifactKind"), edition, baseNpc,
            ParseBoundFile(sources.GetProperty("assignment"), "sources.assignment"),
            ParseBoundFile(sources.GetProperty("templates"), "sources.templates"),
            ParseBoundFile(sources.GetProperty("morphs"), "sources.morphs"),
            ParseChannels(root.GetProperty("channels")), ParseAppliedOutputs(root.GetProperty("appliedOutputs")));
    }

    private ActorAssemblyRuntimeScriptEvidence ParseRuntimeScript(JsonElement root, int schema, GameEdition edition, ActorAssemblyActorIdentity baseNpc)
    {
        RequireProperties(root, "schemaVersion", "artifactKind", "edition", "baseNpc", "sources", "semanticReview", "channels", "appliedOutputs");
        var sources = root.GetProperty("sources");
        RequireObject(sources, "sources");
        RequireProperties(sources, "plugin", "pex", "vmad");
        return new(schema, RequiredString(root, "artifactKind"), edition, baseNpc,
            ParseBoundFile(sources.GetProperty("plugin"), "sources.plugin"),
            ParseBoundFile(sources.GetProperty("pex"), "sources.pex"),
            ParseBoundFile(sources.GetProperty("vmad"), "sources.vmad"),
            ParseBoundFile(root.GetProperty("semanticReview"), "semanticReview"),
            ParseChannels(root.GetProperty("channels")), ParseAppliedOutputs(root.GetProperty("appliedOutputs")));
    }

    private ActorAssemblyCompositeComponentsEvidence ParseCompositeComponents(JsonElement root, int schema, GameEdition edition, ActorAssemblyActorIdentity baseNpc)
    {
        RequireProperties(root, "schemaVersion", "artifactKind", "edition", "baseNpc", "components");
        var components = root.GetProperty("components");
        if (components.ValueKind != JsonValueKind.Array || components.GetArrayLength() is < 2 or > 4)
            throw new FormatException("Composite components must contain 2-4 rows.");
        var builder = ImmutableArray.CreateBuilder<ActorAssemblyCompositeComponent>();
        var owners = new HashSet<ActorAssemblyMorphOwner>();
        foreach (var item in components.EnumerateArray())
        {
            RequireObject(item, "components[]");
            RequireKnownProperties(item, "owner", "evidence");
            RequireProperty(item, "owner", "components[]");
            var owner = ParseOwner(RequiredString(item, "owner"));
            if (owner is ActorAssemblyMorphOwner.None or ActorAssemblyMorphOwner.Unknown or ActorAssemblyMorphOwner.ReviewedComposite || !owners.Add(owner))
                throw new FormatException("Composite component owners must be unique and nonrecursive.");
            var hasEvidence = item.TryGetProperty("evidence", out var evidence);
            if (owner == ActorAssemblyMorphOwner.BakedBodySlide && hasEvidence)
                throw new FormatException("bakedBodySlide composite components forbid evidence.");
            if (owner != ActorAssemblyMorphOwner.BakedBodySlide && !hasEvidence)
                throw new FormatException("Runtime composite components require available evidence.");
            builder.Add(new(owner, hasEvidence ? ParseBoundFile(evidence, "component.evidence") : null));
        }
        return new(schema, RequiredString(root, "artifactKind"), edition, baseNpc, builder.ToImmutable(), ImmutableArray<ActorAssemblyAppliedOutput>.Empty);
    }

    private ActorAssemblyOutfitInventory ParseInventory(JsonElement root)
    {
        RequireProperties(root, "schemaVersion", "artifactKind", "edition", "packageManifestSha256", "baseNpc", "entries");
        if (RequiredInt(root, "schemaVersion") != 1) throw new FormatException("Outfit inventory schemaVersion must be 1.");
        if (RequiredString(root, "artifactKind") != "actor-assembly-outfit-inventory") throw new FormatException("Wrong outfit inventory artifact kind.");
        var entriesElement = root.GetProperty("entries");
        if (entriesElement.ValueKind != JsonValueKind.Array || entriesElement.GetArrayLength() is < 1 or > 256)
            throw new FormatException("Outfit inventory entries must contain 1-256 rows.");
        var entries = ImmutableArray.CreateBuilder<ActorAssemblyOutfitEntry>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in entriesElement.EnumerateArray())
        {
            RequireObject(item, "entries[]");
            var classification = RequiredString(item, "classification");
            if (classification == "morphable")
            {
                RequireProperties(item, "outputId", "classification", "nifPackagePaths", "triPackagePath", "receipt");
                var id = RequiredString(item, "outputId");
                if (!ids.Add(id)) throw new FormatException("Outfit output IDs must be unique.");
                var nifs = ParseAssetPathArray(item.GetProperty("nifPackagePaths"), 1, 8);
                AssetPath? tri = item.TryGetProperty("triPackagePath", out var triValue) ? ParseAssetPath(triValue, "triPackagePath") : null;
                foreach (var path in nifs.Select(item => item.Value).Concat(tri is null ? Enumerable.Empty<string>() : new[] { tri.Value.Value }))
                    if (!packagePaths.Add(path)) throw new FormatException("Outfit package paths must be unique across inventory entries.");
                entries.Add(new ActorAssemblyMorphableOutfitEntry(id, nifs, tri, ParseEvidenceReference(item.GetProperty("receipt"), "receipt")));
            }
            else if (classification == "nonMorphable")
            {
                RequireProperties(item, "classification", "packagePath", "reason");
                var packagePath = ParseAssetPath(item.GetProperty("packagePath"), "packagePath");
                var id = packagePath.Value;
                if (!ids.Add(id)) throw new FormatException("Outfit output paths must be unique.");
                if (!packagePaths.Add(packagePath.Value)) throw new FormatException("Outfit package paths must be unique across inventory entries.");
                var reason = RequiredString(item, "reason");
                if (reason.Length > 512) throw new FormatException("Reasons may contain at most 512 characters.");
                entries.Add(new ActorAssemblyNonMorphableOutfitEntry(id, packagePath, reason));
            }
            else throw new FormatException("Unknown outfit classification.");
        }
        if (!GameEditionExtensions.TryParseWireName(RequiredString(root, "edition"), out var inventoryEdition) || inventoryEdition != GameEdition.SkyrimSpecialEdition)
            throw new FormatException("Outfit inventory edition must be skyrimse.");
        return new(1, RequiredString(root, "artifactKind"), inventoryEdition,
            ParseHash(RequiredString(root, "packageManifestSha256")), ParseActorIdentity(root.GetProperty("baseNpc")), entries.ToImmutable());
    }

    private ActorAssemblyOutfitBuildReceipt ParseReceipt(JsonElement root)
    {
        RequireProperties(root, "schemaVersion", "artifactKind", "edition", "tool", "sourcePreset", "outputs");
        if (RequiredInt(root, "schemaVersion") != 1) throw new FormatException("Receipt schemaVersion must be 1.");
        if (RequiredString(root, "artifactKind") != "actor-assembly-outfit-build-receipt") throw new FormatException("Wrong receipt artifact kind.");
        if (!GameEditionExtensions.TryParseWireName(RequiredString(root, "edition"), out var receiptEdition) || receiptEdition != GameEdition.SkyrimSpecialEdition) throw new FormatException("Receipt edition must be skyrimse.");
        var tool = root.GetProperty("tool");
        RequireObject(tool, "tool"); RequireProperties(tool, "name", "version", "executable");
        var source = root.GetProperty("sourcePreset");
        RequireObject(source, "sourcePreset"); RequireProperties(source, "path", "sha256", "presetName", "sliderSet");
        var outputs = root.GetProperty("outputs");
        if (outputs.ValueKind != JsonValueKind.Array || outputs.GetArrayLength() is < 1 or > 256) throw new FormatException("Receipt outputs must contain 1-256 rows.");
        var outputBuilder = ImmutableArray.CreateBuilder<ActorAssemblyOutfitReceiptOutput>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var output in outputs.EnumerateArray())
        {
            RequireObject(output, "outputs[]"); RequireProperties(output, "outputId", "nifs", "tri");
            var id = RequiredString(output, "outputId"); if (!ids.Add(id)) throw new FormatException("Receipt output IDs must be unique.");
            var nifs = ParseReceiptFiles(output.GetProperty("nifs"), 1, 8);
            var tri = output.TryGetProperty("tri", out var triElement) ? ParseReceiptFile(triElement, "tri") : null;
            foreach (var path in nifs.Select(item => item.PackagePath.Value).Concat(tri is null ? Enumerable.Empty<string>() : new[] { tri.PackagePath.Value }))
                if (!packagePaths.Add(path)) throw new FormatException("Receipt package paths must be unique across output groups.");
            outputBuilder.Add(new(id, nifs, tri));
        }
        return new(1, RequiredString(root, "artifactKind"), receiptEdition,
            new ActorAssemblyTool(RequiredString(tool, "name"), RequiredString(tool, "version"), ParseBoundFile(tool.GetProperty("executable"), "tool.executable")),
            new ActorAssemblyReceiptPreset(ParseInlineBoundFile(source, "sourcePreset"), RequiredString(source, "presetName"), RequiredString(source, "sliderSet")), outputBuilder.ToImmutable());
    }

    private ActorAssemblyRuntimeChannelReview ParseRuntimeReview(JsonElement root)
    {
        RequireProperties(root, "schemaVersion", "artifactKind", "decisionId", "decision", "baseNpc", "scriptSha256", "channels");
        if (RequiredInt(root, "schemaVersion") != 1) throw new FormatException("Runtime review schemaVersion must be 1.");
        if (RequiredString(root, "artifactKind") != "actor-assembly-runtime-script-channel-review" || RequiredString(root, "decision") != "bodyMorphChannels") throw new FormatException("Wrong runtime channel review kind or decision.");
        var channels = ParseStringArray(root.GetProperty("channels"), 1, 256, "channels");
        return new(1, RequiredString(root, "artifactKind"), RequiredString(root, "decisionId"), RequiredString(root, "decision"), ParseActorIdentity(root.GetProperty("baseNpc")), ParseHash(RequiredString(root, "scriptSha256")), channels);
    }

    private ActorAssemblyReviewedCompositePolicy ParseCompositePolicy(JsonElement root)
    {
        RequireProperties(root, "schemaVersion", "artifactKind", "edition", "decisionId", "decision", "baseNpc", "components", "outfitInventorySha256", "receiptSha256s", "outputs");
        if (RequiredInt(root, "schemaVersion") != 1) throw new FormatException("Composite policy schemaVersion must be 1.");
        if (RequiredString(root, "artifactKind") != "actor-assembly-reviewed-composite-policy" || RequiredString(root, "decision") != "allow") throw new FormatException("Wrong composite policy kind or decision.");
        var componentsElement = root.GetProperty("components");
        if (componentsElement.ValueKind != JsonValueKind.Array || componentsElement.GetArrayLength() is < 2 or > 4) throw new FormatException("Composite policy components must contain 2-4 rows.");
        var components = ImmutableArray.CreateBuilder<ActorAssemblyReviewedCompositePolicyComponent>(); var owners = new HashSet<ActorAssemblyMorphOwner>();
        foreach (var item in componentsElement.EnumerateArray())
        {
            RequireObject(item, "components[]"); RequireKnownProperties(item, "owner", "evidenceSha256"); RequireProperty(item, "owner", "components[]");
            var owner = ParseOwner(RequiredString(item, "owner"));
            if (owner is ActorAssemblyMorphOwner.None or ActorAssemblyMorphOwner.Unknown or ActorAssemblyMorphOwner.ReviewedComposite || !owners.Add(owner)) throw new FormatException("Composite policy owners must be unique and nonrecursive.");
            var hasEvidence = item.TryGetProperty("evidenceSha256", out var evidence);
            if (owner == ActorAssemblyMorphOwner.BakedBodySlide && hasEvidence) throw new FormatException("bakedBodySlide policy components forbid evidenceSha256.");
            if (owner != ActorAssemblyMorphOwner.BakedBodySlide && !hasEvidence) throw new FormatException("Runtime policy components require evidenceSha256.");
            components.Add(new(owner, hasEvidence ? ParseHash(RequiredValueString(evidence, "evidenceSha256")) : null));
        }
        var receiptHashes = root.GetProperty("receiptSha256s");
        var hashes = ParseHashArray(receiptHashes, 1, 256, "receiptSha256s");
        var outputs = root.GetProperty("outputs"); if (outputs.ValueKind != JsonValueKind.Array || outputs.GetArrayLength() is < 1 or > 256) throw new FormatException("Composite policy outputs must contain 1-256 rows.");
        var outputBuilder = ImmutableArray.CreateBuilder<ActorAssemblyReviewedCompositePolicyOutput>(); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in outputs.EnumerateArray()) { RequireObject(item, "outputs[]"); RequireProperties(item, "packagePath", "sha256"); var path = ParseAssetPath(item.GetProperty("packagePath"), "outputs.packagePath"); if (!paths.Add(path.Value)) throw new FormatException("Composite policy output paths must be unique."); outputBuilder.Add(new(path, ParseHash(RequiredString(item, "sha256")))); }
        if (!GameEditionExtensions.TryParseWireName(RequiredString(root, "edition"), out var policyEdition) || policyEdition != GameEdition.SkyrimSpecialEdition) throw new FormatException("Composite policy edition must be skyrimse.");
        return new(1, RequiredString(root, "artifactKind"), policyEdition, RequiredString(root, "decisionId"), RequiredString(root, "decision"), ParseActorIdentity(root.GetProperty("baseNpc")), components.ToImmutable(), ParseHash(RequiredString(root, "outfitInventorySha256")), hashes, outputBuilder.ToImmutable());
    }

    private ImmutableArray<ActorAssemblyBoundFile> ParseBoundFileArray(JsonElement element, string role, int min, int max)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() < min || element.GetArrayLength() > max) throw new FormatException($"{role} must contain {min}-{max} rows.");
        var builder = ImmutableArray.CreateBuilder<ActorAssemblyBoundFile>(); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in element.EnumerateArray()) { var file = ParseBoundFile(item, role + "[]"); if (!paths.Add(file.Path.Value)) throw new FormatException("Bound evidence paths must be unique."); builder.Add(file); }
        return builder.ToImmutable();
    }

    private static ImmutableArray<ActorAssemblyAppliedOutput> ParseAppliedOutputs(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() is < 1 or > 256) throw new FormatException("appliedOutputs must contain 1-256 rows.");
        var builder = ImmutableArray.CreateBuilder<ActorAssemblyAppliedOutput>(); var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in element.EnumerateArray()) { RequireObject(item, "appliedOutputs[]"); RequireProperties(item, "outputId", "appliedChannels"); var id = RequiredString(item, "outputId"); if (!ids.Add(id)) throw new FormatException("Applied output IDs must be unique."); builder.Add(new(id, ParseStringArray(item.GetProperty("appliedChannels"), 0, 256, "appliedChannels"))); }
        return builder.ToImmutable();
    }

    private static ImmutableArray<ActorAssemblyBodyGenChannel> ParseChannels(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() is < 1 or > 256) throw new FormatException("channels must contain 1-256 rows.");
        var builder = ImmutableArray.CreateBuilder<ActorAssemblyBodyGenChannel>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in element.EnumerateArray()) { RequireObject(item, "channels[]"); RequireProperties(item, "name", "value"); var name = RequiredString(item, "name"); if (!names.Add(name) || !item.GetProperty("value").TryGetSingle(out var value) || !float.IsFinite(value)) throw new FormatException("Channels must have unique finite values."); builder.Add(new(name, value)); }
        return builder.ToImmutable();
    }

    private static ImmutableArray<string> ParseStringArray(JsonElement element, int min, int max, string role)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() < min || element.GetArrayLength() > max) throw new FormatException($"{role} must contain {min}-{max} rows.");
        var builder = ImmutableArray.CreateBuilder<string>(); var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in element.EnumerateArray()) { var value = RequiredValueString(item, role + "[]"); if (!values.Add(value)) throw new FormatException($"{role} values must be unique."); builder.Add(value); }
        return builder.ToImmutable();
    }

    private static ImmutableArray<Sha256Hash> ParseHashArray(JsonElement element, int min, int max, string role)
    {
        var strings = ParseStringArray(element, min, max, role); return strings.Select(ParseHash).ToImmutableArray();
    }

    private static AssetPath ParseAssetPath(JsonElement element, string role)
    {
        var value = RequiredValueString(element, role);
        if (value.Contains('\\') || value.StartsWith('/') || value.Contains(':') || value.Split('/').Any(segment => segment is "" or "." or "..")) throw new FormatException($"{role} must be a forward-slash package-relative path.");
        return new AssetPath(value);
    }

    private static ImmutableArray<AssetPath> ParseAssetPathArray(JsonElement element, int min, int max)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() < min || element.GetArrayLength() > max) throw new FormatException("nifPackagePaths must contain 1-8 rows.");
        var paths = element.EnumerateArray().Select(item => ParseAssetPath(item, "nifPackagePaths[]")).ToImmutableArray(); if (paths.Select(path => path.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length) throw new FormatException("NIF package paths must be unique."); return paths;
    }

    private static ImmutableArray<ActorAssemblyOutfitReceiptFile> ParseReceiptFiles(JsonElement element, int min, int max)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() < min || element.GetArrayLength() > max) throw new FormatException("Receipt NIFs must contain 1-8 rows.");
        var builder = ImmutableArray.CreateBuilder<ActorAssemblyOutfitReceiptFile>(); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in element.EnumerateArray()) { var file = ParseReceiptFile(item, "nifs[]"); if (!paths.Add(file.PackagePath.Value)) throw new FormatException("Receipt paths must be unique."); builder.Add(file); } return builder.ToImmutable();
    }

    private static ActorAssemblyOutfitReceiptFile ParseReceiptFile(JsonElement element, string role)
    {
        RequireObject(element, role); RequireProperties(element, "packagePath", "sha256"); return new(ParseAssetPath(element.GetProperty("packagePath"), role + ".packagePath"), ParseHash(RequiredString(element, "sha256")));
    }

    private static void RequireKnownProperties(JsonElement element, params string[] allowed)
    {
        var names = allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
            if (!names.Contains(property.Name)) throw new FormatException($"Unknown property '{property.Name}'.");
    }

    private ActorAssemblyBoundFile ParseInlineBoundFile(JsonElement element, string role)
    {
        RequireProperty(element, "path", role);
        RequireProperty(element, "sha256", role);
        var rawPath = RequiredString(element, role + ".path");
        if (!TryValidateSafePath(rawPath, out var path, out var security)) throw new UnsafeDocumentException(security!.Message);
        return new(path, ParseHash(RequiredString(element, role + ".sha256")));
    }
}

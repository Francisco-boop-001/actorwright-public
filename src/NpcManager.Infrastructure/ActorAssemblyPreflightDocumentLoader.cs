using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class ActorAssemblyPreflightDocumentLoader : IActorAssemblyContractLoader, IActorAssemblyEvidenceLoader
{
    private const int MaxBytes = 1 * 1024 * 1024;
    private readonly WorkspacePath _labRoot;

    public ActorAssemblyPreflightDocumentLoader(WorkspacePath labRoot)
    {
        _labRoot = labRoot;
    }

    public async ValueTask<ActorAssemblyDocumentLoadResult<ActorAssemblyContract>> LoadAsync(
        ActorAssemblyPreflightRequest request, CancellationToken cancellationToken)
    {
        var raw = await ReadBoundJsonAsync(request.ContractPath, request.ContractSha256, cancellationToken);
        if (raw.Disposition != ActorAssemblyDocumentDisposition.Loaded || raw.Document is null)
            return new(raw.Disposition, null, raw.ActualSha256, raw.SecurityRefusal, raw.Diagnostics);

        try
        {
            return new(ActorAssemblyDocumentDisposition.Loaded,
                ParseContract(raw.Document.RootElement), raw.ActualSha256, false, raw.Diagnostics);
        }
        catch (UnsafeDocumentException exception)
        {
            return new(ActorAssemblyDocumentDisposition.SecurityRefused, null, raw.ActualSha256, true,
                raw.Diagnostics.Add(new Diagnostic("actor-assembly-security-refused", DiagnosticSeverity.Error,
                    exception.Message)));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
        {
            return new(ActorAssemblyDocumentDisposition.Invalid, null, raw.ActualSha256, false,
                raw.Diagnostics.Add(new Diagnostic("actor-assembly-contract-invalid", DiagnosticSeverity.Error,
                    exception.Message)));
        }
        finally
        {
            raw.Document.Dispose();
        }
    }

    private async ValueTask<BoundJsonReadResult> ReadBoundJsonAsync(
        WorkspacePath path, Sha256Hash expectedSha256, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryValidateSafePath(path.Value, out var safePath, out var securityDiagnostic))
            return new(ActorAssemblyDocumentDisposition.SecurityRefused, null, null, true,
                ImmutableArray.Create(securityDiagnostic!));
        if (!File.Exists(safePath.Value))
            return new(ActorAssemblyDocumentDisposition.Missing, null, null, false,
                ImmutableArray.Create(new Diagnostic("actor-assembly-file-missing", DiagnosticSeverity.Error,
                    $"The bound JSON file does not exist: '{safePath.Value}'.")));

        try
        {
            var info = new FileInfo(safePath.Value);
            if (info.Length == 0 || info.Length > MaxBytes)
                return new(ActorAssemblyDocumentDisposition.Invalid, null, null, false,
                    ImmutableArray.Create(new Diagnostic("actor-assembly-json-size", DiagnosticSeverity.Error,
                        $"Bound JSON must contain 1-{MaxBytes} bytes.")));
            var bytes = await File.ReadAllBytesAsync(safePath.Value, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes.Length == 0 || bytes.Length > MaxBytes)
                return new(ActorAssemblyDocumentDisposition.Invalid, null, null, false,
                    ImmutableArray.Create(new Diagnostic("actor-assembly-json-size", DiagnosticSeverity.Error,
                        $"Bound JSON must contain 1-{MaxBytes} bytes.")));
            var actual = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            if (!string.Equals(actual.Value, expectedSha256.Value, StringComparison.OrdinalIgnoreCase))
                return new(ActorAssemblyDocumentDisposition.HashMismatch, null, actual, false,
                    ImmutableArray.Create(new Diagnostic("actor-assembly-hash-mismatch", DiagnosticSeverity.Error,
                        $"Bound JSON hash '{actual.Value.ToUpperInvariant()}' does not match the caller binding.")));

            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(bytes, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
                ValidateTree(document.RootElement, 0);
                return new(ActorAssemblyDocumentDisposition.Loaded, document, actual, false,
                    ImmutableArray<Diagnostic>.Empty);
            }
            catch
            {
                document?.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException exception)
        {
            return new(ActorAssemblyDocumentDisposition.Invalid, null, null, false,
                ImmutableArray.Create(new Diagnostic("actor-assembly-json-invalid", DiagnosticSeverity.Error,
                    exception.Message)));
        }
        catch (IOException exception)
        {
            return new(ActorAssemblyDocumentDisposition.Unreadable, null, null, false,
                ImmutableArray.Create(new Diagnostic("actor-assembly-file-unreadable", DiagnosticSeverity.Error,
                    exception.Message)));
        }
        catch (UnauthorizedAccessException exception)
        {
            return new(ActorAssemblyDocumentDisposition.Unreadable, null, null, false,
                ImmutableArray.Create(new Diagnostic("actor-assembly-file-unreadable", DiagnosticSeverity.Error,
                    exception.Message)));
        }
        catch (FormatException exception)
        {
            return new(ActorAssemblyDocumentDisposition.Invalid, null, null, false,
                ImmutableArray.Create(new Diagnostic("actor-assembly-json-invalid", DiagnosticSeverity.Error,
                    exception.Message)));
        }
    }

    private static void ValidateTree(JsonElement element, int depth)
    {
        if (depth > 32) throw new FormatException("JSON nesting exceeds the depth-32 safety limit.");
        if (element.ValueKind == JsonValueKind.Null)
            throw new FormatException("JSON null is not admitted in Actor Assembly documents.");
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString() ?? string.Empty;
            if (value.Length is < 1 or > 1024)
                throw new FormatException("JSON strings must contain 1-1024 characters.");
            return;
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new FormatException($"Duplicate JSON property '{property.Name}'.");
                ValidateTree(property.Value, depth + 1);
            }
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            if (element.GetArrayLength() > 256)
                throw new FormatException("JSON arrays may contain at most 256 rows.");
            foreach (var item in element.EnumerateArray()) ValidateTree(item, depth + 1);
        }
    }

    private ActorAssemblyContract ParseContract(JsonElement root)
    {
        RequireObject(root, "contract");
        RequireProperties(root, "schemaVersion", "operation", "edition", "packageManifest", "baseNpc",
            "placement", "bodyMorph", "outfitScope", "reviewedCompositePolicy");
        var schema = RequiredInt(root, "schemaVersion");
        if (schema != 1) throw new FormatException("Actor Assembly schemaVersion must be 1.");
        var operation = RequiredString(root, "operation");
        if (operation != "npc-assembly-preflight") throw new FormatException("The operation is not admitted.");
        if (!GameEditionExtensions.TryParseWireName(RequiredString(root, "edition"), out var edition) ||
            edition != GameEdition.SkyrimSpecialEdition)
            throw new FormatException("Only the skyrimse edition is admitted.");
        var manifest = ParseBoundFile(root.GetProperty("packageManifest"), "packageManifest");
        var baseNpc = ParseActorIdentity(root.GetProperty("baseNpc"));
        var placement = ParsePlacement(root.GetProperty("placement"));
        var body = ParseBodyMorph(root.GetProperty("bodyMorph"));
        var outfit = ParseOutfitScope(root.GetProperty("outfitScope"));
        ActorAssemblyEvidenceReference? policy = null;
        if (root.TryGetProperty("reviewedCompositePolicy", out var policyElement))
            policy = ParseEvidenceReference(policyElement, "reviewedCompositePolicy");
        if (body.Owner == ActorAssemblyMorphOwner.ReviewedComposite &&
            (policy is null || policy.Status != ActorAssemblyEvidenceStatus.Available))
            throw new FormatException("reviewedComposite requires an available reviewedCompositePolicy.");
        return new(schema, operation, edition, manifest, baseNpc, placement, body, outfit, policy);
    }

    private ActorAssemblyBodyMorph ParseBodyMorph(JsonElement element)
    {
        RequireObject(element, "bodyMorph");
        RequireProperties(element, "owner", "evidence");
        var owner = ParseOwner(RequiredString(element, "owner"));
        var evidence = ParseEvidenceReference(element.GetProperty("evidence"), "bodyMorph.evidence");
        var availableOwner = owner is ActorAssemblyMorphOwner.OBody or ActorAssemblyMorphOwner.BodyGen or
            ActorAssemblyMorphOwner.RuntimeScript or ActorAssemblyMorphOwner.ReviewedComposite;
        if (availableOwner && evidence.Status != ActorAssemblyEvidenceStatus.Available)
            throw new FormatException("The selected morph owner requires available evidence.");
        if (owner is ActorAssemblyMorphOwner.None or ActorAssemblyMorphOwner.BakedBodySlide &&
            evidence.Status != ActorAssemblyEvidenceStatus.NotApplicable)
            throw new FormatException("none and bakedBodySlide require notApplicable evidence.");
        if (owner == ActorAssemblyMorphOwner.Unknown && evidence.Status != ActorAssemblyEvidenceStatus.Unavailable)
            throw new FormatException("unknown morph ownership requires unavailable evidence.");
        return new(owner, evidence);
    }

    private ActorAssemblyOutfitScope ParseOutfitScope(JsonElement element)
    {
        RequireObject(element, "outfitScope");
        RequireProperties(element, "status", "inventory", "reason");
        var status = ParseOutfitStatus(RequiredString(element, "status"));
        var hasInventory = element.TryGetProperty("inventory", out var inventoryElement);
        var hasReason = element.TryGetProperty("reason", out var reasonElement);
        if (status == ActorAssemblyOutfitScopeStatus.Complete)
        {
            if (!hasInventory || hasReason) throw new FormatException("complete outfitScope requires inventory and forbids reason.");
            return new(status, ParseBoundFile(inventoryElement, "outfitScope.inventory"), null);
        }
        if (!hasReason || hasInventory) throw new FormatException("unavailable/notApplicable outfitScope requires reason and forbids inventory.");
        var reason = RequiredValueString(reasonElement, "outfitScope.reason");
        if (reason.Length > 512) throw new FormatException("Reasons may contain at most 512 characters.");
        return new(status, null, reason);
    }

    private static ActorAssemblyPlacement ParsePlacement(JsonElement element)
    {
        RequireObject(element, "placement");
        RequireProperties(element, "mode", "placedReferenceFormId");
        var mode = ParsePlacementMode(RequiredString(element, "mode"));
        var hasPlaced = element.TryGetProperty("placedReferenceFormId", out var placedElement);
        if (mode == ActorAssemblyPlacementMode.PersistentReference && !hasPlaced)
            throw new FormatException("persistentReference requires placedReferenceFormId.");
        if (mode != ActorAssemblyPlacementMode.PersistentReference && hasPlaced)
            throw new FormatException("Nonpersistent placement forbids placedReferenceFormId.");
        return new(mode, hasPlaced ? ParseLocalFormId(RequiredValueString(placedElement, "placedReferenceFormId")) : null);
    }

    private static ActorAssemblyActorIdentity ParseActorIdentity(JsonElement element)
    {
        RequireObject(element, "baseNpc");
        RequireProperties(element, "plugin", "formId");
        return new(new PluginName(RequiredString(element, "plugin")),
            ParseLocalFormId(RequiredString(element, "formId")));
    }

    private ActorAssemblyBoundFile ParseBoundFile(JsonElement element, string role)
    {
        RequireObject(element, role);
        RequireProperties(element, "path", "sha256");
        RequireProperty(element, "path", role);
        RequireProperty(element, "sha256", role);
        var rawPath = RequiredString(element, role + ".path");
        if (!TryValidateSafePath(rawPath, out var path, out var security)) throw new UnsafeDocumentException(security!.Message);
        return new(path, ParseHash(RequiredString(element, role + ".sha256")));
    }

    private ActorAssemblyEvidenceReference ParseEvidenceReference(JsonElement element, string role)
    {
        RequireObject(element, role);
        RequireProperties(element, "status", "path", "sha256", "reason");
        var status = ParseEvidenceStatus(RequiredString(element, role + ".status"));
        var hasPath = element.TryGetProperty("path", out var pathElement);
        var hasHash = element.TryGetProperty("sha256", out var hashElement);
        var hasReason = element.TryGetProperty("reason", out var reasonElement);
        if (status == ActorAssemblyEvidenceStatus.Available)
        {
            if (!hasPath || !hasHash || hasReason) throw new FormatException("available evidenceRef requires path and sha256 and forbids reason.");
            var rawPath = RequiredString(element, role + ".path");
            if (!TryValidateSafePath(rawPath, out var path, out var security))
                throw new UnsafeDocumentException(security!.Message);
            return new(status, new ActorAssemblyBoundFile(path,
                ParseHash(RequiredString(element, role + ".sha256"))), null);
        }
        if (hasPath || hasHash || !hasReason) throw new FormatException("unavailable/notApplicable evidenceRef requires reason and forbids path/sha256.");
        var reason = RequiredValueString(reasonElement, role + ".reason");
        if (reason.Length > 512) throw new FormatException("Reasons may contain at most 512 characters.");
        return new(status, null, reason);
    }

    private static Sha256Hash ParseHash(string value)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new FormatException("SHA-256 must be exactly 64 hexadecimal characters.");
        return new(value);
    }

    private static FormId ParseLocalFormId(string value)
    {
        if (value.Length != 10 || !value.StartsWith("0x00", StringComparison.Ordinal) ||
            value.Skip(4).Any(character => !Uri.IsHexDigit(character)))
            throw new FormatException("FormIDs must use plugin-local 0x00XXXXXX notation.");
        var parsed = Convert.ToUInt32(value[2..], 16);
        if (parsed == 0 || parsed > 0x00FFFFFF) throw new FormatException("FormID is outside the local 24-bit range.");
        return new FormId(parsed);
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name.Split('.').Last(), out var value) || value.ValueKind != JsonValueKind.String)
            throw new FormatException($"Property '{name}' must be a string.");
        var text = value.GetString() ?? string.Empty;
        if (text.Length is < 1 or > 1024) throw new FormatException($"Property '{name}' has an invalid length.");
        return text;
    }

    private static string RequiredValueString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new FormatException($"Property '{name}' must be a string.");
        var text = value.GetString() ?? string.Empty;
        if (text.Length is < 1 or > 1024) throw new FormatException($"Property '{name}' has an invalid length.");
        return text;
    }

    private static int RequiredInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            throw new FormatException($"Property '{name}' must be an integer.");
        return result;
    }

    private static void RequireObject(JsonElement element, string role)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException($"'{role}' must be an object.");
    }

    private static void RequireProperty(JsonElement element, string name, string role)
    {
        if (!element.TryGetProperty(name, out _))
            throw new FormatException($"Missing property '{role}.{name}'.");
    }

    private static void RequireProperties(JsonElement element, params string[] allowed)
    {
        var set = allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
            if (!set.Contains(property.Name)) throw new FormatException($"Unknown property '{property.Name}'.");
        foreach (var name in allowed.Where(name => name is not "placedReferenceFormId" and not "reviewedCompositePolicy" and not "inventory" and not "reason" and not "path" and not "sha256"))
        {
            if (!element.TryGetProperty(name, out _)) throw new FormatException($"Missing property '{name}'.");
        }
    }

    private static ActorAssemblyMorphOwner ParseOwner(string value) => value switch
    {
        "none" => ActorAssemblyMorphOwner.None,
        "obody" => ActorAssemblyMorphOwner.OBody,
        "bodygen" => ActorAssemblyMorphOwner.BodyGen,
        "bakedBodySlide" => ActorAssemblyMorphOwner.BakedBodySlide,
        "runtimeScript" => ActorAssemblyMorphOwner.RuntimeScript,
        "reviewedComposite" => ActorAssemblyMorphOwner.ReviewedComposite,
        "unknown" => ActorAssemblyMorphOwner.Unknown,
        _ => throw new FormatException("Unknown morph owner.")
    };
    private static ActorAssemblyPlacementMode ParsePlacementMode(string value) => value switch
    {
        "persistentReference" => ActorAssemblyPlacementMode.PersistentReference,
        "questAlias" => ActorAssemblyPlacementMode.QuestAlias,
        "dynamicSpawn" => ActorAssemblyPlacementMode.DynamicSpawn,
        "none" => ActorAssemblyPlacementMode.None,
        _ => throw new FormatException("Unknown placement mode.")
    };
    private static ActorAssemblyOutfitScopeStatus ParseOutfitStatus(string value) => value switch
    {
        "complete" => ActorAssemblyOutfitScopeStatus.Complete,
        "unavailable" => ActorAssemblyOutfitScopeStatus.Unavailable,
        "notApplicable" => ActorAssemblyOutfitScopeStatus.NotApplicable,
        _ => throw new FormatException("Unknown outfit scope status.")
    };
    private static ActorAssemblyEvidenceStatus ParseEvidenceStatus(string value) => value switch
    {
        "available" => ActorAssemblyEvidenceStatus.Available,
        "unavailable" => ActorAssemblyEvidenceStatus.Unavailable,
        "notApplicable" => ActorAssemblyEvidenceStatus.NotApplicable,
        _ => throw new FormatException("Unknown evidence status.")
    };

    private bool TryValidateSafePath(string raw, out WorkspacePath path, out Diagnostic? diagnostic)
    {
        path = default;
        diagnostic = null;
        if (!Path.IsPathFullyQualified(raw) || raw.Contains('/') || raw.StartsWith("\\\\", StringComparison.Ordinal) ||
            raw.StartsWith("\\?\\", StringComparison.Ordinal) || raw.StartsWith("\\.\\", StringComparison.Ordinal) ||
            raw.IndexOf(':', 2) >= 0)
        {
            diagnostic = new Diagnostic("actor-assembly-unsafe-path", DiagnosticSeverity.Error,
                "Actor Assembly bound paths must be absolute ordinary K-local paths.");
            return false;
        }
        var segments = raw.Split('\\');
        if (segments.Any(segment => segment is "" or "." or ".."))
        {
            diagnostic = new Diagnostic("actor-assembly-unsafe-path", DiagnosticSeverity.Error,
                "Actor Assembly bound paths may not contain empty, dot, or parent segments.");
            return false;
        }
        path = new WorkspacePath(raw);
        if (!path.IsUnder(_labRoot))
        {
            diagnostic = new Diagnostic("actor-assembly-outside-lab", DiagnosticSeverity.Error,
                "Actor Assembly bound paths must remain under the K-only lab root.");
            return false;
        }
        var current = path.Value;
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostic = new Diagnostic("actor-assembly-reparse-refused", DiagnosticSeverity.Error,
                        "Actor Assembly bound paths may not traverse reparse points.");
                    return false;
                }
            }
            catch (IOException exception)
            {
                diagnostic = new Diagnostic("actor-assembly-path-inspection-failed", DiagnosticSeverity.Error, exception.Message);
                return false;
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostic = new Diagnostic("actor-assembly-path-inspection-failed", DiagnosticSeverity.Error, exception.Message);
                return false;
            }
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
        return true;
    }

    private sealed class UnsafeDocumentException(string message) : Exception(message);
    private sealed record BoundJsonReadResult(
        ActorAssemblyDocumentDisposition Disposition,
        JsonDocument? Document,
        Sha256Hash? ActualSha256,
        bool SecurityRefusal,
        ImmutableArray<Diagnostic> Diagnostics);
}

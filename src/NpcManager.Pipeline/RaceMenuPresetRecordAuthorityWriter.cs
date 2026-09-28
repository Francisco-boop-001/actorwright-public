using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

/// <summary>Writes one deterministic schema-2 authority into an owned K-local candidate root.</summary>
public sealed class RaceMenuPresetRecordAuthorityWriter(
    IWorkspacePolicy workspacePolicy,
    WorkspacePath labRoot)
    : IRaceMenuPresetRecordAuthorityWriter
{
    private const int MaximumManifestBytes = 1 * 1024 * 1024;

    public async ValueTask<RaceMenuPresetRecordAuthorityWriteResult> WriteAsync(
        RaceMenuPresetRecordAuthorityWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateDestination(request.Destination, diagnostics);
        ValidateDraft(request.Draft, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        byte[] bytes;
        try
        {
            bytes = Serialize(request.Draft);
            if (bytes.Length <= 0 || bytes.Length > MaximumManifestBytes)
            {
                diagnostics.Add(Error("racemenu-record-authority-size",
                    $"Generated record authority must be 1-{MaximumManifestBytes} bytes."));
                return Refused(diagnostics);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or
                                           ArgumentException or InvalidOperationException)
        {
            diagnostics.Add(Error("racemenu-record-authority-serialize", exception.Message));
            return Refused(diagnostics);
        }

        try
        {
            await using (var stream = new FileStream(
                request.Destination.Value,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var hash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            byte[] reopened = await File.ReadAllBytesAsync(
                request.Destination.Value, cancellationToken).ConfigureAwait(false);
            if (!reopened.AsSpan().SequenceEqual(bytes) ||
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(reopened))) != hash)
            {
                diagnostics.Add(Error("racemenu-record-authority-readback",
                    "Written record authority did not reopen byte-for-byte."));
                return RefusedWithCleanup(request.Destination, diagnostics);
            }
            using JsonDocument document = JsonDocument.Parse(reopened, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 2 ||
                !string.Equals(document.RootElement.GetProperty("authorityId").GetString(),
                    request.Draft.AuthorityId, StringComparison.Ordinal))
            {
                diagnostics.Add(Error("racemenu-record-authority-readback",
                    "Written record authority lost its schema or identity."));
                return RefusedWithCleanup(request.Destination, diagnostics);
            }

            var authority = new RaceMenuNpcRecordAuthority(request.Destination, hash);
            return new RaceMenuPresetRecordAuthorityWriteResult(
                true,
                new RaceMenuPresetRecordAuthorityArtifact(
                    request.Destination, hash, authority),
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            TryDeleteOwnedOutput(request.Destination);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           JsonException or ArgumentException or NotSupportedException)
        {
            TryDeleteOwnedOutput(request.Destination);
            diagnostics.Add(Error("racemenu-record-authority-write", exception.Message));
            return Refused(diagnostics);
        }
    }

    private byte[] Serialize(RaceMenuPresetRecordAuthorityDraft draft)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            SkipValidation = false
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 2);
            writer.WriteString("authorityId", draft.AuthorityId);
            writer.WriteString("edition", "skyrimse");
            writer.WritePropertyName("race");
            WriteBinding(writer, draft.RaceBinding);
            writer.WriteString("sex", draft.Target.Sex == NpcSex.Female ? "female" : "male");
            writer.WriteStartArray("formBindings");
            foreach (RaceMenuPresetHeadPartAuthority headPart in draft.HeadParts)
                WriteBinding(writer, headPart.Binding);
            WriteBinding(writer, draft.HeadTextureBinding);
            writer.WriteEndArray();

            writer.WriteStartArray("headPartDispositions");
            foreach (RaceMenuPresetHeadPartAuthority headPart in draft.HeadParts)
            {
                writer.WriteStartObject();
                writer.WriteString("sourceFormKey", SourceReference(headPart.Source.Identifier).ToString());
                writer.WriteString("disposition", "mapped-record");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartObject("hairColorAuthority");
            writer.WriteString("kind", "output-owned-clfm");
            writer.WriteNumber("packedRgb", draft.HairColorPackedRgb);
            writer.WriteString("allocatedLocalFormId", draft.OutputOwnedHairColorFormId.ToString());
            writer.WriteEndObject();

            writer.WriteStartArray("tintMappings");
            foreach (RaceMenuPresetTintAuthorityDisposition disposition in
                     draft.TintPlan.Dispositions)
            {
                writer.WriteStartObject();
                writer.WriteNumber("jslotIndex", disposition.Source.Index);
                switch (disposition.Kind)
                {
                    case RaceMenuPresetTintAuthorityDispositionKind.MappedRecord:
                        writer.WriteString("disposition", "mapped-record");
                        writer.WriteNumber("tiniIndex", disposition.TiniIndex ??
                            throw new InvalidDataException("Mapped tint lost its TINI index."));
                        writer.WriteNumber("tiasPresetIndex", disposition.TiasPresetIndex ??
                            throw new InvalidDataException("Mapped tint lost its TIAS preset index."));
                        writer.WriteBoolean("skinTint", disposition.IsSkinTint);
                        break;
                    case RaceMenuPresetTintAuthorityDispositionKind.Baked:
                        writer.WriteString("disposition", "baked");
                        break;
                    case RaceMenuPresetTintAuthorityDispositionKind.Inactive:
                        writer.WriteString("disposition", "inactive");
                        break;
                    default:
                        throw new InvalidDataException("Tint disposition is unsupported.");
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartObject("qnam");
            writer.WriteString("source", "mapped-skin-tint");
            writer.WriteNumber("jslotIndex", draft.TintPlan.Qnam.SourceJslotTintIndex);
            writer.WriteNumber("red", draft.TintPlan.Qnam.Red);
            writer.WriteNumber("green", draft.TintPlan.Qnam.Green);
            writer.WriteNumber("blue", draft.TintPlan.Qnam.Blue);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private void WriteBinding(Utf8JsonWriter writer, RaceMenuNpcFormBinding binding)
    {
        writer.WriteStartObject();
        writer.WriteString("signature", binding.Signature.Value);
        writer.WriteString("sourceFormKey", binding.SourceReference.ToString());
        writer.WriteString("providerFormKey", binding.Reference.ToString());
        writer.WriteString("providerPluginName", binding.ProviderPluginName.Value);
        writer.WriteString("providerPluginPath", Relative(binding.ProviderPlugin));
        writer.WriteString("providerPluginSha256", binding.ProviderPluginSha256.Value);
        if (binding.Signature == new RecordSignature("HDPT"))
        {
            writer.WriteString("headPartType", (binding.HeadPartType ??
                throw new InvalidDataException("HDPT binding lost its typed head-part kind."))
                .ToWireName());
        }
        writer.WriteEndObject();
    }

    private string Relative(WorkspacePath path)
    {
        if (!path.IsUnder(labRoot) || path == labRoot)
            throw new InvalidDataException("Record-authority provider escaped the K-local lab root.");
        return new AssetPath(Path.GetRelativePath(labRoot.Value, path.Value)
            .Replace(Path.DirectorySeparatorChar, '/')).Value;
    }

    private void ValidateDestination(
        WorkspacePath destination,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        diagnostics.AddRange(workspacePolicy.Evaluate(labRoot, destination));
        if (!destination.IsUnder(labRoot) || destination == labRoot ||
            !string.Equals(Path.GetExtension(destination.Value), ".json",
                StringComparison.OrdinalIgnoreCase) || File.Exists(destination.Value) ||
            Directory.Exists(destination.Value))
        {
            diagnostics.Add(Error("racemenu-record-authority-destination",
                "Destination must be one absent .json file beneath the K-local lab root."));
            return;
        }
        string? parent = Path.GetDirectoryName(destination.Value);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent) ||
            TraversesReparsePoint(parent))
        {
            diagnostics.Add(Error("racemenu-record-authority-parent",
                "Destination parent must already exist as an ordinary directory."));
        }
    }

    private bool TraversesReparsePoint(string path)
    {
        string current = Path.GetFullPath(path);
        while (!string.Equals(current, labRoot.Value, StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(current) ||
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                return true;
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                return true;
            current = parent;
        }
        return false;
    }

    private static void ValidateDraft(
        RaceMenuPresetRecordAuthorityDraft draft,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (draft is null || string.IsNullOrWhiteSpace(draft.AuthorityId) ||
            draft.AuthorityId.Length > 128 || draft.RuntimeAuthority ||
            draft.HeadParts.IsDefaultOrEmpty || draft.TintPlan is null ||
            draft.HeadTextureAuthority.RuntimeAuthority)
        {
            diagnostics.Add(Error("racemenu-record-authority-draft",
                "Record-authority draft is incomplete or contains an unsupported runtime claim."));
        }
    }

    private static FormReference SourceReference(PresetIdentifier identifier) =>
        new(identifier.Plugin ?? throw new InvalidDataException("Preset source plugin is absent."),
            identifier.FormId ?? throw new InvalidDataException("Preset source FormID is absent."));

    private RaceMenuPresetRecordAuthorityWriteResult RefusedWithCleanup(
        WorkspacePath output,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        TryDeleteOwnedOutput(output);
        return Refused(diagnostics);
    }

    private void TryDeleteOwnedOutput(WorkspacePath output)
    {
        try
        {
            if (output.IsUnder(labRoot) && File.Exists(output.Value) &&
                !File.GetAttributes(output.Value).HasFlag(FileAttributes.ReparsePoint))
                File.Delete(output.Value);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The caller receives the original failure. A retained file is
            // visible because this writer never overwrites an existing path.
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static RaceMenuPresetRecordAuthorityWriteResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());
}

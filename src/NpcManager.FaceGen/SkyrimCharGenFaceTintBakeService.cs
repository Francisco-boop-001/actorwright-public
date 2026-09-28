using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

/// <summary>
/// Hash-binds a canonical CharGen options artifact to the supported native
/// Skyrim FaceTint compositor, then records the exact reopened DDS evidence.
/// </summary>
public sealed class SkyrimCharGenFaceTintBakeService(
    IFaceGenOptionsService optionsService,
    ISkyrimNativeFaceTintPipelineService nativePipeline,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : ISkyrimCharGenFaceTintBakeService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<SkyrimCharGenFaceTintBakeResult> BuildAsync(
        SkyrimCharGenFaceTintBakeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateReceiptOutput(request.ReceiptOutput, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        FaceGenOptionsResult reviewed = await optionsService.ValidateAsync(
            new FaceGenOptionsRequest(
                GameEdition.SkyrimSpecialEdition,
                request.OptionsArtifact,
                null,
                request.ExpectedOptionsSha256,
                false),
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(reviewed.Diagnostics);
        if (!reviewed.IsValid || reviewed.Options is null)
        {
            diagnostics.Add(Error(
                "skyrim-chargen-facetint-options-review",
                "The exact canonical CharGen options artifact was not accepted."));
            return Refused(diagnostics);
        }

        diagnostics.AddRange(
            SkyrimCharGenNativeFaceTintConsumptionRules.Validate(
                reviewed.Options));
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        SkyrimNativeFaceTintPipelineResult native =
            await nativePipeline.BuildAsync(
                new SkyrimNativeFaceTintPipelineRequest(
                    GameEdition.SkyrimSpecialEdition,
                    request.DataRoot,
                    request.PluginOrder,
                    request.Npc,
                    request.ExpectedSex,
                    request.ExpectedRace,
                    request.FaceTintOutput),
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(native.Diagnostics);
        if (!native.Written || native.Artifact is null)
        {
            DeleteOwnedOutputs(request, diagnostics);
            diagnostics.Add(Error(
                "skyrim-chargen-facetint-native-build",
                "The native Skyrim FaceTint pipeline did not retain an output."));
            return Refused(diagnostics);
        }

        SkyrimNativeFaceTintBuildArtifact artifact = native.Artifact;
        if (artifact.Width != 512 || artifact.Height != 512 ||
            !string.Equals(artifact.Format, "bc3-dxt5",
                StringComparison.Ordinal) ||
            artifact.OutputSha256 != HashFile(request.FaceTintOutput))
        {
            DeleteOwnedOutputs(request, diagnostics);
            diagnostics.Add(Error(
                "skyrim-chargen-facetint-native-readback",
                "The native FaceTint output did not retain the options-bound 512/BC3 contract and exact reopened hash."));
            return Refused(diagnostics);
        }

        var receipt = new SkyrimCharGenFaceTintBakeReceipt(
            "1",
            "skyrim-chargen-options-native-facetint-receipt",
            request.OptionsArtifact,
            request.ExpectedOptionsSha256,
            reviewed.Options,
            reviewed.Options.DiffuseResolution,
            reviewed.Options.DiffuseCompression,
            "uniform-512-bc3-linear-red-mask-over-prev-race-order-constant-seed-no-overlays",
            artifact,
            native.PluginAuthorities,
            native.MaskAuthorities,
            RuntimeAuthority: false);
        byte[] receiptBytes = new UTF8Encoding(false).GetBytes(
            JsonSerializer.Serialize(receipt, JsonOptions) +
            Environment.NewLine);
        Sha256Hash receiptHash = Hash(receiptBytes);
        try
        {
            WriteAtomically(request.ReceiptOutput, receiptBytes);
            byte[] reopened = await File.ReadAllBytesAsync(
                request.ReceiptOutput.Value,
                CancellationToken.None).ConfigureAwait(false);
            if (!reopened.AsSpan().SequenceEqual(receiptBytes) ||
                Hash(reopened) != receiptHash)
            {
                diagnostics.Add(Error(
                    "skyrim-chargen-facetint-receipt-readback",
                    "The promoted options-to-FaceTint receipt changed on readback."));
                DeleteOwnedOutputs(request, diagnostics);
                return Refused(diagnostics);
            }

            return new SkyrimCharGenFaceTintBakeResult(
                true,
                receipt,
                receiptHash,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            DeleteOwnedOutputs(request, diagnostics);
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error(
                "skyrim-chargen-facetint-receipt-write",
                exception.Message));
            DeleteOwnedOutputs(request, diagnostics);
            return Refused(diagnostics);
        }
    }

    private void ValidateReceiptOutput(
        WorkspacePath output,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string? parent = Path.GetDirectoryName(output.Value);
        if (parent is null)
        {
            diagnostics.Add(Error(
                "skyrim-chargen-facetint-receipt-parent",
                "The receipt output requires an explicit parent directory."));
            return;
        }
        diagnostics.AddRange(policy.Evaluate(labRoot,
            new WorkspacePath(parent)));
        if (!Directory.Exists(parent))
        {
            diagnostics.Add(Error(
                "skyrim-chargen-facetint-receipt-parent-missing",
                "The receipt output parent must already exist."));
        }
        if (File.Exists(output.Value))
        {
            diagnostics.Add(Error(
                "skyrim-chargen-facetint-receipt-exists",
                "The receipt output already exists; writes never overwrite evidence."));
        }
        if (!string.Equals(Path.GetExtension(output.Value), ".json",
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error(
                "skyrim-chargen-facetint-receipt-extension",
                "The options-to-FaceTint receipt must use the .json extension."));
        }
    }

    private static void WriteAtomically(WorkspacePath output, byte[] bytes)
    {
        string temporary = output.Value + ".tmp-" +
                           Guid.NewGuid().ToString("N",
                               CultureInfo.InvariantCulture);
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, output.Value, overwrite: false);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static Sha256Hash HashFile(WorkspacePath path) =>
        File.Exists(path.Value)
            ? Hash(File.ReadAllBytes(path.Value))
            : new Sha256Hash(new string('0', 64));

    private static Sha256Hash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static void DeleteOwnedOutputs(
        SkyrimCharGenFaceTintBakeRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        DeleteOwnedOutput(request.ReceiptOutput, diagnostics);
        DeleteOwnedOutput(request.FaceTintOutput, diagnostics);
    }

    private static void DeleteOwnedOutput(
        WorkspacePath path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            if (File.Exists(path.Value)) File.Delete(path.Value);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException)
        {
            diagnostics.Add(Error(
                "skyrim-chargen-facetint-rollback-failed",
                $"An invalid owned output could not be removed: {exception.Message}"));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static SkyrimCharGenFaceTintBakeResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, null, diagnostics.ToImmutable());
}

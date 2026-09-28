using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Independently reopens one produced plugin and checks the transform artifact.
/// This verifier shares no write code with the transformer.
/// </summary>
public sealed class BethesdaSkyrimSavePluginVerifier(
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : ISkyrimSavePluginVerifier
{
    private const uint MasterFlag = 0x0000_0001;
    private const uint LightFlag = 0x0000_0200;

    public async ValueTask<SkyrimSavePluginVerifyResult> VerifyAsync(
        SkyrimSavePluginVerifyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!request.Plugin.IsUnder(labRoot))
            diagnostics.Add(Error(
                "save-plugin-verify-outside-lab",
                "The plugin verifier accepts only K-local files."));
        if (!File.Exists(request.Plugin.Value))
            diagnostics.Add(Error(
                "save-plugin-verify-missing",
                "The produced plugin does not exist."));
        else
            diagnostics.AddRange(policy.EvaluateReadRoot(
                labRoot,
                request.Plugin));
        if (HasErrors(diagnostics))
            return new(false, null, diagnostics.ToImmutable());

        try
        {
            Sha256Hash hash = await HashFileAsync(
                request.Plugin.Value,
                cancellationToken).ConfigureAwait(false);
            uint flags = ReadTes4Flags(request.Plugin.Value);
            bool master = (flags & MasterFlag) != 0;
            bool light = (flags & LightFlag) != 0;
            ModKey key = ModKey.FromNameAndExtension(
                Path.GetFileName(request.Plugin.Value));
            using var mod = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(
                    key,
                    new FilePath(request.Plugin.Value)),
                SkyrimRelease.SkyrimSE);
            ImmutableArray<FormId> npcs = mod.Npcs
                .Select(item => new FormId(item.FormKey.ID))
                .OrderBy(item => item.Value)
                .ToImmutableArray();
            string? listEditorId =
                request.Expected.LeveledNpcEditorId;
            ImmutableArray<FormId> entries = [];
            if (!string.IsNullOrEmpty(listEditorId))
            {
                ILeveledNpcGetter? list =
                    mod.LeveledNpcs.SingleOrDefault(item =>
                        string.Equals(
                            item.EditorID,
                            listEditorId,
                            StringComparison.OrdinalIgnoreCase));
                if (list is null)
                    diagnostics.Add(Error(
                        "save-plugin-lvln-missing",
                        $"Expected LVLN '{listEditorId}' is absent."));
                else
                    entries = (list.Entries ?? [])
                        .Where(item => item.Data is not null)
                        .Select(item => new FormId(
                            item.Data!.Reference.FormKey.ID))
                        .ToImmutableArray();
            }

            Compare(
                hash,
                master,
                light,
                npcs,
                entries,
                request.Expected,
                diagnostics);
            VerifyEncoding(
                request.Plugin.Value,
                request.Expected.EncodingMode,
                request.EncodingProbes,
                diagnostics);
            var artifact =
                new SkyrimSavePluginVerificationArtifact(
                    request.Plugin,
                    hash,
                    master,
                    light,
                    request.Expected.EncodingMode,
                    npcs,
                    listEditorId,
                    entries,
                    false);
            return new(
                !HasErrors(diagnostics),
                artifact,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException)
        {
            diagnostics.Add(Error(
                "save-plugin-verify-failed",
                exception.Message));
            return new(false, null, diagnostics.ToImmutable());
        }
    }

    private static void Compare(
        Sha256Hash hash,
        bool master,
        bool light,
        ImmutableArray<FormId> npcs,
        ImmutableArray<FormId> entries,
        SkyrimSavePluginTransformArtifact expected,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (hash != expected.OutputSha256)
            diagnostics.Add(Error(
                "save-plugin-hash-mismatch",
                "The produced plugin hash differs from the transform artifact."));
        if (master != expected.MarkAsMaster ||
            light != expected.LightMaster)
            diagnostics.Add(Error(
                "save-plugin-flags-mismatch",
                "Raw TES4 ESM/ESL flags differ from the transform artifact."));
        if (!npcs.SequenceEqual(expected.NpcFormIds))
            diagnostics.Add(Error(
                "save-plugin-npc-mismatch",
                "The reopened NPC inventory differs from the transform artifact."));
        if (!entries.SequenceEqual(expected.LeveledNpcEntries))
            diagnostics.Add(Error(
                "save-plugin-lvln-mismatch",
                "The reopened LVLN entries differ from the transform artifact."));
    }

    private static void VerifyEncoding(
        string path,
        SkyrimSaveEncodingMode mode,
        ImmutableArray<SkyrimSavePluginEncodingProbe> probes,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (mode == SkyrimSaveEncodingMode.PreservePackageBytes ||
            probes.IsDefaultOrEmpty)
            return;
        Encoding.RegisterProvider(
            CodePagesEncodingProvider.Instance);
        Encoding encoding =
            mode == SkyrimSaveEncodingMode.Utf8
                ? new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true)
                : Encoding.GetEncoding(
                    1252,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
        byte[] bytes = File.ReadAllBytes(path);
        foreach (SkyrimSavePluginEncodingProbe probe in probes)
        {
            byte[] expected = encoding.GetBytes(probe.Value);
            if (bytes.AsSpan().IndexOf(expected) < 0)
                diagnostics.Add(Error(
                    "save-plugin-encoding-mismatch",
                    $"The expected {mode} bytes for an encoding probe are absent."));
        }
    }

    private static uint ReadTes4Flags(string path)
    {
        Span<byte> header = stackalloc byte[12];
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            header.Length,
            FileOptions.SequentialScan);
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("TES4"u8))
            throw new InvalidDataException(
                "The produced plugin does not begin with TES4.");
        return BinaryPrimitives.ReadUInt32LittleEndian(
            header[8..12]);
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(
                stream,
                cancellationToken).ConfigureAwait(false)));
    }

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(
        string code,
        string message) =>
        new(code, DiagnosticSeverity.Error, message);
}

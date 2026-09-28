using System.Collections.Immutable;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Resolves supported winning records without imposing mesh/TRI requirements.
/// This is the record-identity seam, not the FaceGen geometry seam.
/// </summary>
public sealed class BethesdaSkyrimMajorRecordBindingReader(
    ISkyrimFaceRecordPluginAuthorityLoader authorityLoader)
    : ISkyrimMajorRecordBindingReader
{
    private static readonly RecordSignature RaceSignature = new("RACE");
    private static readonly RecordSignature HeadPartSignature = new("HDPT");
    private static readonly RecordSignature TextureSetSignature = new("TXST");
    private static readonly RecordSignature ColorSignature = new("CLFM");
    private const int MaximumPlugins = 64;
    private const int MaximumSelections = 256;

    public async ValueTask<SkyrimMajorRecordBindingResult> ReadAsync(
        SkyrimMajorRecordBindingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        SkyrimFaceRecordPluginAuthorityResult current =
            await authorityLoader.LoadAsync(
                new SkyrimFaceRecordPluginAuthorityRequest(
                    request.Edition,
                    request.DataRoot,
                    request.PluginOrder.Select(item => item.Plugin).ToImmutableArray()),
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(current.Diagnostics);
        if (!current.Accepted ||
            !MatchesReviewedAuthorities(current.Authorities, request.PluginOrder))
        {
            if (current.Accepted)
            {
                diagnostics.Add(Error("skyrim-record-binding-authority-stale",
                    "The copied plugin order no longer matches the reviewed paths and SHA-256 values."));
            }
            return Refused(diagnostics);
        }

        var requested = request.Selections.ToDictionary(
            item => RecordKey.From(item.Reference));
        var winners = new Dictionary<RecordKey, DecodedBinding>();
        try
        {
            foreach (SkyrimFaceRecordPluginAuthority plugin in current.Authorities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var mod = SkyrimMod.CreateFromBinaryOverlay(
                    plugin.Path.Value, SkyrimRelease.SkyrimSE);
                var provider = new SkyrimFaceRecordProvider(
                    plugin.Plugin, plugin.Path, plugin.ExpectedSha256);
                foreach (IMajorRecordGetter record in mod.EnumerateMajorRecords())
                {
                    RecordKey key = RecordKey.From(record.FormKey);
                    if (!requested.ContainsKey(key)) continue;
                    DecodedBinding? decoded = Decode(record, provider);
                    if (decoded is not null) winners[key] = decoded;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(Error("skyrim-record-binding-provider-malformed",
                $"The copied plugin order could not be decoded as supported Skyrim SE records: {exception.Message}"));
            return Refused(diagnostics);
        }

        var bindings = ImmutableArray.CreateBuilder<SkyrimMajorRecordBinding>(
            request.Selections.Length);
        foreach (SkyrimMajorRecordBindingSelection selection in request.Selections)
        {
            if (!winners.TryGetValue(RecordKey.From(selection.Reference), out DecodedBinding? winner) ||
                winner.IsDeleted)
            {
                diagnostics.Add(Error("skyrim-record-binding-missing",
                    $"Winning {selection.ExpectedSignature} {selection.Reference} is unavailable in the reviewed plugin order."));
                continue;
            }
            if (winner.Signature != selection.ExpectedSignature)
            {
                diagnostics.Add(Error("skyrim-record-binding-signature",
                    $"Record {selection.Reference} is {winner.Signature}, not expected {selection.ExpectedSignature}."));
                continue;
            }
            bindings.Add(new SkyrimMajorRecordBinding(
                selection.Reference,
                winner.Signature,
                winner.Provider,
                winner.HeadPartType));
        }

        return HasErrors(diagnostics)
            ? Refused(diagnostics)
            : new SkyrimMajorRecordBindingResult(
                true, bindings.ToImmutable(), diagnostics.ToImmutable());
    }

    private static DecodedBinding? Decode(
        IMajorRecordGetter record,
        SkyrimFaceRecordProvider provider) => record switch
        {
            IRaceGetter race => new DecodedBinding(
                RaceSignature, provider, null, race.IsDeleted),
            IHeadPartGetter headPart => new DecodedBinding(
                HeadPartSignature,
                provider,
                ReadHeadPartType(headPart.Type),
                headPart.IsDeleted),
            ITextureSetGetter textureSet => new DecodedBinding(
                TextureSetSignature, provider, null, textureSet.IsDeleted),
            IColorRecordGetter color => new DecodedBinding(
                ColorSignature, provider, null, color.IsDeleted),
            _ => null
        };

    private static NpcHeadPartType? ReadHeadPartType<TEnum>(TEnum? rawType)
        where TEnum : struct, Enum
    {
        if (rawType is null) return null;
        int raw = Convert.ToInt32(rawType.Value);
        return raw < 0
            ? null
            : raw <= 9
                ? (NpcHeadPartType)raw
                : NpcHeadPartType.Misc;
    }

    private static void ValidateRequest(
        SkyrimMajorRecordBindingRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("skyrim-record-binding-edition",
                "Major-record binding supports Skyrim Special Edition only."));
        if (string.IsNullOrWhiteSpace(request.DataRoot.Value))
            diagnostics.Add(Error("skyrim-record-binding-data-root",
                "A reviewed copied Data root is required."));
        if (request.PluginOrder.IsDefaultOrEmpty ||
            request.PluginOrder.Length > MaximumPlugins ||
            request.PluginOrder.Any(item => item is null))
            diagnostics.Add(Error("skyrim-record-binding-plugin-order",
                $"PluginOrder must contain 1-{MaximumPlugins} explicit reviewed authorities."));
        if (request.Selections.IsDefaultOrEmpty ||
            request.Selections.Length > MaximumSelections ||
            request.Selections.Any(item => item is null))
        {
            diagnostics.Add(Error("skyrim-record-binding-selections",
                $"Selections must contain 1-{MaximumSelections} explicit records."));
            return;
        }

        var keys = new HashSet<RecordKey>();
        foreach (SkyrimMajorRecordBindingSelection selection in request.Selections)
        {
            if (string.IsNullOrWhiteSpace(selection.Reference.Plugin.Value) ||
                selection.Reference.FormId.Value is 0 or > 0x00FF_FFFF ||
                !IsSupported(selection.ExpectedSignature) ||
                !keys.Add(RecordKey.From(selection.Reference)))
            {
                diagnostics.Add(Error("skyrim-record-binding-selection",
                    "Every selected record must have one distinct nonzero reference and a supported RACE/HDPT/TXST/CLFM signature."));
            }
        }
    }

    private static bool IsSupported(RecordSignature signature) =>
        signature == RaceSignature || signature == HeadPartSignature ||
        signature == TextureSetSignature || signature == ColorSignature;

    private static bool MatchesReviewedAuthorities(
        ImmutableArray<SkyrimFaceRecordPluginAuthority> current,
        ImmutableArray<SkyrimFaceRecordPluginAuthority> reviewed)
    {
        if (current.Length != reviewed.Length) return false;
        for (var index = 0; index < current.Length; index++)
        {
            if (!string.Equals(current[index].Plugin.Value,
                    reviewed[index].Plugin.Value, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(current[index].Path.Value,
                    reviewed[index].Path.Value, StringComparison.OrdinalIgnoreCase) ||
                current[index].ExpectedSha256 != reviewed[index].ExpectedSha256)
                return false;
        }
        return true;
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static SkyrimMajorRecordBindingResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, [], diagnostics.ToImmutable());

    private readonly record struct RecordKey(string Plugin, uint FormId)
    {
        public static RecordKey From(FormReference reference) =>
            new(reference.Plugin.Value.ToUpperInvariant(), reference.FormId.Value);

        public static RecordKey From(FormKey key) =>
            new(key.ModKey.ToString().ToUpperInvariant(), key.ID);
    }

    private sealed record DecodedBinding(
        RecordSignature Signature,
        SkyrimFaceRecordProvider Provider,
        NpcHeadPartType? HeadPartType,
        bool IsDeleted);
}

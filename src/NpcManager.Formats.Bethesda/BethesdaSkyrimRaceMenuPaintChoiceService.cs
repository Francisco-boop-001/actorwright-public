using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Builds the RaceMenu paint list from the compiled Papyrus artifacts selected
/// by one explicit copied Skyrim load order. It never guesses from texture
/// filenames and never claims that a listed path rendered in Skyrim.
/// </summary>
public sealed class BethesdaSkyrimRaceMenuPaintChoiceService(
    IWorkspacePolicy workspacePolicy,
    WorkspacePath labRoot) : ISkyrimRaceMenuPaintChoiceService
{
    private const int MaximumSearchLength = 256;
    private const int MaximumRegistrations = 100_000;
    private const int MaximumTexturePathLength = 512;

    private readonly BethesdaSkyrimRaceMenuPaintCatalogScanner scanner =
        new(workspacePolicy, labRoot);

    public async ValueTask<SkyrimRaceMenuPaintChoiceResult> SearchAsync(
        SkyrimRaceMenuPaintChoiceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!Enum.IsDefined(request.Category))
        {
            diagnostics.Add(Error(
                "paint-catalog-category-invalid",
                "The RaceMenu paint category must be a recognized typed value."));
            return Refused(diagnostics);
        }
        string search = request.Search?.Trim() ?? string.Empty;
        if (search.Length > MaximumSearchLength)
        {
            diagnostics.Add(Error(
                "paint-catalog-search-length",
                $"Paint search text may contain at most {MaximumSearchLength} characters."));
            return Refused(diagnostics);
        }

        SkyrimPaintCatalogScanResult scan = await scanner.ScanAsync(
            request.DataRoot,
            request.PluginOrder,
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(scan.Diagnostics);
        if (!scan.Accepted || HasErrors(diagnostics)) return Refused(diagnostics);

        ImmutableArray<SkyrimPaintScannedScript> paintScripts = scan.Scripts
            .Where(item => item.ParseResult.MentionsPaint)
            .ToImmutableArray();
        int malformedPaintScripts = paintScripts.Count(item => !item.ParseResult.Parsed);
        int registrationCount;
        try
        {
            registrationCount = checked(paintScripts
                .Where(item => item.ParseResult.Parsed)
                .Sum(item => item.ParseResult.Registrations.Length));
        }
        catch (OverflowException)
        {
            registrationCount = int.MaxValue;
        }
        if (registrationCount > MaximumRegistrations)
        {
            diagnostics.Add(Error(
                "paint-catalog-registration-count",
                $"Paint registrations exceed the {MaximumRegistrations} entry limit."));
        }
        if (malformedPaintScripts > 0)
        {
            diagnostics.Add(Error(
                "paint-catalog-pex-malformed",
                $"{malformedPaintScripts} winning PEX file(s) mention RaceMenu paint registration methods but could not be parsed safely."));
        }
        if (HasErrors(diagnostics))
        {
            return Refused(
                diagnostics,
                BuildSummary(request, scan, paintScripts.Length,
                    malformedPaintScripts, registrationCount, [], EmptyHash));
        }

        ImmutableArray<SkyrimRaceMenuPaintChoiceCandidate> categoryCandidates =
            BuildCandidates(paintScripts, request.Category, cancellationToken);
        Sha256Hash catalogHash = HashCatalog(categoryCandidates);
        SkyrimRaceMenuPaintCatalogSummary summary = BuildSummary(
            request,
            scan,
            paintScripts.Length,
            malformedPaintScripts,
            registrationCount,
            categoryCandidates,
            catalogHash);
        ImmutableArray<SkyrimRaceMenuPaintChoiceCandidate> visible = categoryCandidates
            .Where(item => Matches(item, search))
            .ToImmutableArray();
        return new SkyrimRaceMenuPaintChoiceResult(
            true,
            visible,
            summary,
            diagnostics.ToImmutable());
    }

    private static ImmutableArray<SkyrimRaceMenuPaintChoiceCandidate> BuildCandidates(
        ImmutableArray<SkyrimPaintScannedScript> scripts,
        SkyrimRaceMenuPaintCategory category,
        CancellationToken cancellationToken)
    {
        var byChoice = new Dictionary<string, CandidateAccumulator>(
            StringComparer.OrdinalIgnoreCase);
        foreach (SkyrimPaintScannedScript script in scripts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!script.ParseResult.Parsed) continue;
            var source = new SkyrimRaceMenuPaintRegistrationSource(
                script.ScriptPath,
                script.ProviderKind,
                script.ProviderPath,
                script.ProviderSha256,
                script.PexSha256,
                script.PexLength);
            foreach (SkyrimPexPaintRegistration registration in
                     script.ParseResult.Registrations.Where(item => item.Category == category))
            {
                if (!TryPrimaryPath(
                        registration.Path,
                        out AssetPath registeredPath,
                        out AssetPath canonicalPath))
                {
                    continue;
                }
                string registeredName = registration.Name.Trim();
                string displayName = registeredName.StartsWith('$')
                    ? registeredName[1..]
                    : registeredName;
                if (displayName.Length == 0) displayName = registeredPath.Value;
                ImmutableArray<SkyrimRaceMenuPaintTextureSlot> slots =
                    BuildTextureSlots(registration, registeredPath);
                string key = string.Concat(
                    displayName,
                    "\0",
                    canonicalPath.Value);
                if (!byChoice.TryGetValue(key, out CandidateAccumulator? accumulator))
                {
                    accumulator = new CandidateAccumulator(
                        category,
                        registeredName,
                        displayName,
                        registeredPath,
                        canonicalPath,
                        slots);
                    byChoice.Add(key, accumulator);
                }
                accumulator.AddSource(source);
            }
        }

        return byChoice.Values
            .Select(item => item.Build())
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DisplayName, StringComparer.Ordinal)
            .ThenBy(item => item.CanonicalTexturePath.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.CanonicalTexturePath.Value, StringComparer.Ordinal)
            .ThenBy(item => item.Sources[0].ScriptPath.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Sources[0].ScriptPath.Value, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static ImmutableArray<SkyrimRaceMenuPaintTextureSlot> BuildTextureSlots(
        SkyrimPexPaintRegistration registration,
        AssetPath primary)
    {
        if (registration.Slots.IsDefaultOrEmpty)
        {
            return
            [
                new SkyrimRaceMenuPaintTextureSlot(
                    0,
                    SkyrimRaceMenuPaintSlotKind.Texture,
                    primary.Value,
                    primary)
            ];
        }

        var slots = ImmutableArray.CreateBuilder<SkyrimRaceMenuPaintTextureSlot>(
            registration.Slots.Length);
        foreach (SkyrimPexPaintSlot slot in registration.Slots)
        {
            string? value = slot.Value?.Trim();
            if (slot.OperandKind == SkyrimPexPaintOperandKind.Identifier)
            {
                slots.Add(new SkyrimRaceMenuPaintTextureSlot(
                    slot.Index,
                    SkyrimRaceMenuPaintSlotKind.Computed,
                    value,
                    null));
            }
            else if (slot.OperandKind != SkyrimPexPaintOperandKind.StringLiteral ||
                     string.IsNullOrEmpty(value))
            {
                slots.Add(new SkyrimRaceMenuPaintTextureSlot(
                    slot.Index,
                    SkyrimRaceMenuPaintSlotKind.Empty,
                    value,
                    null));
            }
            else if (value.Equals("ignore", StringComparison.OrdinalIgnoreCase))
            {
                slots.Add(new SkyrimRaceMenuPaintTextureSlot(
                    slot.Index,
                    SkyrimRaceMenuPaintSlotKind.Ignore,
                    value,
                    null));
            }
            else if (value.StartsWith("::", StringComparison.Ordinal) ||
                     !TryRegisteredPath(value, out AssetPath path))
            {
                slots.Add(new SkyrimRaceMenuPaintTextureSlot(
                    slot.Index,
                    SkyrimRaceMenuPaintSlotKind.Computed,
                    value,
                    null));
            }
            else
            {
                slots.Add(new SkyrimRaceMenuPaintTextureSlot(
                    slot.Index,
                    SkyrimRaceMenuPaintSlotKind.Texture,
                    value,
                    path));
            }
        }
        return slots.MoveToImmutable();
    }

    private static bool TryPrimaryPath(
        string value,
        out AssetPath registered,
        out AssetPath canonical)
    {
        registered = default;
        canonical = default;
        string normalized = value.Trim().Replace('\\', '/');
        if (normalized.Length == 0 || normalized.Length > MaximumTexturePathLength ||
            normalized.Equals("ignore", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("::", StringComparison.Ordinal))
        {
            return false;
        }
        try
        {
            registered = new AssetPath(normalized);
            canonical = normalized.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)
                ? registered
                : new AssetPath("textures/" + normalized);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryRegisteredPath(string value, out AssetPath path)
    {
        path = default;
        string normalized = value.Trim().Replace('\\', '/');
        if (normalized.Length == 0 || normalized.Length > MaximumTexturePathLength)
            return false;
        try
        {
            path = new AssetPath(normalized);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool Matches(
        SkyrimRaceMenuPaintChoiceCandidate candidate,
        string search) =>
        search.Length == 0 ||
        candidate.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        candidate.RegisteredPath.Value.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        candidate.CanonicalTexturePath.Value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static Sha256Hash HashCatalog(
        ImmutableArray<SkyrimRaceMenuPaintChoiceCandidate> candidates)
    {
        var text = new StringBuilder();
        foreach (SkyrimRaceMenuPaintChoiceCandidate candidate in candidates)
        {
            text.Append(candidate.Category.ToWireName()).Append('\0')
                .Append(candidate.RegisteredName).Append('\0')
                .Append(candidate.DisplayName).Append('\0')
                .Append(candidate.RegisteredPath.Value).Append('\0')
                .Append(candidate.CanonicalTexturePath.Value).Append('\n');
            foreach (SkyrimRaceMenuPaintTextureSlot slot in candidate.TextureSlots)
            {
                text.Append(slot.Index).Append('\0')
                    .Append(slot.Kind).Append('\0')
                    .Append(slot.RegisteredValue).Append('\0')
                    .Append(slot.TexturePath?.Value).Append('\n');
            }
            foreach (SkyrimRaceMenuPaintRegistrationSource source in candidate.Sources)
            {
                text.Append(source.ScriptPath.Value).Append('\0')
                    .Append(source.ProviderKind).Append('\0')
                    .Append(source.ProviderSha256.Value).Append('\0')
                    .Append(source.PexSha256.Value).Append('\0')
                    .Append(source.PexLength).Append('\n');
            }
        }
        return new Sha256Hash(Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))));
    }

    private static SkyrimRaceMenuPaintCatalogSummary BuildSummary(
        SkyrimRaceMenuPaintChoiceRequest request,
        SkyrimPaintCatalogScanResult scan,
        int paintScriptCount,
        int malformedPaintScriptCount,
        int registrationCount,
        ImmutableArray<SkyrimRaceMenuPaintChoiceCandidate> candidates,
        Sha256Hash hash) =>
        new(
            request.PluginOrder.Length,
            scan.AdmittedArchiveCount,
            scan.ArchiveScriptEntryCount,
            scan.LooseScriptEntryCount,
            scan.Scripts.Length,
            paintScriptCount,
            malformedPaintScriptCount,
            registrationCount,
            candidates.Length,
            hash);

    private static Sha256Hash EmptyHash { get; } = new(Convert.ToHexString(
        SHA256.HashData([])));

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static SkyrimRaceMenuPaintChoiceResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        SkyrimRaceMenuPaintCatalogSummary? summary = null) =>
        new(false, [], summary, diagnostics.ToImmutable());

    private sealed class CandidateAccumulator(
        SkyrimRaceMenuPaintCategory category,
        string registeredName,
        string displayName,
        AssetPath registeredPath,
        AssetPath canonicalPath,
        ImmutableArray<SkyrimRaceMenuPaintTextureSlot> slots)
    {
        private readonly Dictionary<string, SkyrimRaceMenuPaintRegistrationSource> sources =
            new(StringComparer.OrdinalIgnoreCase);

        public void AddSource(SkyrimRaceMenuPaintRegistrationSource source)
        {
            string key = string.Concat(
                source.ScriptPath.Value,
                "\0",
                source.PexSha256.Value);
            sources.TryAdd(key, source);
        }

        public SkyrimRaceMenuPaintChoiceCandidate Build() =>
            new(
                category,
                registeredName,
                displayName,
                registeredPath,
                canonicalPath,
                slots,
                sources.Values
                    .OrderBy(item => item.ScriptPath.Value, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.ScriptPath.Value, StringComparer.Ordinal)
                    .ThenBy(item => item.PexSha256.Value, StringComparer.Ordinal)
                    .ToImmutableArray());
    }
}

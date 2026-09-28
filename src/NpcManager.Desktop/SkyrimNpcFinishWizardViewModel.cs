using System.ComponentModel;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed class SkyrimNpcFinishWizardViewModel(
    SkyrimNpcFinishWizardTransaction transaction,
    Func<ReviewedGameIntake, FormReference?>? targetRaceResolver = null) : INotifyPropertyChanged, IDisposable
{
    private readonly SkyrimNpcFinishWizardTransaction transaction =
        transaction ?? throw new ArgumentNullException(nameof(transaction));
    private readonly Func<ReviewedGameIntake, FormReference?>? targetRaceResolver =
        targetRaceResolver;
    private int currentStep;
    private string status = "Source: choose a reviewed request.";
    private bool busy;
    private ReviewedGameIntake? reviewedIntake;
    private ExternalHeadPartInstallVerificationContext? installContext;
    private ExternalHeadPartInstallVerificationArtifact? externalVerification;
    private bool freshInstallObservation;
    private bool completeReviewedIntake;
    private string? externalProviderName;
    private Sha256Hash? externalProviderExpectedSha256;
    private Sha256Hash? externalProviderCurrentSha256;
    private bool? externalProviderEnabled;
    private string? externalDescriptorId;
    private ImmutableArray<string> externalMissingPortableItems = [];
    private ImmutableArray<string> externalDriftedPortableItems = [];
    private ExternalInstallDependencyState externalInstallDependencyState =
        ExternalInstallDependencyState.NotRequired;
    private bool externalInstallReady = true;
    private string externalProviderCurrentHash = "Not checked this invocation";
    private string externalProviderEnabledState = "Not checked this invocation";
    private string externalHistoricalSnapshot = "None";
    private string externalNextGate = "No external install dependency.";

    public IReadOnlyList<string> Steps { get; } =
        ["Source", "Role and Outfit", "Routine", "Finish Core Review"];

    public int CurrentStep
    {
        get => currentStep;
        private set
        {
            if (currentStep == value) return;
            currentStep = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentStepLabel));
        }
    }

    public string CurrentStepLabel => Steps[CurrentStep];

    public string Status
    {
        get => status;
        private set
        {
            if (status == value) return;
            status = value;
            OnPropertyChanged();
        }
    }

    public bool IsBusy
    {
        get => busy;
        private set
        {
            if (busy == value) return;
            busy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNotBusy));
            OnPropertyChanged(nameof(CanApply));
        }
    }

    public bool IsNotBusy => !IsBusy;

    public SkyrimNpcFinishCoreProposal? Proposal { get; private set; }

    public SkyrimNpcFinishCoreManifest? Manifest { get; private set; }

    public SkyrimNpcFinishCoreVerification? Verification { get; private set; }

    public ReviewedGameIntake? ReviewedIntake => reviewedIntake;

    public bool HasCompleteReviewedIntake => completeReviewedIntake;

    public IReadOnlyList<PluginName> ReviewedEnabledPluginOrder =>
        installContext?.EnabledPluginOrder ?? ImmutableArray<PluginName>.Empty;

    public string? ExternalProviderName => externalProviderName;

    public string ExternalProviderNameDisplay =>
        externalProviderName ?? "Not required";

    public Sha256Hash? ExternalProviderExpectedSha256 =>
        externalProviderExpectedSha256;

    public Sha256Hash? ExternalProviderCurrentSha256 =>
        externalProviderCurrentSha256;

    public bool? ExternalProviderEnabled => externalProviderEnabled;

    public string ExternalProviderExpectedHash =>
        externalProviderExpectedSha256?.Value ?? "Not available";

    public string ExternalProviderCurrentHash => externalProviderCurrentHash;

    public string ExternalProviderEnabledState => externalProviderEnabledState;

    public string? ExternalDescriptorId => externalDescriptorId;

    public string ExternalDescriptorIdDisplay =>
        externalDescriptorId ?? "Not available";

    public IReadOnlyList<string> ExternalMissingPortableItems =>
        externalMissingPortableItems;

    public IReadOnlyList<string> ExternalDriftedPortableItems =>
        externalDriftedPortableItems;

    public string ExternalMissingPortableItemsText =>
        externalMissingPortableItems.IsDefaultOrEmpty
            ? "None"
            : string.Join(", ", externalMissingPortableItems);

    public string ExternalDriftedPortableItemsText =>
        externalDriftedPortableItems.IsDefaultOrEmpty
            ? "None"
            : string.Join(", ", externalDriftedPortableItems);

    public ExternalInstallDependencyState ExternalInstallDependencyState =>
        externalInstallDependencyState;

    public bool ExternalInstallReady => externalInstallReady;

    public bool InstallReady => ExternalInstallReady;

    public string ExternalHistoricalSnapshot => externalHistoricalSnapshot;

    public string ExternalNextGate => externalNextGate;

    public string NextGate => ExternalNextGate;

    public string ExternalNextAction =>
        freshInstallObservation
            ? externalVerification?.MissingPrerequisites.FirstOrDefault()?.NextAction ??
              externalNextGate
            : externalNextGate;

    public bool CanApply =>
        !IsBusy &&
        Proposal is not null &&
        (Proposal.ExternalHeadParts is null
            ? true
            : Proposal.Status == SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite &&
              completeReviewedIntake && freshInstallObservation &&
              externalInstallDependencyState ==
                  ExternalInstallDependencyState.Verified &&
              externalInstallReady);

    public bool PlacementIncluded => transaction is null;

    public bool RuntimeAuthority => transaction is null;

    public bool VisualAuthority => transaction is null;

    public void ApplyReviewedIntake(ReviewedGameIntake? intake)
    {
        reviewedIntake = intake;
        installContext = BuildInstallContext(intake, targetRaceResolver);
        completeReviewedIntake = installContext is not null;
        freshInstallObservation = false;

        ProjectExternalVerification(
            externalVerification ?? Proposal?.ExternalHeadParts?.Verification,
            hasFreshContext: false);
        OnPropertyChanged(nameof(ReviewedIntake));
        OnPropertyChanged(nameof(HasCompleteReviewedIntake));
        OnPropertyChanged(nameof(ReviewedEnabledPluginOrder));
        OnPropertyChanged(nameof(CanApply));
    }

    public async Task<SkyrimNpcFinishCoreProposalResult> AnalyzeAsync(
        WorkspacePath requestPath,
        Sha256Hash requestSha256,
        WorkspacePath proposalPath,
        CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            CurrentStep = 0;
            SkyrimNpcFinishCoreProposalResult result = await transaction.AnalyzeAsync(
                requestPath,
                requestSha256,
                proposalPath,
                installContext,
                cancellationToken);
            Proposal = result.Proposal;
            OnPropertyChanged(nameof(Proposal));
            externalVerification = result.Proposal?.ExternalHeadParts?.Verification;
            freshInstallObservation = installContext is not null;
            ProjectExternalVerification(externalVerification, freshInstallObservation);
            CurrentStep = result.Proposed ? 3 : 0;
            Status = result.Proposed
                ? result.Proposal?.Status ==
                      SkyrimNpcFinishCoreStatus.StaticPassInstallDependencyRequired
                    ? $"Finish Core proposal ready; external install dependency required. {ExternalNextGate}"
                    : "Finish Core proposal ready for human review. Placement is not included."
                : "Finish Core proposal refused; review the diagnostics.";
            OnPropertyChanged(nameof(CanApply));
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<SkyrimNpcFinishCoreApplyResult> ApplyAsync(
        WorkspacePath requestPath,
        Sha256Hash requestSha256,
        WorkspacePath proposalPath,
        Sha256Hash proposalSha256,
        CancellationToken cancellationToken)
    {
        if (!CanApply)
        {
            SkyrimNpcFinishCoreApplyResult refused = new(
                false,
                null,
                null,
                null,
                [new Diagnostic(
                    "desktop-finish-core-apply-disabled",
                    DiagnosticSeverity.Error,
                    ExternalNextGate)]);
            Status = $"Finish Core apply disabled. {ExternalNextGate}";
            return refused;
        }
        IsBusy = true;
        try
        {
            CurrentStep = 3;
            SkyrimNpcFinishCoreApplyResult result = await transaction.ApplyAsync(
                requestPath,
                requestSha256,
                proposalPath,
                proposalSha256,
                installContext,
                cancellationToken);
            Manifest = result.Manifest;
            OnPropertyChanged(nameof(Manifest));
            Status = result.Applied
                ? "Finish Core package published; runtime and visual authority remain open."
                : "Finish Core apply refused or produced no changes.";
            OnPropertyChanged(nameof(CanApply));
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<SkyrimNpcFinishCoreVerificationResult> VerifyAsync(
        WorkspacePath manifestPath,
        Sha256Hash manifestSha256,
        CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            CurrentStep = 3;
            SkyrimNpcFinishCoreVerificationResult result = await transaction.VerifyAsync(
                manifestPath,
                manifestSha256,
                installContext,
                cancellationToken);
            Verification = result.Verification;
            OnPropertyChanged(nameof(Verification));
            externalVerification = result.Verification?.ExternalHeadParts?.Verification;
            freshInstallObservation = installContext is not null;
            ProjectExternalVerification(externalVerification, freshInstallObservation);
            Status = result.Verified
                ? "Finish Core statically verified; runtime and visual authority remain open."
                : "Finish Core verification refused; review the diagnostics.";
            OnPropertyChanged(nameof(CanApply));
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose() { }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static ExternalHeadPartInstallVerificationContext?
        BuildInstallContext(
            ReviewedGameIntake? intake,
            Func<ReviewedGameIntake, FormReference?>? targetRaceResolver)
    {
        if (intake is null || intake.Plugins.IsDefaultOrEmpty)
            return null;

        PluginClosureReviewEntry[] ordered = intake.Plugins
            .OrderBy(item => item.Order)
            .ToArray();
        if (ordered.Any(item => item.Enabled &&
                (!item.Exists || !item.ReadSucceeded)))
            return null;

        PluginClosureReviewEntry[] enabled = ordered
            .Where(item => item.Enabled && item.Exists && item.ReadSucceeded)
            .ToArray();
        if (enabled.Length == 0 ||
            enabled.Select(item => item.Plugin.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != enabled.Length ||
            enabled.Zip(enabled.Skip(1), (left, right) => left.Order < right.Order)
                .Any(item => !item))
            return null;

        FormReference? targetRace = targetRaceResolver?.Invoke(intake);
        if (targetRace is not { } exactTargetRace)
            return null;

        return new ExternalHeadPartInstallVerificationContext(
            intake.DataRoot,
            enabled.Select(item => item.Plugin).ToImmutableArray(),
            exactTargetRace);
    }

    private void ProjectExternalVerification(
        ExternalHeadPartInstallVerificationArtifact? artifact,
        bool hasFreshContext)
    {
        externalProviderName = null;
        externalProviderExpectedSha256 = null;
        externalProviderCurrentSha256 = null;
        externalProviderEnabled = null;
        externalDescriptorId = null;
        externalMissingPortableItems = [];
        externalDriftedPortableItems = [];
        externalInstallDependencyState =
            ExternalInstallDependencyState.NotRequired;
        externalInstallReady = true;
        externalProviderCurrentHash = hasFreshContext
            ? "Not available"
            : "Not checked this invocation";
        externalProviderEnabledState = hasFreshContext
            ? "Not available"
            : "Not checked this invocation";
        externalHistoricalSnapshot = "None";
        externalNextGate = "No external install dependency.";

        if (artifact is not null)
        {
            ExternalHeadPartInstallProviderObservation? provider =
                artifact.ProviderObservations.FirstOrDefault();
            externalProviderName = provider?.ProviderPlugin.Value;
            externalProviderExpectedSha256 = provider?.ExpectedSha256;
            externalProviderCurrentSha256 = hasFreshContext
                ? provider?.CurrentSha256
                : null;
            externalProviderEnabled = hasFreshContext
                ? provider?.Enabled
                : null;
            externalDescriptorId = artifact.DescriptorIds.Length == 0
                ? null
                : string.Join(", ", artifact.DescriptorIds.Select(item => item.Value));
            externalInstallDependencyState = hasFreshContext
                ? artifact.CurrentInstallDependencyState
                : ExternalInstallDependencyState.DeclaredUnverified;
            externalInstallReady = hasFreshContext && artifact.InstallReady;
            externalProviderCurrentHash = hasFreshContext
                ? provider?.CurrentSha256?.Value ?? "Missing"
                : "Not checked this invocation";
            externalProviderEnabledState = hasFreshContext
                ? provider?.Enabled switch
                {
                    true => "Enabled",
                    false => "Disabled",
                    null => "Missing"
                }
                : "Not checked this invocation";
            externalHistoricalSnapshot = hasFreshContext
                ? "Current invocation"
                : artifact.VerifiedInstallSnapshot is null
                    ? "None"
                    : "Historical only";

            if (hasFreshContext)
            {
                externalMissingPortableItems = artifact.MissingPrerequisites
                    .Where(item => !IsDrift(item))
                    .Select(item => item.PortableIdentity)
                    .Where(IsPortableIdentity)
                    .Distinct(StringComparer.Ordinal)
                    .ToImmutableArray();
                externalDriftedPortableItems = artifact.MissingPrerequisites
                    .Where(IsDrift)
                    .Select(item => item.PortableIdentity)
                    .Where(IsPortableIdentity)
                    .Distinct(StringComparer.Ordinal)
                    .ToImmutableArray();
            }
            externalNextGate = DetermineNextGate(artifact, hasFreshContext);
        }

        RaiseExternalProjection();
    }

    private static bool IsDrift(ExternalHeadPartInstallPrerequisite item) =>
        item.DiagnosticCode.Contains("drift", StringComparison.OrdinalIgnoreCase) ||
        item.ExpectedSha256 is { } expected && item.CurrentSha256 is { } current &&
        expected != current;

    private static bool IsPortableIdentity(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !Path.IsPathRooted(value) &&
        !value.Contains(':', StringComparison.Ordinal) &&
        !value.Contains('\\', StringComparison.Ordinal);

    private string DetermineNextGate(
        ExternalHeadPartInstallVerificationArtifact artifact,
        bool hasFreshContext)
    {
        if (!completeReviewedIntake)
            return "Review a complete enabled game intake before analyzing.";
        if (!hasFreshContext)
            return "Analyze again with the current reviewed game intake.";
        if (externalDriftedPortableItems.Length > 0)
            return "Restore the expected external dependency bytes, then analyze again.";
        if (externalMissingPortableItems.Length > 0)
            return "Provide or enable the listed external dependency, then analyze again.";
        if (artifact.CurrentInstallDependencyState !=
                ExternalInstallDependencyState.Verified ||
            !artifact.InstallReady)
            return "Verify the external dependency in the reviewed game workspace.";
        return "Static Finish Core write is ready; runtime and visual review remain required.";
    }

    private void RaiseExternalProjection()
    {
        OnPropertyChanged(nameof(ExternalProviderName));
        OnPropertyChanged(nameof(ExternalProviderNameDisplay));
        OnPropertyChanged(nameof(ExternalProviderExpectedSha256));
        OnPropertyChanged(nameof(ExternalProviderCurrentSha256));
        OnPropertyChanged(nameof(ExternalProviderEnabled));
        OnPropertyChanged(nameof(ExternalProviderExpectedHash));
        OnPropertyChanged(nameof(ExternalProviderCurrentHash));
        OnPropertyChanged(nameof(ExternalProviderEnabledState));
        OnPropertyChanged(nameof(ExternalDescriptorId));
        OnPropertyChanged(nameof(ExternalDescriptorIdDisplay));
        OnPropertyChanged(nameof(ExternalMissingPortableItems));
        OnPropertyChanged(nameof(ExternalDriftedPortableItems));
        OnPropertyChanged(nameof(ExternalMissingPortableItemsText));
        OnPropertyChanged(nameof(ExternalDriftedPortableItemsText));
        OnPropertyChanged(nameof(ExternalInstallDependencyState));
        OnPropertyChanged(nameof(ExternalInstallReady));
        OnPropertyChanged(nameof(InstallReady));
        OnPropertyChanged(nameof(ExternalHistoricalSnapshot));
        OnPropertyChanged(nameof(ExternalNextGate));
        OnPropertyChanged(nameof(NextGate));
        OnPropertyChanged(nameof(ExternalNextAction));
        OnPropertyChanged(nameof(CanApply));
    }
}

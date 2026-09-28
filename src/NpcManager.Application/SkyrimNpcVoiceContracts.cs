using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Document schema identifiers for the custom-voice authoring stages.</summary>
public static class SkyrimNpcVoiceSchemas
{
    public const string Services = "npc.voice-services.v1";
    public const string Sample = "npc.voice-sample.v1";
    public const string Synthesis = "npc.voice-synthesis.v1";
}

/// <summary>Typed diagnostic codes shared by the voice discovery, import, and synthesis stages.</summary>
public static class SkyrimNpcVoiceDiagnosticCodes
{
    public const string ServiceNone = "voice-service-none";
    public const string ServiceUnreachable = "voice-service-unreachable";
    public const string ServiceUnsupportedApi = "voice-service-unsupported-api";
    public const string ServiceStorageRefused = "voice-service-storage-refused";
    public const string ServiceBusyTimeout = "voice-service-busy-timeout";
    public const string ServiceRequestRejected = "voice-service-request-rejected";
    public const string ServiceError = "voice-service-error";
    public const string ServiceAmbiguous = "voice-service-ambiguous";
    public const string AudioEmpty = "voice-audio-empty";
    public const string AudioShape = "voice-audio-shape";
    public const string Cancelled = "voice-cancelled";
    public const string SampleInvalid = "voice-sample-invalid";
    public const string SampleSilent = "voice-sample-silent";
    public const string SampleTooShort = "voice-sample-too-short";
    public const string SampleTooLong = "voice-sample-too-long";
    public const string SamplePathRefused = "voice-sample-path-refused";
    public const string OutputExists = "voice-output-exists";
    public const string DocumentInvalid = "voice-document-invalid";
    public const string DocumentHashMismatch = "voice-document-hash-mismatch";
    public const string ServerSetupInvalid = "voice-server-setup-invalid";
    public const string ServerSetupUnavailable = "voice-server-setup-unavailable";
}

/// <summary>
/// One probed service. <see cref="Source"/> is <c>override</c>, <c>config</c>, or
/// <c>default</c>. <see cref="StorageVerdict"/> is <c>safe</c>, <c>refused</c>, or
/// <c>unknown</c>; a refused or unknown verdict blocks synthesis.
/// </summary>
public sealed record SkyrimVoiceServiceDescriptor(
    string Endpoint,
    string Source,
    bool Compatible,
    string? ApiTitle,
    string? ApiVersion,
    ImmutableArray<string> Models,
    ImmutableArray<string> Languages,
    int SpeakerCount,
    string? SpeakerFolder,
    string? OutputFolder,
    string? ModelFolder,
    string? SettingsJson,
    string StorageVerdict,
    string? StorageDetail,
    string? Fingerprint,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public string Platform { get; init; } = "wsl";
}

public sealed record SkyrimVoiceServiceInventory(
    string Schema,
    string? SelectedEndpoint,
    ImmutableArray<SkyrimVoiceServiceDescriptor> Services,
    ImmutableArray<Diagnostic> Diagnostics,
    string ProbedUtc)
{
    public bool RequestTextLoggedByService { get; } = true;
}

public sealed record SkyrimVoiceServerSetup(
    string Endpoint,
    string Platform,
    string ServerFolder,
    string OutputFolder);

public sealed record SkyrimVoiceServerSetupResult(
    bool Succeeded,
    SkyrimVoiceServerSetup? Settings,
    SkyrimVoiceServiceInventory? Inventory,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>Measured properties of one decoded WAV stream.</summary>
public sealed record SkyrimVoiceSampleAudio(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    string Format,
    long SampleFrames,
    double DurationSeconds,
    double PeakDbfs,
    double RmsDbfs);

/// <summary>
/// Hash-bound binding between one user-supplied reference sample and one NPC
/// identity. Paths are absolute K-local paths; the original is never modified.
/// </summary>
public sealed record SkyrimVoiceSampleAuthority(
    string Schema,
    PluginName Plugin,
    FormId FormId,
    EditorId? EditorId,
    string VoicePrefix,
    string OriginalPath,
    Sha256Hash OriginalSha256,
    SkyrimVoiceSampleAudio Original,
    string NormalizedPath,
    Sha256Hash NormalizedSha256,
    SkyrimVoiceSampleAudio Normalized,
    string CreatedUtc);

public sealed record SkyrimVoiceSampleImportRequest(
    WorkspacePath Sample,
    PluginName Plugin,
    FormId FormId,
    EditorId? EditorId,
    string VoicePrefix,
    WorkspacePath OutputRoot);

public sealed record SkyrimVoiceSampleImportResult(
    bool Imported,
    SkyrimVoiceSampleAuthority? Authority,
    WorkspacePath? AuthorityPath,
    Sha256Hash? AuthoritySha256,
    ImmutableArray<Diagnostic> Diagnostics);

public enum SkyrimVoiceLineStatus
{
    Pending,
    Succeeded,
    Failed,
    Skipped
}

/// <summary>Per-line synthesis record; <see cref="OutputFile"/> is relative to the synthesis output root.</summary>
public sealed record SkyrimVoiceSynthesisLine(
    string LineId,
    string Text,
    string Language,
    Sha256Hash TextSha256,
    SkyrimVoiceLineStatus Status,
    string? OutputFile,
    Sha256Hash? OutputSha256,
    double? DurationSeconds,
    int? SampleRate,
    double? ElapsedSeconds,
    string? FailureCode,
    string? FailureMessage);

/// <summary>
/// Resumable synthesis ledger. A line is reused only when its text hash, language,
/// sample hash, settings hash, and output hash all still match; otherwise it is
/// regenerated. Audio bytes are never expected to be byte-identical across runs.
/// </summary>
public sealed record SkyrimVoiceSynthesisManifest(
    string Schema,
    string DialogueManifestPath,
    Sha256Hash DialogueManifestSha256,
    string SampleAuthorityPath,
    Sha256Hash SampleAuthoritySha256,
    Sha256Hash SampleSha256,
    string Endpoint,
    string BackendIdentity,
    Sha256Hash SettingsSha256,
    string SpeakerReference,
    ImmutableArray<SkyrimVoiceSynthesisLine> Lines,
    string UpdatedUtc);

public sealed record SkyrimVoiceSynthesisRequest(
    WorkspacePath Manifest,
    Sha256Hash ManifestSha256,
    WorkspacePath SampleAuthority,
    Sha256Hash SampleAuthoritySha256,
    WorkspacePath OutputRoot,
    string? Endpoint,
    string? Language,
    int? MaxLines,
    bool Resume);

public sealed record SkyrimVoiceSynthesisResult(
    bool Completed,
    int Succeeded,
    int Failed,
    int Skipped,
    int Pending,
    WorkspacePath? ManifestPath,
    Sha256Hash? ManifestSha256,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Application seam shared by the CLI and the desktop voice panel. Discovery is
/// read-only; import never modifies the original sample; synthesis performs one
/// in-flight request at a time against an already running local service and
/// never administers that service.
/// </summary>
public interface ISkyrimNpcVoiceService
{
    ValueTask<SkyrimVoiceServerSetupResult> LoadServerSetupAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(UnavailableServerSetup());

    ValueTask<SkyrimVoiceServerSetupResult> CheckServerSetupAsync(
        SkyrimVoiceServerSetup setup,
        CancellationToken cancellationToken) => ValueTask.FromResult(UnavailableServerSetup());

    ValueTask<SkyrimVoiceServerSetupResult> SaveServerSetupAsync(
        SkyrimVoiceServerSetup setup,
        CancellationToken cancellationToken) => ValueTask.FromResult(UnavailableServerSetup());

    ValueTask<SkyrimVoiceServiceInventory> DiscoverAsync(
        string? endpointOverride,
        int timeoutSeconds,
        WorkspacePath? outputPath,
        CancellationToken cancellationToken);

    ValueTask<SkyrimVoiceSampleImportResult> ImportSampleAsync(
        SkyrimVoiceSampleImportRequest request,
        CancellationToken cancellationToken);

    ValueTask<SkyrimVoiceSynthesisResult> SynthesizeAsync(
        SkyrimVoiceSynthesisRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken);

    private static SkyrimVoiceServerSetupResult UnavailableServerSetup() => new(
        false,
        null,
        null,
        [new Diagnostic(
            SkyrimNpcVoiceDiagnosticCodes.ServerSetupUnavailable,
            DiagnosticSeverity.Error,
            "Voice server setup is unavailable in this service implementation.")]);
}

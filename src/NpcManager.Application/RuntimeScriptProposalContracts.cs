using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum RuntimeScriptPropertyType
{
    BoolValue,
    IntValue,
    FloatValue,
    StringValue,
    BoolArray,
    IntArray,
    FloatArray,
    StringArray
}

/// <summary>A JSON value that has already been checked against a Papyrus property type.</summary>
public sealed record RuntimeScriptInputProperty(string Name, RuntimeScriptPropertyType Type, JsonElement Value);

public sealed record RuntimeScriptObjectReference(string Name, FormReference Reference);

public sealed record RuntimeScriptFragment(int Index, int StartInstruction, int EndInstruction);

public sealed record RuntimeScriptProposalRequest(
    GameEdition Edition,
    FormId NpcFormId,
    string ScriptName,
    ImmutableArray<RuntimeScriptInputProperty> Properties,
    ImmutableArray<RuntimeScriptObjectReference> ObjectReferences,
    ImmutableArray<RuntimeScriptFragment> Fragments,
    WorkspacePath Output,
    WorkspacePath? SourcePlugin = null);

public sealed record RuntimeScriptPropertyArtifact(string Name, string Type, JsonElement Value);

public sealed record RuntimeScriptObjectReferenceArtifact(string Name, string Reference);

public sealed record RuntimeScriptFragmentArtifact(int Index, int StartInstruction, int EndInstruction);

public sealed record RuntimeScriptProposalArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string NpcFormId,
    string ScriptName,
    ImmutableArray<RuntimeScriptPropertyArtifact> Properties,
    ImmutableArray<RuntimeScriptObjectReferenceArtifact> ObjectReferences,
    ImmutableArray<RuntimeScriptFragmentArtifact> Fragments,
    string EmitterSource,
    string EmitterSourceSha256,
    bool BinaryMutation,
    bool NoUnrelatedRecords,
    string? SourcePlugin = null,
    string? InputSha256 = null);

public sealed record RuntimeScriptProposalResult(
    bool Written,
    RuntimeScriptProposalArtifact? Artifact,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRuntimeScriptProposalService
{
    ValueTask<RuntimeScriptProposalResult> ProposeAsync(RuntimeScriptProposalRequest request, CancellationToken cancellationToken);
}

public sealed record RuntimeScriptBuildRequest(GameEdition Edition, WorkspacePath SourceRoot, WorkspacePath Output);

public sealed record RuntimeScriptApiProperty(string Name, string Type);

public sealed record RuntimeScriptBuildArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string ScriptName,
    string SourcePath,
    string SourceSha256,
    string PexPath,
    string PexSha256,
    string PexInspectionPath,
    string PexInspectionSha256,
    string CompilerManifestPath,
    string CompilerManifestSha256,
    string CompilationMode,
    string FlagsStatus,
    ImmutableArray<string> ApiDependencies,
    ImmutableArray<RuntimeScriptApiProperty> Properties,
    bool ValidPex,
    bool BinaryMutation,
    bool RuntimeProof);

public sealed record RuntimeScriptBuildResult(
    bool Written,
    RuntimeScriptBuildArtifact? Artifact,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRuntimeScriptBuildService
{
    ValueTask<RuntimeScriptBuildResult> BuildAsync(RuntimeScriptBuildRequest request, CancellationToken cancellationToken);
}

public sealed record RuntimeScriptPackageRequest(GameEdition Edition, WorkspacePath SourceRoot, WorkspacePath OutputRoot);

public sealed record RuntimeScriptPackageArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string ScriptName,
    string SourceRoot,
    string SourcePscPath,
    string SourcePscSha256,
    string SourcePexPath,
    string SourcePexSha256,
    string InstalledRelativePath,
    string InstalledPath,
    string InstalledSha256,
    long InstalledByteLength,
    bool BinaryMutation,
    bool RuntimeProof);

public sealed record RuntimeScriptPackageResult(
    bool Written,
    WorkspacePath OutputRoot,
    RuntimeScriptPackageArtifact? Artifact,
    Sha256Hash? ManifestSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRuntimeScriptPackageService
{
    ValueTask<RuntimeScriptPackageResult> PackageAsync(RuntimeScriptPackageRequest request,
        CancellationToken cancellationToken);
}

public sealed record RuntimeScriptDeployRequest(GameEdition Edition, WorkspacePath PackageManifest,
    WorkspacePath DataRoot);

public sealed record RuntimeScriptDeployResult(
    bool Installed,
    bool AlreadyPresent,
    WorkspacePath DataRoot,
    WorkspacePath Output,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRuntimeScriptDeployService
{
    ValueTask<RuntimeScriptDeployResult> DeployAsync(RuntimeScriptDeployRequest request,
        CancellationToken cancellationToken);
}

public sealed record RuntimeScriptVmadInspectRequest(
    GameEdition Edition,
    WorkspacePath Plugin,
    FormId NpcFormId,
    string ScriptName);

public sealed record RuntimeScriptVmadInspectArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string Plugin,
    string PluginSha256,
    string NpcFormId,
    string ScriptName,
    ImmutableArray<string> PropertyNames,
    int PropertyCount,
    bool NoWrite,
    bool RuntimeProof);

public sealed record RuntimeScriptVmadInspectResult(
    bool Resolved,
    RuntimeScriptVmadInspectArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRuntimeScriptVmadInspectService
{
    ValueTask<RuntimeScriptVmadInspectResult> InspectAsync(
        RuntimeScriptVmadInspectRequest request, CancellationToken cancellationToken);
}

public sealed record RuntimeScriptBinaryWriteRequest(GameEdition Edition, WorkspacePath SourcePlugin,
    WorkspacePath Proposal, WorkspacePath Output);

public sealed record RuntimeScriptBinaryWriteResult(bool Written, WorkspacePath Output, FormId? TargetFormId,
    Sha256Hash? OutputSha256, ImmutableArray<Diagnostic> Diagnostics);

public interface IRuntimeScriptBinaryWriteService
{
    ValueTask<RuntimeScriptBinaryWriteResult> WriteAsync(RuntimeScriptBinaryWriteRequest request,
        CancellationToken cancellationToken);
}

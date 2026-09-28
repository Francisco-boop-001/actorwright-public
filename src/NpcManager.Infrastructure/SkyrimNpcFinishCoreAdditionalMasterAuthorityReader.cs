using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

/// <summary>
/// Revalidates caller-declared additional-master bindings and derives their
/// dependencies from the retained bytes of the admitted file.
/// </summary>
public sealed class SkyrimNpcFinishCoreAdditionalMasterAuthorityReader
{
    private const long MaximumPluginBytes = 256L * 1024 * 1024;

    private readonly WorkspacePath workspaceRoot;
    private readonly IWorkspacePolicy policy;
    private readonly FaceGeomHairRegionsPinnedFileSystem fileSystem;
    private readonly Action<WorkspacePath>? afterInitialRead;

    public SkyrimNpcFinishCoreAdditionalMasterAuthorityReader(
        WorkspacePath workspaceRoot,
        IWorkspacePolicy policy)
        : this(
            workspaceRoot,
            policy,
            new FaceGeomHairRegionsPinnedFileSystem(workspaceRoot))
    {
    }

    internal SkyrimNpcFinishCoreAdditionalMasterAuthorityReader(
        WorkspacePath workspaceRoot,
        IWorkspacePolicy policy,
        FaceGeomHairRegionsPinnedFileSystem fileSystem,
        Action<WorkspacePath>? afterInitialRead = null)
    {
        this.workspaceRoot = workspaceRoot;
        this.policy = policy;
        this.fileSystem = fileSystem;
        this.afterInitialRead = afterInitialRead;
    }

    public async ValueTask<SkyrimNpcFinishCoreAuthorityReadResult> ReadAsync(
        ImmutableArray<SkyrimNpcFinishCoreAdditionalMasterBinding> bindings,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ImmutableArray<SkyrimNpcFinishCoreAdditionalMasterBinding> rows =
            bindings.IsDefault ? ImmutableArray<SkyrimNpcFinishCoreAdditionalMasterBinding>.Empty : bindings;
        var plugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indexes = new HashSet<int>();

        foreach (SkyrimNpcFinishCoreAdditionalMasterBinding binding in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string plugin = binding.Plugin.Value ?? string.Empty;
            string path = binding.Path.Value ?? string.Empty;
            bool missing = string.IsNullOrWhiteSpace(plugin) ||
                string.IsNullOrWhiteSpace(path) ||
                string.IsNullOrWhiteSpace(binding.Sha256.Value) ||
                binding.ByteLength <= 0 ||
                binding.LoadOrderIndex < 0;
            if (missing)
            {
                diagnostics.Add(Error(
                    "finish-core-authority-identity-missing",
                    "Every additional-master authority binding must declare plugin, path, SHA-256, positive byte length, and non-negative load-order index."));
                continue;
            }

            if (!plugins.Add(plugin))
            {
                diagnostics.Add(Error(
                    "finish-core-authority-duplicate-plugin",
                    $"Additional-master authority contains duplicate plugin identity={plugin}."));
            }

            if (!paths.Add(path))
            {
                diagnostics.Add(Error(
                    "finish-core-authority-duplicate-path",
                    $"Additional-master authority contains duplicate path={path}."));
            }

            if (!indexes.Add(binding.LoadOrderIndex))
            {
                diagnostics.Add(Error(
                    "finish-core-authority-duplicate-index",
                    $"Additional-master authority contains duplicate load-order index={binding.LoadOrderIndex}."));
            }

            if (!string.Equals(
                    Path.GetFileName(path), plugin, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error(
                    "finish-core-authority-identity-mismatch",
                    $"Additional-master path identity does not match plugin identity={plugin}; expected={plugin}, observed={Path.GetFileName(path)}."));
            }

            diagnostics.AddRange(policy.EvaluateReadRoot(workspaceRoot, binding.Path));
        }

        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return Refused(diagnostics);

        var admitted = ImmutableArray.CreateBuilder<SkyrimNpcFinishCoreVerifiedAdditionalMaster>();
        foreach (SkyrimNpcFinishCoreAdditionalMasterBinding binding in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] initial;
            try
            {
                await using FaceGeomHairRegionsPinnedReadFile file =
                    fileSystem.OpenRead(binding.Path, "Finish Core additional master");
                initial = await file.ReadExactAsync(
                    MaximumPluginBytes,
                    cancellationToken);
                if (initial.LongLength != binding.ByteLength)
                {
                    diagnostics.Add(Error(
                        "finish-core-authority-identity-mismatch",
                        $"Additional-master byte length changed: identity={binding.Plugin.Value}, expected={binding.ByteLength}, observed={initial.LongLength}."));
                    continue;
                }

                Sha256Hash observedHash = Hash(initial);
                if (!string.Equals(
                        observedHash.Value,
                        binding.Sha256.Value,
                        StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(Error(
                        "finish-core-authority-identity-mismatch",
                        $"Additional-master bytes do not match authority: identity={binding.Plugin.Value}, expected={binding.Sha256.Value}, observed={observedHash.Value}."));
                    continue;
                }

                try
                {
                    afterInitialRead?.Invoke(binding.Path);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add(Error(
                        "finish-core-authority-revalidation",
                        $"Additional-master revalidation hook failed: identity={binding.Plugin.Value}, observed={exception.Message}."));
                    continue;
                }

                byte[] revalidated = await file.ReadExactAsync(
                    MaximumPluginBytes,
                    cancellationToken);
                Sha256Hash retainedHash = new(file.ComputeSha256());
                if (!initial.AsSpan().SequenceEqual(revalidated) ||
                    !string.Equals(
                        retainedHash.Value,
                        observedHash.Value,
                        StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(Error(
                        "finish-core-authority-revalidation",
                        $"Additional-master bytes or physical identity changed after initial admission: identity={binding.Plugin.Value}, expected={observedHash.Value}, observed={retainedHash.Value}."));
                    continue;
                }

                ImmutableArray<PluginName> dependencies =
                    BethesdaSkyrimNpcFinishCoreSourceReader
                        .ReadMasterDependencies(initial);

                admitted.Add(new SkyrimNpcFinishCoreVerifiedAdditionalMaster(
                    binding.Plugin,
                    binding.Path,
                    observedHash,
                    initial.LongLength,
                    binding.LoadOrderIndex,
                    dependencies));
            }
            catch (InvalidDataException exception)
            {
                diagnostics.Add(Error(
                    "finish-core-authority-tes4-invalid",
                    $"Additional-master retained bytes do not contain a valid TES4 master header: identity={binding.Plugin.Value}, observed={exception.Message}."));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                    EndOfStreamException)
            {
                diagnostics.Add(Error(
                    "finish-core-authority-revalidation",
                    $"Additional-master bytes could not be retained and revalidated: identity={binding.Plugin.Value}, observed={exception.Message}."));
            }
        }

        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return Refused(diagnostics);

        return new SkyrimNpcFinishCoreAuthorityReadResult(
            true,
            admitted.ToImmutable(),
            ImmutableArray<Diagnostic>.Empty);
    }

    private static SkyrimNpcFinishCoreAuthorityReadResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, ImmutableArray<SkyrimNpcFinishCoreVerifiedAdditionalMaster>.Empty,
            diagnostics.ToImmutable());

    private static Sha256Hash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}

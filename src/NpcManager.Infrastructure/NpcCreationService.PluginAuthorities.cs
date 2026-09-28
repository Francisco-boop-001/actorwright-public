using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class NpcCreationService
{
    private void ValidateMasterProviders(
        NpcCreationRequest request,
        ImmutableArray<PluginName> directMasters,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var dataRoot = new WorkspacePath(Path.GetDirectoryName(request.TemplatePlugin.Value)!);
        var authorities = request.PluginAuthorities.IsDefault
            ? ImmutableArray<NpcCreationPluginAuthority>.Empty
            : request.PluginAuthorities;
        var explicitByPlugin = new Dictionary<string, NpcCreationPluginAuthority>(
            StringComparer.OrdinalIgnoreCase);

        foreach (NpcCreationPluginAuthority authority in authorities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!explicitByPlugin.TryAdd(authority.Plugin.Value, authority))
            {
                diagnostics.Add(Error("npc-create-plugin-authority-duplicate",
                    $"Plugin authority '{authority.Plugin}' is declared more than once."));
                continue;
            }
            if (!string.Equals(Path.GetFileName(authority.PluginPath.Value),
                    authority.Plugin.Value, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error("npc-create-plugin-authority-name-mismatch",
                    $"Plugin authority '{authority.Plugin}' does not match filename " +
                    $"'{Path.GetFileName(authority.PluginPath.Value)}'."));
            }
            AddUnsafePathDiagnostic(authority.PluginPath.Value,
                $"plugin authority {authority.Plugin}", diagnostics);
            if (!authority.PluginPath.IsUnder(labRoot))
            {
                diagnostics.Add(Error("npc-create-plugin-authority-outside-lab",
                    $"Plugin authority '{authority.Plugin}' must remain under the K-only lab root."));
                continue;
            }
            if (!File.Exists(authority.PluginPath.Value))
            {
                diagnostics.Add(Error("npc-create-plugin-authority-missing",
                    $"Plugin authority '{authority.Plugin}' is missing at its reviewed path."));
                continue;
            }
            AddReparseDiagnostic(authority.PluginPath.Value,
                $"plugin-authority-{authority.Plugin.Value}", diagnostics);
            try
            {
                Sha256Hash actualHash = HashFile(authority.PluginPath.Value);
                if (actualHash != authority.ExpectedSha256)
                {
                    diagnostics.Add(Error("npc-create-plugin-authority-hash-mismatch",
                        $"Plugin authority '{authority.Plugin}' hash {actualHash} does not match " +
                        $"the reviewed hash {authority.ExpectedSha256}."));
                }
            }
            catch (Exception exception) when (exception is IOException or
                                                UnauthorizedAccessException)
            {
                diagnostics.Add(Error("npc-create-plugin-authority-hash-failed",
                    $"Plugin authority '{authority.Plugin}' could not be hashed: {exception.Message}"));
            }
        }

        var pending = new Queue<PluginName>(directMasters);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PluginName master = pending.Dequeue();
            if (!visited.Add(master.Value)) continue;
            if (visited.Count > 256)
            {
                diagnostics.Add(Error("npc-create-master-closure-limit",
                    "The template master closure exceeds the 256-plugin safety bound."));
                return;
            }

            bool isExplicit = explicitByPlugin.TryGetValue(
                master.Value, out NpcCreationPluginAuthority? authority);
            WorkspacePath provider = isExplicit
                ? authority!.PluginPath
                : new WorkspacePath(Path.Combine(dataRoot.Value, master.Value));
            if (!provider.IsUnder(labRoot) || (!isExplicit && !provider.IsUnder(dataRoot)))
            {
                diagnostics.Add(Error("npc-create-master-provider-outside-lab",
                    isExplicit
                        ? $"Explicit master provider {master} does not resolve under the K-only lab root."
                        : $"Master provider {master} does not resolve under the copied K-local Data root."));
                continue;
            }
            if (!File.Exists(provider.Value))
            {
                diagnostics.Add(Error("npc-create-master-provider-missing",
                    isExplicit
                        ? $"Explicit master provider {master} is missing at its reviewed path."
                        : $"Master provider {master} is missing from the copied K-local Data root."));
                continue;
            }
            AddReparseDiagnostic(provider.Value, $"master-provider-{master.Value}", diagnostics);
            try
            {
                foreach (PluginName transitive in ReadHeaderMasters(provider.Value))
                    pending.Enqueue(transitive);
            }
            catch (Exception exception) when (exception is IOException or
                                                UnauthorizedAccessException or
                                                InvalidDataException or ArgumentException)
            {
                diagnostics.Add(Error("npc-create-master-provider-invalid",
                    $"Master provider {master} could not be validated: {exception.Message}"));
            }
        }
    }

    private static ImmutableArray<PluginName> ReadHeaderMasters(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.SequentialScan);
        Span<byte> header = stackalloc byte[24];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("TES4"u8))
            throw new InvalidDataException("Provider does not begin with TES4.");
        uint payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        if (payloadSize == 0 || payloadSize > MaximumHeaderBytes)
            throw new InvalidDataException(
                "Provider TES4 header size is outside the accepted bound.");
        var payload = new byte[payloadSize];
        stream.ReadExactly(payload);
        var masters = ImmutableArray.CreateBuilder<PluginName>();
        int position = 0;
        while (position < payload.Length)
        {
            if (position + 6 > payload.Length)
                throw new InvalidDataException("Provider subrecord header is truncated.");
            string signature = Encoding.ASCII.GetString(payload, position, 4);
            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(
                payload.AsSpan(position + 4, 2));
            position += 6;
            if (position + size > payload.Length)
                throw new InvalidDataException("Provider subrecord exceeds TES4 header.");
            if (signature == "MAST")
            {
                ReadOnlySpan<byte> data = payload.AsSpan(position, size);
                if (data.IsEmpty || data[^1] != 0)
                    throw new InvalidDataException("Provider MAST is not NUL terminated.");
                masters.Add(new PluginName(Encoding.Latin1.GetString(data[..^1])));
            }
            position += size;
        }
        return masters.ToImmutable();
    }
}

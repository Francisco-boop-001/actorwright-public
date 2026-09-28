using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using NpcManager.Domain;

namespace NpcManager.Rendering;

public readonly record struct EmbeddedBlenderScriptId
{
    public EmbeddedBlenderScriptId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = Path.GetFileNameWithoutExtension(value);
    }

    public string Value { get; }

    public static implicit operator EmbeddedBlenderScriptId(string value) => new(value);

    public static implicit operator EmbeddedBlenderScriptId(WorkspacePath path) => new(path.Value);
}

public sealed record EmbeddedBlenderScript(
    string Id,
    string EntryFileName,
    ImmutableArray<byte> SourceBytes,
    Sha256Hash Sha256,
    ImmutableDictionary<string, ImmutableArray<byte>> HelperModules);

public static class EmbeddedBlenderScriptBundle
{
    private const string ResourcePrefix = "Actorwright.Rendering.Scripts.";
    private static readonly ImmutableDictionary<string, ImmutableArray<string>> Admitted =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["export_facegeom_nif"] = ["nif_geometry_readback"],
            ["export_preview_nif"] = ["hair_zap", "nif_geometry_readback"],
            ["hair_zap"] = [],
            ["render_npc_preview_bundle"] = [],
            ["render_preview_scene"] = ["hair_zap"]
        }.ToImmutableDictionary(StringComparer.Ordinal);

    public static EmbeddedBlenderScript Load(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!Admitted.TryGetValue(id, out ImmutableArray<string> helpers))
            throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown Blender script ID.");
        ImmutableArray<byte> source = Read(id);
        ImmutableDictionary<string, ImmutableArray<byte>> modules = helpers
            .ToImmutableDictionary(helper => helper, Read, StringComparer.Ordinal);
        return new EmbeddedBlenderScript(
            id,
            $"{id}.py",
            source,
            new Sha256Hash(Convert.ToHexString(SHA256.HashData(source.AsSpan()))),
            modules);
    }

    private static ImmutableArray<byte> Read(string id)
    {
        Assembly assembly = typeof(EmbeddedBlenderScriptBundle).Assembly;
        string name = $"{ResourcePrefix}{id}.py";
        using Stream stream = assembly.GetManifestResourceStream(name) ??
            throw new InvalidOperationException($"Embedded Blender script is missing: {name}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return [.. memory.ToArray()];
    }
}

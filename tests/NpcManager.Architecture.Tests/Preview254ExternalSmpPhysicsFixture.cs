using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Architecture.Tests;

internal enum Preview254ExternalSmpPhysicsFixtureMode
{
    DirectLocator = 0,
    DefaultBbp = 1,
    DirectAndDefaultBbp = 2,
    AmbiguousDefaultBbp = 3
}

internal sealed record Preview254ExternalSmpPhysicsFixture(
    WorkspacePath ScratchRoot,
    WorkspacePath DataRoot,
    FormReference MemberForm,
    AssetPath ModelNif,
    AssetPath Tri,
    AssetPath DiffuseTexture,
    AssetPath PhysicsXml,
    AssetPath ColliderNif,
    AssetPath DefaultBbpXml,
    ImmutableArray<ExternalHeadPartAssetDependency> ReopenedAssets)
{
    internal WorkspacePath LabRoot { get; init; } =
        new(@"K:\Actorwright");

    internal void RestoreDefaultBbp() =>
        Preview254ExternalSmpPhysicsFixtureFactory.WriteOrdinaryFixtureFile(
            DataRoot,
            DefaultBbpXml,
            Encoding.UTF8.GetBytes(
                Preview254ExternalSmpPhysicsFixtureFactory.BuildDefaultBbpXml(
                    PhysicsXml)),
            LabRoot);

    internal void RestorePhysicsXml() =>
        Preview254ExternalSmpPhysicsFixtureFactory.WriteOrdinaryFixtureFile(
            DataRoot,
            PhysicsXml,
            Encoding.UTF8.GetBytes(
                Preview254ExternalSmpPhysicsFixtureFactory.BuildPhysicsXml(
                    ColliderNif)),
            LabRoot);

    private string Physical(AssetPath path) =>
        Path.Combine(DataRoot.Value,
            path.Value.Replace('/', Path.DirectorySeparatorChar));
}

internal interface IPreview254ExternalSmpPhysicsFixtureFactory
{
    ValueTask<Preview254ExternalSmpPhysicsFixture> CreateAsync(
        WorkspacePath scratchRoot,
        FormReference memberForm,
        AssetPath modelNif,
        Preview254ExternalSmpPhysicsFixtureMode mode,
        CancellationToken cancellationToken);
}

internal sealed class Preview254ExternalSmpPhysicsFixtureFactory :
    IPreview254ExternalSmpPhysicsFixtureFactory
{
    private readonly WorkspacePath _labRoot;

    internal Preview254ExternalSmpPhysicsFixtureFactory()
        : this(new WorkspacePath(@"K:\Actorwright"))
    {
    }

    internal Preview254ExternalSmpPhysicsFixtureFactory(WorkspacePath labRoot)
    {
        _labRoot = labRoot;
    }

    private static readonly PluginName ProviderPlugin =
        new("OrchidAdornment.esp");
    private static readonly Sha256Hash ProviderSha256 =
        Hash("provider-plugin");
    private const string DefaultBbpRelative =
        "SKSE/Plugins/hdtSkinnedMeshConfigs/defaultBBPs.xml";
    private const string DirectPhysicsRelative =
        "SKSE/Plugins/hdtSkinnedMeshConfigs/hair/direct.xml";
    private const string FallbackPhysicsRelative =
        "SKSE/Plugins/hdtSkinnedMeshConfigs/hair/example.xml";
    private const string FallbackAlternateRelative =
        "SKSE/Plugins/hdtSkinnedMeshConfigs/hair/fallback.xml";
    private const string ColliderRelative =
        "meshes/actors/character/character assets/hair/actorwright_fixture_collider.nif";
    private const string TriRelative =
        "meshes/actors/character/character assets/hair/actorwright_fixture_hair.tri";
    private const string DiffuseRelative =
        "textures/actors/character/hair/actorwright_fixture_hair.dds";

    public async ValueTask<Preview254ExternalSmpPhysicsFixture> CreateAsync(
        WorkspacePath scratchRoot,
        FormReference memberForm,
        AssetPath modelNif,
        Preview254ExternalSmpPhysicsFixtureMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateScratchRoot(scratchRoot, _labRoot);
        WorkspacePath dataRoot = new(Path.Combine(scratchRoot.Value, "Data"));

        AssetPath defaultBbp = new(DefaultBbpRelative);
        AssetPath collider = new(ColliderRelative);
        AssetPath tri = new(TriRelative);
        AssetPath diffuse = new(DiffuseRelative);
        AssetPath physics = new(
            mode is Preview254ExternalSmpPhysicsFixtureMode.DirectLocator or
                Preview254ExternalSmpPhysicsFixtureMode.DirectAndDefaultBbp
                ? DirectPhysicsRelative
                : FallbackPhysicsRelative);
        AssetPath fallbackAlternate = new(FallbackAlternateRelative);

        bool direct = mode is Preview254ExternalSmpPhysicsFixtureMode.DirectLocator or
            Preview254ExternalSmpPhysicsFixtureMode.DirectAndDefaultBbp;
        var plannedAssets = new List<AssetPath>
        {
            modelNif,
            tri,
            diffuse,
            collider,
            physics
        };
        if (mode is Preview254ExternalSmpPhysicsFixtureMode.DirectAndDefaultBbp)
            plannedAssets.Add(fallbackAlternate);
        if (mode != Preview254ExternalSmpPhysicsFixtureMode.DirectLocator)
            plannedAssets.Add(defaultBbp);
        ValidateExistingFixturePaths(dataRoot, plannedAssets, _labRoot);
        Directory.CreateDirectory(dataRoot.Value);

        Write(dataRoot, modelNif, BuildProviderNif(
            direct ? physics.Value : null, tri.Value));
        Write(dataRoot, tri, Encoding.UTF8.GetBytes("fixture-tri"));
        Write(dataRoot, diffuse, Encoding.UTF8.GetBytes("fixture-diffuse"));
        Write(dataRoot, collider, Encoding.UTF8.GetBytes("fixture-collider"));
        Write(dataRoot, physics, Encoding.UTF8.GetBytes(
            BuildPhysicsXml(collider)));

        if (mode is Preview254ExternalSmpPhysicsFixtureMode.DirectAndDefaultBbp)
            Write(dataRoot, fallbackAlternate, Encoding.UTF8.GetBytes(
                BuildPhysicsXml(collider)));

        if (mode != Preview254ExternalSmpPhysicsFixtureMode.DirectLocator)
        {
            string mapping = mode ==
                Preview254ExternalSmpPhysicsFixtureMode.AmbiguousDefaultBbp
                ? BuildAmbiguousDefaultBbpXml(physics, fallbackAlternate)
                : BuildDefaultBbpXml(
                    mode == Preview254ExternalSmpPhysicsFixtureMode.DirectAndDefaultBbp
                        ? fallbackAlternate
                        : physics);
            Write(dataRoot, defaultBbp, Encoding.UTF8.GetBytes(mapping));
        }

        var reopened = ImmutableArray.CreateBuilder<ExternalHeadPartAssetDependency>();
        foreach (AssetPath asset in new[] { modelNif, tri, diffuse, physics, collider })
        {
            byte[] bytes = File.ReadAllBytes(Physical(dataRoot, asset));
            reopened.Add(new ExternalHeadPartAssetDependency(
                asset,
                HashBytes(bytes),
                bytes.LongLength,
                ProviderPlugin,
                ProviderSha256,
                null));
        }
        if (File.Exists(Physical(dataRoot, defaultBbp)))
        {
            byte[] bytes = File.ReadAllBytes(Physical(dataRoot, defaultBbp));
            reopened.Add(new ExternalHeadPartAssetDependency(
                defaultBbp,
                HashBytes(bytes),
                bytes.LongLength,
                ProviderPlugin,
                ProviderSha256,
                null));
        }

        await Task.CompletedTask;
        return new Preview254ExternalSmpPhysicsFixture(
            scratchRoot,
            dataRoot,
            memberForm,
            modelNif,
            tri,
            diffuse,
            physics,
            collider,
            defaultBbp,
            reopened.ToImmutable())
        {
            LabRoot = _labRoot
        };
    }

    internal static string BuildDefaultBbpXml(AssetPath physicsXml) =>
        $"<defaultBBPs><map shape=\"HairPhysicsShape\" file=\"{physicsXml.Value}\" />" +
        $"<map shape=\"HairCollisionShape\" file=\"{physicsXml.Value}\" /></defaultBBPs>";

    private static string BuildAmbiguousDefaultBbpXml(
        AssetPath physicsXml,
        AssetPath alternateXml) =>
        $"<defaultBBPs><map shape=\"HairPhysicsShape\" file=\"{physicsXml.Value}\" />" +
        $"<map shape=\"HairPhysicsShape\" file=\"{alternateXml.Value}\" />" +
        $"<map shape=\"HairCollisionShape\" file=\"{physicsXml.Value}\" /></defaultBBPs>";

    internal static string BuildPhysicsXml(AssetPath collider) =>
        "<hdtSmp><bones><bone name=\"NPC Head [Head]\" /></bones>" +
        $"<colliders><collider path=\"{collider.Value}\" /></colliders></hdtSmp>";

    internal static byte[] BuildProviderNifForTests(
        string? directPhysicsXml,
        string triPath,
        string? secondTriPath = null,
        IReadOnlyList<string>? textureSlots = null) =>
        BuildProviderNif(directPhysicsXml, triPath, secondTriPath,
            textureSlots);

    private static byte[] BuildProviderNif(
        string? directPhysicsXml,
        string triPath,
        string? secondTriPath = null,
        IReadOnlyList<string>? textureSlots = null)
    {
        var strings = new List<string>
        {
            "Root",
            "HairPhysicsShape",
            "HairCollisionShape",
            "BODYTRI",
            triPath,
            secondTriPath ?? triPath
        };
        if (directPhysicsXml is not null)
        {
            strings.Add("HDT Skinned Mesh Physics Object");
            strings.Add(directPhysicsXml);
        }
        if (textureSlots is not null)
            strings.AddRange(textureSlots);

        int rootIndex = 0;
        int physicsShapeIndex = 1;
        int collisionShapeIndex = 2;
        int metadataIndex = 3;
        int textureBlockIndex = metadataIndex + 2 +
            (directPhysicsXml is null ? 0 : 1);
        var rootExtras = ImmutableArray.CreateBuilder<int>();
        if (directPhysicsXml is not null)
            rootExtras.Add(metadataIndex + 2);
        if (textureSlots is not null && textureSlots.Count > 0)
            rootExtras.Add(textureBlockIndex);
        var blocks = new List<(ushort Type, byte[] Data)>
        {
            (0, BuildNodeBlock(
                NameIndex(strings, "Root"),
                rootExtras.ToImmutable(),
                [physicsShapeIndex, collisionShapeIndex])),
            (1, BuildShapeBlock(
                NameIndex(strings, "HairPhysicsShape"), metadataIndex)),
            (1, BuildShapeBlock(
                NameIndex(strings, "HairCollisionShape"), metadataIndex + 1)),
            (2, BuildMetadataBlock(
                NameIndex(strings, "BODYTRI"), NameIndex(strings, triPath))),
            (2, BuildMetadataBlock(
                NameIndex(strings, "BODYTRI"),
                NameIndex(strings, secondTriPath ?? triPath)))
        };
        if (directPhysicsXml is not null)
            blocks.Add((2, BuildMetadataBlock(
                NameIndex(strings, "HDT Skinned Mesh Physics Object"),
                NameIndex(strings, directPhysicsXml))));
        if (textureSlots is not null && textureSlots.Count > 0)
            blocks.Add((3, BuildTextureSetBlock(
                textureSlots.ToImmutableArray())));

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes(
            "Gamebryo File Format, Version 20.2.0.7\n"));
        writer.Write(0x14020007U);
        writer.Write((byte)1);
        writer.Write(12U);
        writer.Write((uint)blocks.Count);
        writer.Write(100U);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)(textureSlots is not null && textureSlots.Count > 0
            ? 4
            : 3));
        WriteSizedAscii(writer, "NiNode");
        WriteSizedAscii(writer, "BSTriShape");
        WriteSizedAscii(writer, "NiStringExtraData");
        if (textureSlots is not null && textureSlots.Count > 0)
            WriteSizedAscii(writer, "BSShaderTextureSet");
        foreach ((ushort type, _) in blocks)
            writer.Write(type);
        foreach ((_, byte[] data) in blocks)
            writer.Write((uint)data.Length);
        writer.Write((uint)strings.Count);
        writer.Write((uint)strings.Max(item => item.Length));
        foreach (string value in strings)
            WriteSizedAscii(writer, value);
        writer.Write(0U);
        foreach ((_, byte[] data) in blocks)
            writer.Write(data);
        writer.Write(1U);
        writer.Write(rootIndex);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildTextureSetBlock(
        IReadOnlyList<string> textureSlots)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write((uint)textureSlots.Count);
        foreach (string texture in textureSlots)
            WriteSizedAscii(writer, texture);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildNodeBlock(
        int nameIndex,
        ImmutableArray<int> extras,
        ImmutableArray<int> children)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write((uint)nameIndex);
        writer.Write((uint)extras.Length);
        foreach (int extra in extras)
            writer.Write(extra);
        writer.Write(-1);
        writer.Write(new byte[56]);
        writer.Write(-1);
        writer.Write((uint)children.Length);
        foreach (int child in children)
            writer.Write(child);
        writer.Write(0U);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildShapeBlock(int nameIndex, int metadataIndex)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write((uint)nameIndex);
        writer.Write(1U);
        writer.Write(metadataIndex);
        writer.Write(-1);
        writer.Write(new byte[56]);
        writer.Write(-1);
        writer.Write(new byte[16]);
        writer.Write(-1);
        writer.Write(-1);
        writer.Write(-1);
        writer.Write(new byte[16]);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildMetadataBlock(int nameIndex, int targetIndex)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write((uint)nameIndex);
        writer.Write((uint)targetIndex);
        writer.Flush();
        return stream.ToArray();
    }

    private static int NameIndex(List<string> strings, string value)
    {
        for (var index = 0; index < strings.Count; index++)
            if (string.Equals(strings[index], value, StringComparison.Ordinal))
                return index;
        throw new InvalidOperationException($"Fixture string was not found: {value}");
    }

    private static void WriteSizedAscii(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }

    private static void Write(
        WorkspacePath dataRoot,
        AssetPath path,
        byte[] bytes)
        => WriteOrdinaryFixtureFile(dataRoot, path, bytes);

    internal static void WriteOrdinaryFixtureFile(
        WorkspacePath dataRoot,
        AssetPath path,
        byte[] bytes,
        WorkspacePath? labRoot = null)
    {
        string physical = Physical(dataRoot, path);
        string boundary = labRoot?.Value ?? dataRoot.Value;
        ValidateExistingFixturePath(physical, boundary);
        Directory.CreateDirectory(Path.GetDirectoryName(physical)!);
        ValidateExistingFixturePath(physical, boundary);
        File.WriteAllBytes(physical, bytes);
        ValidateExistingFixturePath(physical, boundary);
    }

    private static string Physical(WorkspacePath dataRoot, AssetPath path) =>
        Path.Combine(dataRoot.Value,
            path.Value.Replace('/', Path.DirectorySeparatorChar));

    private static void ValidateScratchRoot(
        WorkspacePath scratchRoot,
        WorkspacePath labRoot)
    {
        if (scratchRoot == labRoot || !scratchRoot.IsUnder(labRoot))
            throw new InvalidOperationException(
                "Physics fixture scratch roots must remain below the K-local lab root.");

        string current = scratchRoot.Value;
        while (true)
        {
            if (Directory.Exists(current) || File.Exists(current))
            {
                FileAttributes attributes = File.GetAttributes(current);
                if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    attributes.HasFlag(FileAttributes.Device))
                    throw new InvalidOperationException(
                        $"Physics fixture scratch ancestry contains a reparse/device path: {current}");
            }

            if (string.Equals(current, labRoot.Value,
                    StringComparison.OrdinalIgnoreCase))
                return;
            string? parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Physics fixture scratch ancestry did not reach the K-local lab root.");
            current = parent;
        }
    }

    private static void ValidateExistingFixturePaths(
        WorkspacePath dataRoot,
        IEnumerable<AssetPath> assets,
        WorkspacePath labRoot)
    {
        ValidateExistingFixturePath(dataRoot.Value, labRoot.Value);
        foreach (AssetPath asset in assets)
        {
            string physical = Physical(dataRoot, asset);
            string? parent = Path.GetDirectoryName(physical);
            if (parent is not null)
                ValidateExistingFixturePath(parent, labRoot.Value);
        }
    }

    private static void ValidateExistingFixturePath(
        string candidate,
        string labRoot)
    {
        string current = Path.GetFullPath(candidate);
        string boundary = Path.GetFullPath(labRoot);
        while (true)
        {
            if (Directory.Exists(current) || File.Exists(current))
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & (FileAttributes.ReparsePoint |
                        FileAttributes.Device)) != 0)
                    throw new InvalidOperationException(
                        $"Physics fixture path '{current}' is a pre-existing reparse/device entry.");
            }

            if (string.Equals(current, boundary,
                    StringComparison.OrdinalIgnoreCase))
                return;
            string? parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Physics fixture path ancestry did not reach the configured lab root.");
            current = parent;
        }
    }

    private static Sha256Hash Hash(string value) =>
        HashBytes(Encoding.UTF8.GetBytes(value));

    private static Sha256Hash HashBytes(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));
}

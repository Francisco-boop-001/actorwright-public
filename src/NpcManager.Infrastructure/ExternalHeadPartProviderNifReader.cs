using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Separate, dependency-only parse profile for the exact external headpart
/// provider metadata used by the admitted Dint route. Both parser profiles
/// admit the bounded NiStringExtraData leaf; this reader supplies the separate
/// provider dependency and metadata semantics.
/// </summary>
public sealed class ExternalHeadPartProviderNifReader :
    IExternalHeadPartProviderNifReader
{
    private const int MaximumNifBytes = 64 * 1024 * 1024;
    private const string HdtName = "HDT Skinned Mesh Physics Object";
    private const string BodyTriName = "BODYTRI";
    private const string DintHairNifPrefix =
        "meshes/armor/[dint999]/02 Hair/hairS/";

    public ExternalHeadPartProviderNifReadResult Read(
        ExternalHeadPartProviderNifReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!IsCanonicalNifPath(request.SourcePath))
            diagnostics.Add(Error(
                "external-headpart-nif-asset-path",
                "The external headpart source must be a canonical meshes-relative .nif AssetPath."));
        else if (!request.AllowPhysicsBinding && !request.SourcePath.Value.StartsWith(
                     DintHairNifPrefix,
                     StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error(
                "external-headpart-nif-provider-path",
                "The external provider reader admits only the exact Dint HairPack02 hairS NIF route."));
        if (request.Bytes.IsDefaultOrEmpty ||
            request.Bytes.Length > MaximumNifBytes)
            diagnostics.Add(Error(
                "external-headpart-nif-size",
                $"The external headpart NIF must contain 1 to {MaximumNifBytes} bytes."));
        if (HasErrors(diagnostics))
            return Refused(diagnostics);

        Sha256Hash actualHash = new(Convert.ToHexString(
            SHA256.HashData(request.Bytes.AsSpan())));
        if (actualHash != request.ExpectedSourceSha256)
        {
            diagnostics.Add(Error(
                "external-headpart-nif-hash-mismatch",
                $"External headpart NIF hash {actualHash} does not match {request.ExpectedSourceSha256}."));
            return Refused(diagnostics);
        }

        try
        {
            SseNifDocument document =
                SseFaceGeomCarrierCodec.ParseExternalHeadPartProvider(
                    request.Bytes.ToArray());
            if (document.UserVersion != 12 ||
                document.BethesdaStreamVersion != 100)
                throw Invalid(
                    "The external headpart NIF is not a Skyrim SE user-version 12 / stream-100 asset.");
            if (document.Roots.Length != 1)
                throw Invalid(
                    "An external headpart NIF must have exactly one unambiguous root.");

            int rootIndex = document.Roots[0];
            SseNifBlock root = document.Blocks[rootIndex];
            if (!string.Equals(root.Type, "NiNode", StringComparison.Ordinal))
                throw Invalid(
                    "An external headpart provider root must be a NiNode.");

            HashSet<int> reachable =
                SseFaceGeomCarrierCodec.FindReachableBlockIndexes(document);
            var dependencies = ImmutableArray.CreateBuilder<AssetPath>();
            var providerSidecars = ImmutableArray.CreateBuilder<AssetPath>();
            var shapeNames = ImmutableArray.CreateBuilder<string>();
            var physicsObjectLocators = ImmutableArray.CreateBuilder<string>();
            var seenShapeNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (SseNifBlock shape in request.AllowPhysicsBinding
                         ? document.Blocks.Where(item =>
                             reachable.Contains(item.Index) &&
                             item.Type is "BSTriShape" or "BSDynamicTriShape" or
                                 "BSSubIndexTriShape")
                         : [])
            {
                if (string.IsNullOrWhiteSpace(shape.Name) ||
                    !ExternalHeadPartPhysicsPathRules.IsSafePortableToken(
                        shape.Name))
                    throw Invalid(
                        $"External shape block {shape.Index} has an empty or unsafe shape name.");
                if (!seenShapeNames.Add(shape.Name))
                    throw Invalid(
                        $"External provider NIF contains duplicate reachable shape name '{shape.Name}'.");
                shapeNames.Add(shape.Name);
            }
            foreach (SseNifBlock textureSet in document.Blocks.Where(item =>
                         reachable.Contains(item.Index) &&
                         string.Equals(item.Type, "BSShaderTextureSet",
                             StringComparison.Ordinal)))
            {
                foreach (string texture in textureSet.Textures.Where(item =>
                             !string.IsNullOrWhiteSpace(item)))
                {
                    AssetPath texturePath = CanonicalTexture(
                        texture,
                        request.AllowPhysicsBinding);
                    if (request.AllowPhysicsBinding)
                        AddDependency(dependencies, texturePath, true);
                    else
                        dependencies.Add(texturePath);
                }
            }
            foreach (SseNifBlock metadata in document.Blocks.Where(item =>
                         string.Equals(item.Type, "NiStringExtraData",
                             StringComparison.Ordinal)))
            {
                if (!reachable.Contains(metadata.Index))
                    throw Invalid(
                        $"External metadata block {metadata.Index} is hidden and unreachable.");

                SseNifBlock[] owners = document.Blocks.Where(owner =>
                        reachable.Contains(owner.Index) &&
                        owner.References.Any(reference =>
                            reference.Kind == "extra" &&
                            reference.Target == metadata.Index))
                    .ToArray();
                if (owners.Length != 1)
                    throw Invalid(
                        $"External metadata block {metadata.Index} has {owners.Length} reachable owners; exactly one is required.");

                ReadOnlySpan<byte> bytes =
                    document.Data.AsSpan(metadata.Offset, metadata.Size);
                if (bytes.Length != 8)
                    throw Invalid(
                        $"External metadata block {metadata.Index} has trailing or truncated bytes; exactly 8 bytes are required.");
                uint nameIndex = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes[..4]);
                uint targetIndex = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes[4..]);
                if (nameIndex >= (uint)document.Strings.Length ||
                    targetIndex >= (uint)document.Strings.Length)
                    throw Invalid(
                        $"External metadata block {metadata.Index} contains an invalid string-table index.");

                string name = document.Strings[(int)nameIndex];
                string target = document.Strings[(int)targetIndex];
                AssetPath dependency;
                if (string.Equals(name, HdtName, StringComparison.Ordinal))
                {
                    if (owners[0].Index != rootIndex ||
                        !string.Equals(owners[0].Type, "NiNode",
                            StringComparison.Ordinal))
                        throw Invalid(
                            $"External HDT metadata block {metadata.Index} has the wrong owner; the exact root NiNode is required.");
                    if (request.AllowPhysicsBinding)
                    {
                        dependency = CanonicalPhysicsLocator(target);
                        physicsObjectLocators.Add(dependency.Value);
                    }
                    else
                    {
                        dependency = CanonicalDependency(
                            target, ".xml", request.AllowPhysicsBinding);
                        if (!providerSidecars.Any(item =>
                                string.Equals(item.Value, dependency.Value,
                                    StringComparison.OrdinalIgnoreCase)))
                            providerSidecars.Add(dependency);
                    }
                    continue;
                }
                else if (string.Equals(name, BodyTriName,
                             StringComparison.Ordinal))
                {
                    if (!string.Equals(owners[0].Type, "BSTriShape",
                            StringComparison.Ordinal))
                        throw Invalid(
                            $"External BODYTRI metadata block {metadata.Index} has the wrong owner; one reachable BSTriShape is required.");
                    dependency = CanonicalDependency(
                        target, ".tri", request.AllowPhysicsBinding);
                }
                else
                {
                    throw Invalid(
                        $"External metadata block {metadata.Index} uses unknown name '{name}'.");
                }

                AddDependency(
                    dependencies,
                    dependency,
                    request.AllowPhysicsBinding);
            }

            diagnostics.Add(new Diagnostic(
                "external-headpart-provider-nif-read",
                DiagnosticSeverity.Info,
                $"Parsed {dependencies.Count} external provider dependency route(s) from '{request.SourcePath}' without admitting provider geometry to FaceGeom."));
            return new ExternalHeadPartProviderNifReadResult(
                true,
                dependencies.ToImmutable(),
                providerSidecars.ToImmutable(),
                diagnostics.ToImmutable())
            {
                ShapeNames = shapeNames.ToImmutable(),
                PhysicsObjectLocators = physicsObjectLocators.ToImmutable()
            };
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(Error(
                "external-headpart-nif-malformed",
                $"External headpart NIF '{request.SourcePath.Value}' is malformed: {exception.Message}"));
            return Refused(diagnostics);
        }
        catch (OverflowException)
        {
            diagnostics.Add(Error(
                "external-headpart-nif-count-overflow",
                "External headpart NIF count arithmetic overflowed the supported address space."));
            return Refused(diagnostics);
        }
    }

    private static AssetPath CanonicalDependency(
        string value,
        string extension,
        bool strictPortablePath)
    {
        string normalized = value.Trim().Replace('\\', '/');
        if (normalized.StartsWith("armor/", StringComparison.OrdinalIgnoreCase))
            normalized = "meshes/" + normalized;
        AssetPath strictPath = default;
        if (strictPortablePath &&
            !ExternalHeadPartPhysicsPathRules.TryCreateAssetPath(
                normalized,
                "meshes/",
                extension,
                out strictPath))
            throw Invalid(
                $"External dependency '{value}' is not a safe meshes-relative {extension} route.");
        if (!normalized.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith('/') ||
            normalized.Contains(':') ||
            normalized.Contains('\0') ||
            normalized.Contains("//", StringComparison.Ordinal) ||
            normalized.Split('/').Any(segment => segment is "." or "..") ||
            !normalized.EndsWith(extension, StringComparison.Ordinal))
        {
            throw Invalid(
                $"External dependency '{value}' is not a canonical meshes-relative {extension} route.");
        }
        return strictPortablePath ? strictPath : new AssetPath(normalized);
    }

    private static AssetPath CanonicalTexture(
        string value,
        bool strictPortablePath)
    {
        string normalized = value.Trim().Replace('\\', '/');
        if (normalized.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["Data/".Length..];
        AssetPath strictPath = default;
        if (strictPortablePath &&
            !ExternalHeadPartPhysicsPathRules.TryCreateAssetPath(
                normalized,
                "textures/",
                ".dds",
                out strictPath))
            throw Invalid(
                $"External texture '{value}' is not a safe textures-relative DDS route.");
        if (!normalized.StartsWith("textures/",
                StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith('/') ||
            normalized.Contains(':') ||
            normalized.Contains('\0') ||
            normalized.Contains("//", StringComparison.Ordinal) ||
            normalized.Split('/').Any(segment => segment is "." or "..") ||
            !normalized.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(
                $"External texture '{value}' is not a canonical textures-relative DDS route.");
        }
        return strictPortablePath ? strictPath : new AssetPath(normalized);
    }

    private static void AddDependency(
        ImmutableArray<AssetPath>.Builder dependencies,
        AssetPath dependency,
        bool strictPortablePath)
    {
        foreach (AssetPath existingPath in dependencies)
        {
            if (!string.Equals(existingPath.Value, dependency.Value,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (strictPortablePath && !string.Equals(
                    existingPath.Value,
                    dependency.Value,
                    StringComparison.Ordinal))
                throw Invalid(
                    $"External dependency paths '{existingPath.Value}' and '{dependency.Value}' collide case-insensitively.");
            return;
        }
        dependencies.Add(dependency);
    }

    private static AssetPath CanonicalPhysicsLocator(string value)
    {
        if (!ExternalHeadPartPhysicsPathRules.TryCreateAssetPath(
                value,
                "SKSE/Plugins/hdtSkinnedMeshConfigs/",
                ".xml",
                out AssetPath path))
            throw Invalid(
                $"External HDT physics locator '{value}' is not a safe hdtSkinnedMeshConfigs XML route.");
        return path;
    }

    private static bool IsCanonicalNifPath(AssetPath path) =>
        ExternalHeadPartPhysicsPathRules.TryCreateAssetPath(
            path.Value,
            "meshes/",
            ".nif",
            out _);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static InvalidDataException Invalid(string message) =>
        new(message);

    private static ExternalHeadPartProviderNifReadResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, [], [], diagnostics.ToImmutable());
}

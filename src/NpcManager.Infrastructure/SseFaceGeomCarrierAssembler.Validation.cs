using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class SseFaceGeomCarrierAssembler
{
    private const int MaximumParts = 64;
    private const int MaximumPartBytes = 64 * 1024 * 1024;
    private const long MaximumTotalSourceBytes = 512L * 1024 * 1024;
    private const string HeadNodeName = "NPC Head [Head]";
    private const string SpineNodeName = "NPC Spine2 [Spn2]";
    private const string FaceNodeName = "BSFaceGenNiNodeSkinned";

    private static bool ValidateRequest(
        SseFaceGeomCarrierAssemblyRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Parts.IsDefaultOrEmpty || request.Parts.Length > MaximumParts)
            diagnostics.Add(Error("sse-facegeom-carrier-part-count",
                $"Carrier assembly requires 1 through {MaximumParts} selected headpart models."));
        if (!IsCanonicalFaceTintPath(request.FaceTintPath))
            diagnostics.Add(Error("sse-facegeom-carrier-facetint-path",
                "The carrier FaceTint route must be a canonical Textures/*.dds asset path."));
        if (!Enum.IsDefined(request.SkeletonAuthority))
            diagnostics.Add(Error("sse-facegeom-carrier-skeleton-authority",
                $"Carrier skeleton authority '{request.SkeletonAuthority}' is not supported."));

        long totalBytes = 0;
        var headParts = new HashSet<FormReference>();
        var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shapeNames = new HashSet<string>(StringComparer.Ordinal);
        var faceTintParts = 0;
        foreach (SseFaceGeomCarrierAssemblyPart part in request.Parts)
        {
            if (!headParts.Add(part.HeadPart))
                diagnostics.Add(Error("sse-facegeom-carrier-headpart-duplicate",
                    $"Headpart '{part.HeadPart}' occurs more than once."));
            if (!sourcePaths.Add(part.SourcePath.Value))
                diagnostics.Add(Error("sse-facegeom-carrier-source-duplicate",
                    $"Source NIF '{part.SourcePath.Value}' occurs more than once."));
            if (!IsCanonicalNifPath(part.SourcePath))
                diagnostics.Add(Error("sse-facegeom-carrier-source-path",
                    $"Source '{part.SourcePath.Value}' must be a canonical meshes-relative .nif path."));
            if (part.SourceBytes.IsDefaultOrEmpty ||
                part.SourceBytes.Length > MaximumPartBytes)
                diagnostics.Add(Error("sse-facegeom-carrier-source-size",
                    $"Source '{part.SourcePath.Value}' must contain 1 through {MaximumPartBytes} bytes."));
            totalBytes = checked(totalBytes + part.SourceBytes.Length);
            if (!IsNifName(part.OutputShapeName) ||
                !shapeNames.Add(part.OutputShapeName))
                diagnostics.Add(Error("sse-facegeom-carrier-shape-name",
                    $"Output shape name '{part.OutputShapeName}' is invalid or duplicated."));
            if (part.UsesFaceTint) faceTintParts++;
            if (!part.TextureSetOverride.IsDefaultOrEmpty &&
                part.TextureSetOverride.Length != 8)
                diagnostics.Add(Error("sse-facegeom-carrier-texture-override-shape",
                    $"Source '{part.SourcePath.Value}' TXST override must contain exactly eight NIF slots."));
            if (part.HairTintPackedRgb is > 0x00FF_FFFF)
                diagnostics.Add(Error("sse-facegeom-carrier-hair-tint",
                    $"Source '{part.SourcePath.Value}' hair tint exceeds packed RGB 0xFFFFFF."));
        }
        if (totalBytes > MaximumTotalSourceBytes)
            diagnostics.Add(Error("sse-facegeom-carrier-source-budget",
                $"Selected headpart sources exceed the {MaximumTotalSourceBytes}-byte aggregate budget."));
        if (faceTintParts != 1)
            diagnostics.Add(Error("sse-facegeom-carrier-facetint-owner",
                $"Exactly one selected headpart must own the FaceTint route; found {faceTintParts}."));
        return !HasErrors(diagnostics);
    }

    private static AdmittedPart? AdmitPart(
        SseFaceGeomCarrierAssemblyPart part,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        byte[] bytes = part.SourceBytes.ToArray();
        var actualHash = new Sha256Hash(Convert.ToHexString(
            SHA256.HashData(bytes)));
        if (actualHash != part.ExpectedSourceSha256)
        {
            diagnostics.Add(Error("sse-facegeom-carrier-source-hash",
                $"Source '{part.SourcePath.Value}' hash {actualHash} does not match {part.ExpectedSourceSha256}."));
            return null;
        }

        SseNifDocument document = SseFaceGeomCarrierCodec.Parse(bytes);
        if (document.UserVersion != 12 || document.BethesdaStreamVersion != 100)
            throw Invalid($"Source '{part.SourcePath.Value}' is not a Skyrim SE stream-100 NIF.");
        if (document.Roots.Length != 1)
            throw Invalid($"Source '{part.SourcePath.Value}' must have exactly one root.");
        HashSet<int> reachable =
            SseFaceGeomCarrierCodec.FindReachableBlockIndexes(document);
        if (reachable.Count != document.Blocks.Length)
            throw Invalid($"Source '{part.SourcePath.Value}' contains unreachable hidden blocks.");

        ImmutableArray<SseNifBlock> nodes = document.Blocks
            .Where(block => block.Type is "NiNode" or "BSFadeNode")
            .ToImmutableArray();
        if (nodes.Length is < 2 or > 16)
            throw Invalid($"Source '{part.SourcePath.Value}' must contain a bounded root, head, and skin-bone node set.");
        SseNifBlock root = document.Blocks[document.Roots[0]];
        if (root.Type != "NiNode")
            throw Invalid($"Source '{part.SourcePath.Value}' root must be a NiNode.");
        SseNifBlock head = SingleNamedNode(nodes, HeadNodeName,
            part.SourcePath);
        SseNifBlock? spine = OptionalNamedNode(nodes, SpineNodeName,
            part.SourcePath);
        if (root.Index == head.Index || root.Index == spine?.Index)
            throw Invalid($"Source '{part.SourcePath.Value}' root aliases a required bone node.");

        ImmutableArray<SseNifBlock> shapes = document.Blocks
            .Where(block => block.Type is "BSDynamicTriShape" or "BSTriShape")
            .ToImmutableArray();
        if (shapes.Length != 1 || shapes[0].Type != "BSDynamicTriShape" ||
            shapes[0].DynamicGeometry is null ||
            string.IsNullOrWhiteSpace(shapes[0].Name))
            throw Invalid($"Source '{part.SourcePath.Value}' must expose exactly one named packed BSDynamicTriShape.");
        SseNifBlock shape = shapes[0];
        int skinIndex = SingleTarget(shape, "skin", part.SourcePath);
        int[] shaderTargets = Targets(shape, "shader");
        if (shaderTargets.Length != 1 || shaderTargets[0] < 0)
        {
            if (IsShaderlessDummyPart(part, shape))
            {
                diagnostics.Add(new Diagnostic(
                    "sse-facegeom-carrier-shaderless-dummy-skipped",
                    DiagnosticSeverity.Warning,
                    $"Source '{part.SourcePath.Value}' is an explicitly shaderless dummy lens part and was omitted from the assembled FaceGeom carrier."));
                return null;
            }
            throw Invalid($"Source '{part.SourcePath.Value}' block {shape.Index} must have one non-null shader reference.");
        }
        int shaderIndex = shaderTargets[0];
        SseNifBlock skin = document.Blocks[skinIndex];
        SseNifBlock shader = document.Blocks[shaderIndex];
        if (skin.Type is not "NiSkinInstance" and not "BSDismemberSkinInstance")
            throw Invalid($"Source '{part.SourcePath.Value}' shape has an unsupported skin instance.");
        if (shader.Type != "BSLightingShaderProperty")
            throw Invalid($"Source '{part.SourcePath.Value}' shape has an unsupported shader.");
        if (SingleTarget(skin, "skeletonroot", part.SourcePath) != root.Index)
            throw Invalid($"Source '{part.SourcePath.Value}' skin is not rooted at its scene node.");
        int[] bones = Targets(skin, "bone");
        if (bones.Length is < 1 or > 8 ||
            bones.Distinct().Count() != bones.Length)
            throw Invalid($"Source '{part.SourcePath.Value}' skin bones must be one through eight distinct admitted scene nodes.");
        ImmutableArray<SseNifBlock> boneNodes = bones
            .Select(index => document.Blocks[index])
            .ToImmutableArray();
        if (boneNodes.Any(node => node.Type is not "NiNode" and not "BSFadeNode") ||
            boneNodes.Any(node => node.Index == root.Index) ||
            boneNodes.All(node => node.Index != head.Index) ||
            boneNodes.Any(node => string.IsNullOrWhiteSpace(node.Name) ||
                                  !IsNifName(node.Name)) ||
            boneNodes.Select(node => node.Name)
                .Distinct(StringComparer.Ordinal).Count() != boneNodes.Length)
            throw Invalid($"Source '{part.SourcePath.Value}' skin bones are not a bounded, named, non-root node set containing the head node.");
        ValidateSourceNodes(root, nodes, boneNodes, shape, part.SourcePath);
        ValidateBakedPositions(part, shape);

        ImmutableHashSet<int> closureIndexes = FindShapeClosure(document, shape,
            root, boneNodes, part.SourcePath);
        ImmutableArray<SseNifBlock> closure = closureIndexes.Order()
            .Select(index => document.Blocks[index]).ToImmutableArray();
        ImmutableArray<SseNifBlock> nonNodes = document.Blocks
            .Where(block => block.Type is not "NiNode" and not "BSFadeNode")
            .ToImmutableArray();
        if (!closure.Select(block => block.Index).SequenceEqual(
                nonNodes.Select(block => block.Index)))
            throw Invalid($"Source '{part.SourcePath.Value}' contains non-node blocks outside its sole shape closure.");
        int textureSetIndex = SingleTarget(shader, "textureset", part.SourcePath);
        SseNifBlock textureSet = document.Blocks[textureSetIndex];
        if (textureSet.Type != "BSShaderTextureSet" ||
            textureSet.Textures.IsDefaultOrEmpty)
            throw Invalid($"Source '{part.SourcePath.Value}' shader lacks a bounded texture set.");
        if (part.UsesFaceTint &&
            !SseFaceGeomCarrierCodec.IsValidHeadDiffusePath(
                textureSet.Textures[0]))
        {
            diagnostics.Add(Error("sse-facegeom-carrier-head-diffuse",
                $"Source '{part.SourcePath.Value}' must retain a nonempty DDS diffuse route in texture slot 0, separate from FaceTint."));
            return null;
        }

        if (!part.TextureSetOverride.IsDefaultOrEmpty &&
            textureSet.Textures.Length < 8)
        {
            diagnostics.Add(Error("carrier-texture-slot-capacity",
                $"Source '{part.SourcePath.Value}' exposes {textureSet.Textures.Length} texture slots; a TXST override requires at least eight."));
            return null;
        }
        if (part.HairTintPackedRgb is not null &&
            (shader.Size < 16 ||
             System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                 document.Data.AsSpan(shader.Offset, sizeof(uint))) != 6))
        {
            diagnostics.Add(Error("sse-facegeom-carrier-hair-tint-shader",
                $"Source '{part.SourcePath.Value}' requested hair tint but does not use shader type 6."));
            return null;
        }

        return new AdmittedPart(part, document, root, head, spine, boneNodes, shape,
            shader, textureSet, closure);
    }

    private static bool IsShaderlessDummyPart(
        SseFaceGeomCarrierAssemblyPart part,
        SseNifBlock shape) =>
        !part.UsesFaceTint &&
        part.SourcePath.Value.Contains("dummy", StringComparison.OrdinalIgnoreCase) &&
        part.SourcePath.Value.Contains("lens", StringComparison.OrdinalIgnoreCase) &&
        shape.Name?.Contains("lens", StringComparison.OrdinalIgnoreCase) == true;

    private static void ValidateSourceNodes(
        SseNifBlock root,
        ImmutableArray<SseNifBlock> nodes,
        ImmutableArray<SseNifBlock> boneNodes,
        SseNifBlock shape,
        AssetPath source)
    {
        var nodeIndexes = nodes.Select(node => node.Index).ToHashSet();
        foreach (SseNifBlock node in nodes)
        {
            foreach (SseNifReference reference in node.References
                         .Where(reference => reference.Target >= 0))
            {
                if (reference.Kind != "child")
                    throw Invalid($"Source '{source.Value}' bone nodes contain an unexpected outgoing reference.");
                if (reference.Target == shape.Index)
                {
                    if (node.Index != root.Index)
                        throw Invalid($"Source '{source.Value}' shape must be a direct root child.");
                    continue;
                }
                if (!nodeIndexes.Contains(reference.Target))
                    throw Invalid($"Source '{source.Value}' node hierarchy references a non-node child.");
            }
        }

        int[] rootShapeChildren = root.References
            .Where(reference => reference.Kind == "child" &&
                                reference.Target == shape.Index)
            .Select(reference => reference.Target)
            .ToArray();
        if (rootShapeChildren.Length != 1)
            throw Invalid($"Source '{source.Value}' root must own exactly one direct selected shape child.");

        HashSet<int> reachableNodes = FindReachableNodes(root, nodes);
        if (boneNodes.Any(node => !reachableNodes.Contains(node.Index)))
            throw Invalid($"Source '{source.Value}' skin bones are not reachable through the source node hierarchy.");
    }

    private static HashSet<int> FindReachableNodes(
        SseNifBlock root,
        ImmutableArray<SseNifBlock> nodes)
    {
        var byIndex = nodes.ToDictionary(node => node.Index);
        var reachable = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(root.Index);
        while (pending.Count > 0)
        {
            int index = pending.Pop();
            if (!reachable.Add(index)) continue;
            foreach (int child in byIndex[index].References
                         .Where(reference => reference.Kind == "child" &&
                                             reference.Target >= 0 &&
                                             byIndex.ContainsKey(reference.Target))
                         .Select(reference => reference.Target))
                pending.Push(child);
        }
        return reachable;
    }

    private static void ValidateBakedPositions(
        SseFaceGeomCarrierAssemblyPart part,
        SseNifBlock shape)
    {
        if (part.BakedPositions.IsDefaultOrEmpty) return;
        int vertexCount = shape.DynamicGeometry!.VertexCount;
        if (part.BakedPositions.Length != vertexCount)
            throw Invalid($"Source '{part.SourcePath.Value}' has {vertexCount} vertices but received {part.BakedPositions.Length} baked positions.");
        const float coordinateLimit = 1_000_000f;
        for (int index = 0; index < part.BakedPositions.Length; index++)
        {
            Vector3 position = part.BakedPositions[index];
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
                !float.IsFinite(position.Z) ||
                MathF.Abs(position.X) > coordinateLimit ||
                MathF.Abs(position.Y) > coordinateLimit ||
                MathF.Abs(position.Z) > coordinateLimit)
                throw Invalid($"Source '{part.SourcePath.Value}' baked position {index} is non-finite or outside the coordinate bound.");
        }
    }

    private static void ValidateSkeletonCompatibility(
        ImmutableArray<AdmittedPart>.Builder parts,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        AdmittedPart authority = parts[0];
        Vector3 expectedHead = ReadNodeWorldTranslation(authority.Document,
            authority.HeadNode);
        AdmittedPart? spineAuthority = parts.FirstOrDefault(part =>
            part.SpineNode is not null);
        if (spineAuthority is null)
        {
            diagnostics.Add(Error("sse-facegeom-carrier-spine-authority",
                "At least one selected headpart source must carry the standard spine-node transform."));
            return;
        }
        Vector3 expectedSpine = ReadNodeWorldTranslation(spineAuthority.Document,
            spineAuthority.SpineNode!);
        const float tolerance = 0.02f;
        var expectedBones = new Dictionary<string, Vector3>(
            StringComparer.Ordinal);
        foreach (AdmittedPart part in parts)
        {
            bool normalizedNestedHeadOnly =
                CanNormalizeNestedHeadOnlySkeleton(part);
            bool warnedNestedNormalization = false;
            void AddTransformDiagnostic(string code, string message)
            {
                if (!normalizedNestedHeadOnly)
                {
                    diagnostics.Add(Error(code, message));
                    return;
                }

                if (warnedNestedNormalization) return;
                diagnostics.Add(new Diagnostic(
                    "sse-facegeom-carrier-nested-head-skeleton-normalized",
                    DiagnosticSeverity.Warning,
                    $"Source '{part.Request.SourcePath.Value}' carries a nested head-only skeleton with divergent bone translations; the carrier normalized it to the selected FaceGen skeleton."));
                warnedNestedNormalization = true;
            }

            Vector3 head = ReadNodeWorldTranslation(part.Document, part.HeadNode);
            if (Vector3.Distance(head, expectedHead) > tolerance)
                AddTransformDiagnostic(
                    "sse-facegeom-carrier-head-transform",
                    $"Source '{part.Request.SourcePath.Value}' head-node translation is incompatible with the selected carrier skeleton.");
            if (part.SpineNode is not null)
            {
                Vector3 spine = ReadNodeWorldTranslation(part.Document, part.SpineNode);
                if (Vector3.Distance(spine, expectedSpine) > tolerance)
                    AddTransformDiagnostic(
                        "sse-facegeom-carrier-spine-transform",
                        $"Source '{part.Request.SourcePath.Value}' spine-node translation is incompatible with the selected carrier skeleton.");
            }
            foreach (SseNifBlock bone in part.BoneNodes)
            {
                string name = bone.Name!;
                Vector3 translation = ReadNodeWorldTranslation(part.Document, bone);
                if (expectedBones.TryGetValue(name, out Vector3 expected))
                {
                    if (Vector3.Distance(translation, expected) > tolerance)
                        AddTransformDiagnostic(
                            "sse-facegeom-carrier-bone-transform",
                            $"Source '{part.Request.SourcePath.Value}' bone-node '{name}' translation is incompatible with the selected carrier skeleton.");
                }
                else
                {
                    expectedBones.Add(name, translation);
                }
            }
        }
    }

    private static bool CanNormalizeNestedHeadOnlySkeleton(AdmittedPart part) =>
        !part.Request.UsesFaceTint &&
        part.BoneNodes.Length == 1 &&
        string.Equals(part.BoneNodes[0].Name, HeadNodeName,
            StringComparison.Ordinal) &&
        HasNestedNodePath(part.Document, part.Root, part.HeadNode);

    private static bool HasNestedNodePath(
        SseNifDocument document,
        SseNifBlock root,
        SseNifBlock target)
    {
        var nodes = document.Blocks
            .Where(block => block.Type is "NiNode" or "BSFadeNode")
            .ToDictionary(block => block.Index);
        var pending = new Queue<(int Index, int Depth)>();
        var seen = new HashSet<int>();
        pending.Enqueue((root.Index, 0));
        while (pending.Count > 0)
        {
            var (index, depth) = pending.Dequeue();
            if (!seen.Add(index)) continue;
            if (index == target.Index) return depth > 1;
            foreach (int child in nodes[index].References
                         .Where(reference => reference.Kind == "child" &&
                                             reference.Target >= 0 &&
                                             nodes.ContainsKey(reference.Target))
                         .Select(reference => reference.Target))
                pending.Enqueue((child, depth + 1));
        }

        return false;
    }

    private static ImmutableHashSet<int> FindShapeClosure(
        SseNifDocument document,
        SseNifBlock shape,
        SseNifBlock root,
        ImmutableArray<SseNifBlock> boneNodes,
        AssetPath source)
    {
        var admittedBoneIndexes = boneNodes
            .Select(node => node.Index)
            .ToImmutableHashSet();
        var closure = ImmutableHashSet.CreateBuilder<int>();
        var pending = new Stack<int>();
        pending.Push(shape.Index);
        while (pending.Count > 0)
        {
            int index = pending.Pop();
            if (!closure.Add(index)) continue;
            foreach (SseNifReference reference in document.Blocks[index].References)
            {
                if (reference.Target < 0) continue;
                if (reference.Target == root.Index ||
                    admittedBoneIndexes.Contains(reference.Target))
                {
                    bool admittedNodeReference =
                        reference.Kind == "skeletonroot" && reference.Target == root.Index ||
                        reference.Kind == "bone" &&
                        admittedBoneIndexes.Contains(reference.Target);
                    if (!admittedNodeReference)
                        throw Invalid($"Source '{source.Value}' shape closure uses an unsupported node reference '{reference.Kind}'.");
                    continue;
                }
                if (document.Blocks[reference.Target].Type is "NiNode" or "BSFadeNode")
                    throw Invalid($"Source '{source.Value}' shape closure references an unexpected node.");
                pending.Push(reference.Target);
            }
        }
        return closure.ToImmutable();
    }

    private static SseNifBlock SingleNamedNode(
        ImmutableArray<SseNifBlock> nodes,
        string name,
        AssetPath source)
    {
        SseNifBlock[] matches = nodes.Where(node =>
            string.Equals(node.Name, name, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
            throw Invalid($"Source '{source.Value}' must contain one '{name}' node.");
        return matches[0];
    }

    private static SseNifBlock? OptionalNamedNode(
        ImmutableArray<SseNifBlock> nodes,
        string name,
        AssetPath source)
    {
        SseNifBlock[] matches = nodes.Where(node =>
            string.Equals(node.Name, name, StringComparison.Ordinal)).ToArray();
        if (matches.Length > 1)
            throw Invalid($"Source '{source.Value}' contains more than one '{name}' node.");
        return matches.SingleOrDefault();
    }

    private static int SingleTarget(SseNifBlock block, string kind,
        AssetPath source)
    {
        int[] targets = Targets(block, kind);
        if (targets.Length != 1 || targets[0] < 0)
            throw Invalid($"Source '{source.Value}' block {block.Index} must have one non-null {kind} reference.");
        return targets[0];
    }

    private static int[] Targets(SseNifBlock block, string kind) =>
        block.References.Where(reference =>
                string.Equals(reference.Kind, kind, StringComparison.Ordinal))
            .Select(reference => reference.Target).ToArray();

    private static bool IsCanonicalNifPath(AssetPath path) =>
        path.Value.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) &&
        path.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) &&
        !path.Value.Contains('\\');

    private static bool IsCanonicalFaceTintPath(AssetPath path)
    {
        if (path.Value.Contains('\\')) return false;
        string[] segments = path.Value.Split('/');
        if (segments.Length != 7 ||
            !string.Equals(segments[0], "Textures",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[1], "Actors",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[2], "Character",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[3], "FaceGenData",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[4], "FaceTint",
                StringComparison.OrdinalIgnoreCase))
            return false;

        string plugin = segments[5];
        bool pluginName = plugin.EndsWith(".esp",
                              StringComparison.OrdinalIgnoreCase) ||
                          plugin.EndsWith(".esm",
                              StringComparison.OrdinalIgnoreCase) ||
                          plugin.EndsWith(".esl",
                              StringComparison.OrdinalIgnoreCase);
        string file = segments[6];
        return pluginName &&
               file.Length == 12 &&
               file.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) &&
               file.AsSpan(0, 8).ToArray().All(Uri.IsHexDigit);
    }

    private static bool IsNifName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255 ||
            value.Contains('\0')) return false;
        byte[] encoded = Encoding.Latin1.GetBytes(value);
        return encoded.Length <= 255 &&
               string.Equals(Encoding.Latin1.GetString(encoded), value,
                   StringComparison.Ordinal);
    }

    private static InvalidDataException Invalid(string message) => new(message);
}

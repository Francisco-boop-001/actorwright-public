using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

internal static partial class SseFaceGeomCarrierCodec
{
    private static readonly ImmutableDictionary<string, int> QualifiedCensus =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["BSDismemberSkinInstance"] = 4,
            ["BSDynamicTriShape"] = 7,
            ["BSFadeNode"] = 1,
            ["BSLightingShaderProperty"] = 7,
            ["BSShaderTextureSet"] = 6,
            ["NiAlphaProperty"] = 6,
            ["NiNode"] = 3,
            ["NiSkinData"] = 7,
            ["NiSkinInstance"] = 3,
            ["NiSkinPartition"] = 7
        }.ToImmutableDictionary(StringComparer.Ordinal);

    internal static QualifiedFaceGeomCarrierStructure BuildStructure(SseNifDocument document)
    {
        var reachable = Reachable(document);
        var census = reachable.Select(index => document.Blocks[index].Type)
            .GroupBy(type => type, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new QualifiedFaceGeomCarrierCensusEntry(group.Key, group.Count()))
            .ToImmutableArray();
        var shapes = reachable.Order()
            .Select(index => document.Blocks[index])
            .Where(block => string.Equals(block.Type, "BSDynamicTriShape", StringComparison.Ordinal))
            .Select(block => block.Name ?? string.Empty)
            .ToImmutableArray();
        var nullChildren = reachable.Select(index => document.Blocks[index])
            .Sum(block => block.References.Count(reference =>
                string.Equals(reference.Kind, "child", StringComparison.Ordinal) && reference.Target == -1));
        var graphHash = ComputeGraphHash(document, reachable);
        return new QualifiedFaceGeomCarrierStructure(
            document.Blocks.Length,
            reachable.Count,
            document.Roots.Length,
            census.FirstOrDefault(item => item.BlockType == "NiNode")?.Count ?? 0,
            census.FirstOrDefault(item => item.BlockType == "BSFadeNode")?.Count ?? 0,
            census.FirstOrDefault(item => item.BlockType == "BSDynamicTriShape")?.Count ?? 0,
            nullChildren,
            shapes,
            census,
            graphHash);
    }

    internal static void Qualify(
        SseNifDocument document,
        QualifiedFaceGeomCarrierStructure structure,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        Qualify(
            document,
            structure,
            QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape,
            diagnostics);

    internal static void Qualify(
        SseNifDocument document,
        QualifiedFaceGeomCarrierStructure structure,
        QualifiedFaceGeomCarrierProfile profile,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (document.UserVersion != ExpectedUserVersion ||
            document.BethesdaStreamVersion != ExpectedStreamVersion)
            diagnostics.Add(new Diagnostic("qualified-carrier-version-envelope", DiagnosticSeverity.Error,
                "The NIF is not a Skyrim SE user-version 12 / Bethesda-stream 100 carrier."));
        if (document.Roots.Length != 1 || document.Roots[0] != 0)
            diagnostics.Add(new Diagnostic("qualified-carrier-root-envelope", DiagnosticSeverity.Error,
                "A qualified carrier must have exactly one root at block 0."));
        if (structure.NullChildReferenceCount != 0)
            diagnostics.Add(new Diagnostic("qualified-carrier-null-children", DiagnosticSeverity.Error,
                "A qualified complete carrier may not contain reachable null child slots."));
        if (structure.ReachableShapeNames.Any(string.IsNullOrWhiteSpace) ||
            structure.ReachableShapeNames.Distinct(StringComparer.Ordinal).Count() !=
            structure.ReachableShapeNames.Length)
            diagnostics.Add(new Diagnostic("qualified-carrier-shape-names", DiagnosticSeverity.Error,
                "Every reachable carrier shape must have a distinct non-empty name."));

        switch (profile)
        {
            case QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape:
                ValidateProviderPinnedEnvelope(structure, diagnostics);
                break;
            case QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete:
                ValidateManagerAssembledEnvelope(structure, diagnostics);
                break;
            case QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete:
                ValidateRaceMenuExportedEnvelope(structure, diagnostics);
                break;
            default:
                diagnostics.Add(new Diagnostic(
                    "qualified-carrier-profile-unsupported",
                    DiagnosticSeverity.Error,
                    "The requested carrier qualification profile is unsupported."));
                return;
        }

        ValidateCompleteTopology(document, profile, diagnostics);
    }

    private static void ValidateProviderPinnedEnvelope(
        QualifiedFaceGeomCarrierStructure structure,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (structure.BlockCount != 51 ||
            structure.ReachableBlockCount != structure.BlockCount)
            diagnostics.Add(new Diagnostic("qualified-carrier-reachability", DiagnosticSeverity.Error,
                "A provider-pinned complete carrier must contain exactly 51 blocks, all reachable from its root."));
        if (structure.NiNodeCount != 3 || structure.FadeNodeCount != 1 ||
            structure.DynamicShapeCount != 7)
            diagnostics.Add(new Diagnostic("qualified-carrier-shape-envelope", DiagnosticSeverity.Error,
                "A provider-pinned complete carrier requires three NiNode blocks, one BSFadeNode root, and seven dynamic face/headpart shapes."));

        var actual = structure.ReachableCensus.ToDictionary(
            item => item.BlockType,
            item => item.Count,
            StringComparer.Ordinal);
        if (actual.Count != QualifiedCensus.Count || QualifiedCensus.Any(expected =>
                !actual.TryGetValue(expected.Key, out var count) || count != expected.Value))
            diagnostics.Add(new Diagnostic("qualified-carrier-census", DiagnosticSeverity.Error,
                "The reachable block census does not match the provider-pinned complete-carrier envelope."));
    }

    private static void ValidateManagerAssembledEnvelope(
        QualifiedFaceGeomCarrierStructure structure,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        const int maximumShapes = 64;
        var actual = structure.ReachableCensus.ToDictionary(
            item => item.BlockType,
            item => item.Count,
            StringComparer.Ordinal);
        int Count(string blockType) =>
            actual.TryGetValue(blockType, out int count) ? count : 0;

        int shapeCount = structure.DynamicShapeCount;
        int alphaCount = Count("NiAlphaProperty");
        int skinCount = checked(
            Count("BSDismemberSkinInstance") + Count("NiSkinInstance"));
        bool onlyAssemblerTypes = actual.Keys.All(QualifiedCensus.ContainsKey);
        bool completeCensus =
            onlyAssemblerTypes &&
            Count("BSFadeNode") == 1 &&
            Count("NiNode") is >= 3 and <= 15 &&
            Count("BSDynamicTriShape") == shapeCount &&
            Count("BSLightingShaderProperty") == shapeCount &&
            Count("BSShaderTextureSet") == shapeCount &&
            skinCount == shapeCount &&
            Count("NiSkinData") == shapeCount &&
            Count("NiSkinPartition") == shapeCount &&
            alphaCount is >= 0 && alphaCount <= shapeCount &&
            actual.Values.Sum() == structure.BlockCount &&
            structure.BlockCount == checked(
                6 * shapeCount + alphaCount + Count("NiNode") +
                Count("BSFadeNode"));

        if (structure.BlockCount != structure.ReachableBlockCount)
            diagnostics.Add(new Diagnostic(
                "qualified-carrier-manager-reachability",
                DiagnosticSeverity.Error,
                "A Manager-assembled carrier must make every block reachable from its sole root."));
        if (shapeCount is < 1 or > maximumShapes ||
            structure.NiNodeCount is < 3 or > 15 ||
            structure.FadeNodeCount != 1)
            diagnostics.Add(new Diagnostic(
                "qualified-carrier-manager-shape-envelope",
                DiagnosticSeverity.Error,
                $"A Manager-assembled carrier requires 1 through {maximumShapes} dynamic shapes, three through fifteen NiNode blocks, and one BSFadeNode root."));
        if (!completeCensus)
            diagnostics.Add(new Diagnostic(
                "qualified-carrier-manager-census",
                DiagnosticSeverity.Error,
                "The reachable block census does not match the product assembler's per-shape skin, shader, texture, and optional-alpha closure."));
    }

    private static void ValidateRaceMenuExportedEnvelope(
        QualifiedFaceGeomCarrierStructure structure,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        const int maximumShapes = 64;
        var actual = structure.ReachableCensus.ToDictionary(
            item => item.BlockType,
            item => item.Count,
            StringComparer.Ordinal);
        int Count(string blockType) =>
            actual.TryGetValue(blockType, out int count) ? count : 0;

        int shapeCount = structure.DynamicShapeCount;
        int shaderCount = Count("BSLightingShaderProperty");
        int textureSetCount = Count("BSShaderTextureSet");
        int alphaCount = Count("NiAlphaProperty");
        int skinCount = checked(
            Count("BSDismemberSkinInstance") + Count("NiSkinInstance"));
        bool completeCensus =
            actual.Keys.All(QualifiedCensus.ContainsKey) &&
            Count("BSFadeNode") == 1 &&
            Count("NiNode") is >= 3 and <= 15 &&
            Count("BSDynamicTriShape") == shapeCount &&
            shaderCount is >= 1 && shaderCount <= shapeCount &&
            textureSetCount is >= 1 && textureSetCount <= shaderCount &&
            skinCount == shapeCount &&
            Count("NiSkinData") == shapeCount &&
            Count("NiSkinPartition") == shapeCount &&
            alphaCount >= 0 && alphaCount <= shaderCount &&
            actual.Values.Sum() == structure.BlockCount;

        if (structure.BlockCount != structure.ReachableBlockCount)
            diagnostics.Add(new Diagnostic(
                "qualified-carrier-racemenu-export-reachability",
                DiagnosticSeverity.Error,
                "A RaceMenu-exported carrier must make every block reachable from its sole root."));
        if (shapeCount is < 1 or > maximumShapes ||
            structure.NiNodeCount is < 3 or > 15 ||
            structure.FadeNodeCount != 1)
            diagnostics.Add(new Diagnostic(
                "qualified-carrier-racemenu-export-shape-envelope",
                DiagnosticSeverity.Error,
                $"A RaceMenu-exported carrier requires 1 through {maximumShapes} dynamic shapes, three through fifteen NiNode blocks, and one BSFadeNode root."));
        if (!completeCensus)
            diagnostics.Add(new Diagnostic(
                "qualified-carrier-racemenu-export-census",
                DiagnosticSeverity.Error,
                "The reachable RaceMenu export census does not retain bounded per-shape skin closure and shader, texture, and alpha ownership."));
    }

    private static void ValidateCompleteTopology(
        SseNifDocument document,
        QualifiedFaceGeomCarrierProfile profile,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        RejectUnexpectedAuxiliaryReferences(document, diagnostics);

        var fadeNodes = BlocksOfType(document, "BSFadeNode");
        var headNodes = document.Blocks.Where(block =>
                string.Equals(block.Type, "NiNode", StringComparison.Ordinal) &&
                string.Equals(block.Name, "NPC Head [Head]", StringComparison.Ordinal))
            .ToImmutableArray();
        var spineNodes = document.Blocks.Where(block =>
                string.Equals(block.Type, "NiNode", StringComparison.Ordinal) &&
                string.Equals(block.Name, "NPC Spine2 [Spn2]", StringComparison.Ordinal))
            .ToImmutableArray();
        var skinnedNodes = document.Blocks.Where(block =>
                string.Equals(block.Type, "NiNode", StringComparison.Ordinal) &&
                string.Equals(block.Name, "BSFaceGenNiNodeSkinned", StringComparison.Ordinal))
            .ToImmutableArray();
        var shapes = BlocksOfType(document, "BSDynamicTriShape");
        var extraBoneNodes = document.Blocks.Where(block =>
                string.Equals(block.Type, "NiNode", StringComparison.Ordinal) &&
                !string.Equals(block.Name, "NPC Head [Head]", StringComparison.Ordinal) &&
                !string.Equals(block.Name, "NPC Spine2 [Spn2]", StringComparison.Ordinal) &&
                !string.Equals(block.Name, "BSFaceGenNiNodeSkinned", StringComparison.Ordinal))
            .ToImmutableArray();

        bool shapeCountValid = profile switch
        {
            QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape =>
                shapes.Length == 7,
            QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete =>
                shapes.Length is >= 1 and <= 64,
            QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete =>
                shapes.Length is >= 1 and <= 64,
            _ => false
        };
        if (fadeNodes.Length != 1 || headNodes.Length != 1 ||
            spineNodes.Length != 1 || skinnedNodes.Length != 1 ||
            !shapeCountValid ||
            profile == QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape &&
            !extraBoneNodes.IsEmpty ||
            (profile == QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete ||
             profile == QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete) &&
            extraBoneNodes.Length > 12)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-node-topology", DiagnosticSeverity.Error,
                "The admitted carrier requires one fade root, exact head/spine/skinned node roles, profile-bounded extra bones, and its profile-bounded shape count."));
            return;
        }

        var fade = fadeNodes[0];
        var head = headNodes[0];
        var spine = spineNodes[0];
        var skinned = skinnedNodes[0];
        ImmutableArray<SseNifBlock> admittedBones =
            profile == QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete ||
            profile == QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete
                ? ImmutableArray.Create(head, spine).AddRange(extraBoneNodes)
                : [head, spine];
        int[] expectedFadeChildren = admittedBones
            .Select(node => node.Index)
            .Append(skinned.Index)
            .ToArray();
        RequireExactTargets(fade, "child", expectedFadeChildren.ToImmutableArray(),
            "qualified-carrier-node-topology", diagnostics);
        foreach (SseNifBlock bone in admittedBones)
            RequireExactTargets(bone, "child", [], "qualified-carrier-node-topology", diagnostics);
        RequireExactTargets(skinned, "child", shapes.Select(shape => shape.Index).ToImmutableArray(),
            "qualified-carrier-node-topology", diagnostics);

        var skinTargets = ImmutableArray.CreateBuilder<int>(shapes.Length);
        var shaderTargets = ImmutableArray.CreateBuilder<int>(shapes.Length);
        var alphaTargets = ImmutableArray.CreateBuilder<int>(shapes.Length);
        int headShapeIndex = -1;
        if (profile == QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape)
        {
            var namedHeadShapes = shapes.Where(shape =>
                    shape.Name?.Contains("Head", StringComparison.OrdinalIgnoreCase) == true)
                .ToImmutableArray();
            if (namedHeadShapes.Length != 1)
                diagnostics.Add(new Diagnostic("qualified-carrier-alpha-topology", DiagnosticSeverity.Error,
                    "Exactly one of the seven provider-pinned shapes must be the head shape."));
            headShapeIndex = namedHeadShapes.Length == 1 ? namedHeadShapes[0].Index : -1;
        }

        foreach (var shape in shapes)
        {
            AddOwnedTarget(document, shape, "skin", SkinTypes, skinTargets,
                "qualified-carrier-shape-skin-topology", diagnostics);
            bool shaderlessRaceMenuDummy =
                profile == QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete &&
                IsRaceMenuShaderlessDummy(shape);
            if (shaderlessRaceMenuDummy)
                RequireNullTarget(shape, "shader",
                    "qualified-carrier-racemenu-export-dummy-topology", diagnostics);
            else
                AddOwnedTarget(document, shape, "shader", ["BSLightingShaderProperty"], shaderTargets,
                    "qualified-carrier-shape-shader-topology", diagnostics);
            if (profile == QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape)
                ValidateShapeAlpha(
                    document,
                    shape,
                    shape.Index == headShapeIndex,
                    alphaTargets,
                    diagnostics);
            else if (shaderlessRaceMenuDummy)
                RequireNullTarget(shape, "alpha",
                    "qualified-carrier-racemenu-export-dummy-topology", diagnostics);
            else
                ValidateManagerShapeAlpha(document, shape, alphaTargets, diagnostics);
        }

        RequireExactOwnership(document, skinTargets, SkinTypes,
            "qualified-carrier-skin-ownership", diagnostics);
        RequireExactOwnership(document, shaderTargets, ["BSLightingShaderProperty"],
            "qualified-carrier-shader-ownership", diagnostics);
        RequireExactOwnership(document, alphaTargets, ["NiAlphaProperty"],
            "qualified-carrier-alpha-topology", diagnostics);

        ValidateSkinClosure(document, skinTargets, admittedBones, skinned, diagnostics);
        ValidateShaderClosure(document, shaderTargets, profile, diagnostics);
    }

    private static void RejectUnexpectedAuxiliaryReferences(
        SseNifDocument document,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var unexpected = document.Blocks
            .SelectMany(block => block.References.Select(reference => (Block: block, Reference: reference)))
            .Where(item =>
                (item.Reference.Kind is "extra" or "effect" or "controller" or "collision") &&
                item.Reference.Target != -1)
            .ToImmutableArray();
        if (!unexpected.IsEmpty)
            diagnostics.Add(new Diagnostic("qualified-carrier-unrelated-reference", DiagnosticSeverity.Error,
                "The admitted carrier may not use extra/effect links or live controller/collision links to make unrelated blocks reachable."));
    }

    private static void ValidateShapeAlpha(
        SseNifDocument document,
        SseNifBlock shape,
        bool isHead,
        ImmutableArray<int>.Builder alphaTargets,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var references = References(shape, "alpha");
        if (references.Length != 1)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-alpha-topology", DiagnosticSeverity.Error,
                $"Shape {shape.Index} must carry exactly one alpha reference slot."));
            return;
        }

        var target = references[0].Target;
        if (isHead)
        {
            if (target != -1)
                diagnostics.Add(new Diagnostic("qualified-carrier-alpha-topology", DiagnosticSeverity.Error,
                    "The admitted head shape is the sole shape without an alpha property."));
            return;
        }

        if (target < 0 || !string.Equals(document.Blocks[target].Type, "NiAlphaProperty",
                StringComparison.Ordinal))
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-alpha-topology", DiagnosticSeverity.Error,
                $"Non-head shape {shape.Index} must own a NiAlphaProperty."));
            return;
        }
        alphaTargets.Add(target);
    }

    private static void ValidateManagerShapeAlpha(
        SseNifDocument document,
        SseNifBlock shape,
        ImmutableArray<int>.Builder alphaTargets,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var references = References(shape, "alpha");
        if (references.Length != 1)
        {
            diagnostics.Add(new Diagnostic(
                "qualified-carrier-manager-alpha-topology",
                DiagnosticSeverity.Error,
                $"Manager-assembled shape {shape.Index} must preserve exactly one alpha reference slot."));
            return;
        }

        int target = references[0].Target;
        if (target == -1) return;
        if (target < 0 ||
            !string.Equals(
                document.Blocks[target].Type,
                "NiAlphaProperty",
                StringComparison.Ordinal))
        {
            diagnostics.Add(new Diagnostic(
                "qualified-carrier-manager-alpha-topology",
                DiagnosticSeverity.Error,
                $"Manager-assembled shape {shape.Index} has a non-null alpha target of the wrong type."));
            return;
        }
        alphaTargets.Add(target);
    }

    private static bool IsRaceMenuShaderlessDummy(SseNifBlock shape) =>
        shape.Name is { } name &&
        name.EndsWith("_Dummy", StringComparison.Ordinal) &&
        name.Contains("Lens", StringComparison.OrdinalIgnoreCase);

    private static void RequireNullTarget(
        SseNifBlock owner,
        string kind,
        string diagnosticCode,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ImmutableArray<SseNifReference> references = References(owner, kind);
        if (references.Length != 1 || references[0].Target != -1)
            diagnostics.Add(new Diagnostic(
                diagnosticCode,
                DiagnosticSeverity.Error,
                $"RaceMenu dummy shape {owner.Index} must retain exactly one null {kind} reference."));
    }

    private static void ValidateSkinClosure(
        SseNifDocument document,
        ImmutableArray<int>.Builder skinTargets,
        ImmutableArray<SseNifBlock> admittedBones,
        SseNifBlock skinned,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var admittedBoneIndexes = admittedBones
            .Select(node => node.Index)
            .ToImmutableHashSet();
        var dataTargets = ImmutableArray.CreateBuilder<int>(skinTargets.Count);
        var partitionTargets = ImmutableArray.CreateBuilder<int>(skinTargets.Count);
        foreach (var skinIndex in skinTargets.Distinct().Order())
        {
            var skin = document.Blocks[skinIndex];
            AddOwnedTarget(document, skin, "skindata", ["NiSkinData"], dataTargets,
                "qualified-carrier-skin-topology", diagnostics);
            AddOwnedTarget(document, skin, "skinpartition", ["NiSkinPartition"], partitionTargets,
                "qualified-carrier-skin-topology", diagnostics);

            var skeleton = References(skin, "skeletonroot");
            var bones = References(skin, "bone");
            if (skeleton.Length != 1 || skeleton[0].Target != skinned.Index ||
                bones.Length is < 1 or > 8 || bones.Any(reference => reference.Target < 0) ||
                bones.Select(reference => reference.Target).Distinct().Count() != bones.Length ||
                bones.Any(reference => !admittedBoneIndexes.Contains(reference.Target)))
            {
                diagnostics.Add(new Diagnostic("qualified-carrier-skin-topology", DiagnosticSeverity.Error,
                    $"Skin block {skin.Index} must use the skinned node as skeleton root and one through eight distinct admitted bones."));
            }
        }

        RequireExactOwnership(document, dataTargets, ["NiSkinData"],
            "qualified-carrier-skin-data-ownership", diagnostics);
        RequireExactOwnership(document, partitionTargets, ["NiSkinPartition"],
            "qualified-carrier-skin-partition-ownership", diagnostics);
    }

    private static void ValidateShaderClosure(
        SseNifDocument document,
        ImmutableArray<int>.Builder shaderTargets,
        QualifiedFaceGeomCarrierProfile profile,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var textureTargets = ImmutableArray.CreateBuilder<int>(shaderTargets.Count);
        foreach (var shaderIndex in shaderTargets.Distinct().Order())
        {
            AddOwnedTarget(document, document.Blocks[shaderIndex], "textureset", ["BSShaderTextureSet"],
                textureTargets, "qualified-carrier-shader-topology", diagnostics);
        }

        var textureBlocks = BlocksOfType(document, "BSShaderTextureSet").Select(block => block.Index).Order();
        var uniqueTargets = textureTargets.Distinct().Order();
        var referenceCounts = textureTargets.GroupBy(target => target).Select(group => group.Count()).Order();
        bool valid = profile switch
        {
            QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape =>
                uniqueTargets.SequenceEqual(textureBlocks) &&
                referenceCounts.SequenceEqual([1, 1, 1, 1, 1, 2]),
            QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete =>
                textureTargets.Count == textureTargets.Distinct().Count() &&
                uniqueTargets.SequenceEqual(textureBlocks),
            QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete =>
                uniqueTargets.SequenceEqual(textureBlocks) &&
                referenceCounts.All(count => count is 1 or 2),
            _ => false
        };
        if (!valid)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-texture-set-topology", DiagnosticSeverity.Error,
                profile switch
                {
                    QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete =>
                        "Every Manager-assembled shader must own one distinct texture set and every texture set must be owned.",
                    QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete =>
                        "Every RaceMenu-exported shader must own one texture set, every texture set must be owned, and a set may be shared by at most two shaders.",
                    _ =>
                        "All six provider-pinned texture sets must be shader-owned, with exactly one legitimate two-shader share."
                }));
        }
    }

    private static void AddOwnedTarget(
        SseNifDocument document,
        SseNifBlock owner,
        string kind,
        ImmutableHashSet<string> allowedTypes,
        ImmutableArray<int>.Builder targets,
        string diagnosticCode,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var references = References(owner, kind);
        if (references.Length != 1 || references[0].Target < 0 ||
            !allowedTypes.Contains(document.Blocks[references[0].Target].Type))
        {
            diagnostics.Add(new Diagnostic(diagnosticCode, DiagnosticSeverity.Error,
                $"Block {owner.Index} must own exactly one non-null {kind} reference of the admitted type."));
            return;
        }
        targets.Add(references[0].Target);
    }

    private static void RequireExactOwnership(
        SseNifDocument document,
        IEnumerable<int> ownedTargets,
        ImmutableHashSet<string> targetTypes,
        string diagnosticCode,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var owned = ownedTargets.Order().ToImmutableArray();
        var expected = document.Blocks.Where(block => targetTypes.Contains(block.Type))
            .Select(block => block.Index)
            .Order()
            .ToImmutableArray();
        if (owned.Length != owned.Distinct().Count() || !owned.SequenceEqual(expected))
            diagnostics.Add(new Diagnostic(diagnosticCode, DiagnosticSeverity.Error,
                "Every admitted topology block must be owned exactly once; unrelated or shared blocks are refused."));
    }

    private static void RequireExactTargets(
        SseNifBlock owner,
        string kind,
        ImmutableArray<int> expected,
        string diagnosticCode,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var actual = References(owner, kind).Select(reference => reference.Target).ToImmutableArray();
        if (!actual.SequenceEqual(expected))
            diagnostics.Add(new Diagnostic(diagnosticCode, DiagnosticSeverity.Error,
                $"Block {owner.Index} {kind} topology does not match the admitted carrier."));
    }

    private static ImmutableArray<SseNifReference> References(SseNifBlock block, string kind) =>
        block.References.Where(reference => string.Equals(reference.Kind, kind, StringComparison.Ordinal))
            .ToImmutableArray();

    private static ImmutableArray<SseNifBlock> BlocksOfType(SseNifDocument document, string type) =>
        document.Blocks.Where(block => string.Equals(block.Type, type, StringComparison.Ordinal))
            .ToImmutableArray();

    private static HashSet<int> Reachable(SseNifDocument document)
    {
        var reachable = document.Roots.ToHashSet();
        var pending = new Stack<int>(document.Roots);
        while (pending.TryPop(out var index))
        {
            foreach (var reference in document.Blocks[index].References)
                if (reference.Target >= 0 && reachable.Add(reference.Target))
                    pending.Push(reference.Target);
        }
        return reachable;
    }

    private static Sha256Hash ComputeGraphHash(SseNifDocument document, HashSet<int> reachable)
    {
        var builder = new StringBuilder();
        builder.Append("roots:");
        foreach (var root in document.Roots)
            builder.Append(root.ToString(CultureInfo.InvariantCulture)).Append(',');
        builder.AppendLine();
        foreach (var index in reachable.Order())
        {
            var block = document.Blocks[index];
            builder.Append(index.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(block.Type).Append('|').Append(block.Name ?? "<null>");
            foreach (var reference in block.References)
                builder.Append('|').Append(reference.Kind).Append(':')
                    .Append(reference.Target.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine();
        }
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(builder.ToString()))));
    }
}

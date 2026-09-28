using System.Buffers.Binary;
using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

internal static partial class SseFaceGeomCarrierCodec
{
    internal static NifTextureTarget? FindFaceTintTarget(
        SseNifDocument document,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        FindFaceTintTarget(
            document,
            QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape,
            diagnostics);

    internal static NifTextureTarget? FindFaceTintTarget(
        SseNifDocument document,
        QualifiedFaceGeomCarrierProfile profile,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var matches = document.Blocks
            .Where(block => string.Equals(block.Type, "BSShaderTextureSet", StringComparison.Ordinal))
            .SelectMany(block => block.Textures.Select((path, slot) =>
                new NifTextureTarget(block.Index, slot, path)))
            .Where(target =>
                IsFaceTintPath(target.OriginalPath) ||
                profile ==
                    QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete &&
                IsRaceMenuCharGenFaceTintPath(target.OriginalPath))
            .ToImmutableArray();
        if (matches.Length != 1)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-facetint-target-count", DiagnosticSeverity.Error,
                $"Expected exactly one embedded FaceTint route, found {matches.Length}."));
            return null;
        }

        var target = matches[0];
        int expectedSlot = profile switch
        {
            QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape => 6,
            QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete => 6,
            QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete => 6,
            _ => -1
        };
        if (target.SlotIndex != expectedSlot)
            diagnostics.Add(new Diagnostic("qualified-carrier-facetint-slot", DiagnosticSeverity.Error,
                $"The embedded FaceTint route is in texture slot {target.SlotIndex}; profile {profile} requires slot {expectedSlot}."));
        SseNifBlock textureSet = document.Blocks[target.BlockIndex];
        if ((profile == QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete ||
             profile == QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete) &&
            (textureSet.Textures.IsDefaultOrEmpty ||
             !IsValidHeadDiffusePath(textureSet.Textures[0])))
            diagnostics.Add(new Diagnostic(
                "qualified-carrier-head-diffuse",
                DiagnosticSeverity.Error,
                "The FaceTint-owning Manager head texture set must retain a nonempty DDS diffuse route in slot 0, separate from FaceTint."));
        var shaders = document.Blocks.Where(block => block.References.Any(reference =>
                string.Equals(reference.Kind, "textureset", StringComparison.Ordinal) &&
                reference.Target == target.BlockIndex))
            .ToImmutableArray();
        var shapes = shaders.Length == 1
            ? document.Blocks.Where(block => block.References.Any(reference =>
                    string.Equals(reference.Kind, "shader", StringComparison.Ordinal) &&
                    reference.Target == shaders[0].Index))
                .ToImmutableArray()
            : [];
        // Manager assembly already admits exactly one typed UsesFaceTint part
        // and binds this route only to that part. Its output name preserves the
        // source HDPT EditorID, which is not required to contain "Head".
        bool ownerNameAccepted =
            profile == QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete ||
            shapes.Length == 1 &&
            shapes[0].Name?.Contains(
                "Head",
                StringComparison.OrdinalIgnoreCase) == true;
        if (shaders.Length != 1 || shapes.Length != 1 ||
            !string.Equals(shapes[0].Type, "BSDynamicTriShape", StringComparison.Ordinal) ||
            !ownerNameAccepted)
            diagnostics.Add(new Diagnostic("qualified-carrier-facetint-head-binding", DiagnosticSeverity.Error,
                "The FaceTint texture set must be referenced by exactly one shader on exactly one dynamic head shape."));
        return target;
    }

    internal static byte[] RewriteFaceTintPath(
        SseNifDocument source,
        NifTextureTarget target,
        AssetPath requestedPath)
    {
        var block = RequireTextureSet(source, target);
        var textures = block.Textures.SetItem(target.SlotIndex, WirePath(requestedPath));
        return RewriteTextureSet(source, target.BlockIndex, block, textures);
    }

    internal static byte[] RewriteHeadTexturePaths(
        SseNifDocument source,
        NifTextureTarget target,
        AssetPath requestedFaceTintPath,
        SkyrimPrivateHeadTexturePaths requestedHeadTextures,
        QualifiedFaceGeomCarrierProfile profile)
    {
        var block = RequireTextureSet(source, target);
        int expectedSlot = profile switch
        {
            QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape => 6,
            QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete => 6,
            QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete => 6,
            _ => -1
        };
        if (target.SlotIndex != expectedSlot)
            throw Invalid(
                $"The private-head route for profile {profile} requires FaceTint texture slot {expectedSlot}.");
        if (block.Textures.Length < 8)
            throw Invalid("The private-head route requires a BSShaderTextureSet with at least eight slots.");

        var textures = block.Textures.ToBuilder();
        textures[0] = WirePath(requestedHeadTextures.Diffuse);
        textures[1] = WirePath(requestedHeadTextures.NormalOrGloss);
        textures[2] = WirePath(requestedHeadTextures.GlowOrDetailMap);
        textures[6] = WirePath(requestedFaceTintPath);
        textures[7] = WirePath(requestedHeadTextures.BacklightMaskOrSpecular);
        return RewriteTextureSet(source, target.BlockIndex, block, textures.ToImmutable());
    }

    private static SseNifBlock RequireTextureSet(
        SseNifDocument source,
        NifTextureTarget target)
    {
        if (target.BlockIndex < 0 || target.BlockIndex >= source.Blocks.Length)
            throw Invalid("The rewrite target block is outside the source block table.");
        var block = source.Blocks[target.BlockIndex];
        if (!string.Equals(block.Type, "BSShaderTextureSet", StringComparison.Ordinal))
            throw Invalid("The rewrite target is not a BSShaderTextureSet block.");
        if (target.SlotIndex < 0 || target.SlotIndex >= block.Textures.Length)
            throw Invalid("The rewrite texture slot is outside the selected texture set.");
        return block;
    }

    internal static byte[] ExtractTextureSetPreimage(
        SseNifDocument source,
        int blockIndex)
    {
        if (blockIndex < 0 || blockIndex >= source.Blocks.Length)
            throw Invalid("The texture-set preimage block is outside the NIF block table.");
        var block = source.Blocks[blockIndex];
        if (!string.Equals(block.Type, "BSShaderTextureSet", StringComparison.Ordinal))
            throw Invalid("The durable preimage target is not a BSShaderTextureSet block.");
        if (block.Size <= 0 || block.Offset < source.BlockDataOffset ||
            block.Offset > source.FooterOffset - block.Size)
            throw Invalid("The texture-set preimage has an invalid source byte range.");
        return source.Data.AsSpan(block.Offset, block.Size).ToArray();
    }

    internal static byte[] ReconstructSourceFromTextureSetPreimage(
        SseNifDocument output,
        int targetBlockIndex,
        ReadOnlySpan<byte> sourcePreimage)
    {
        if (targetBlockIndex < 0 || targetBlockIndex >= output.Blocks.Length)
            throw Invalid("The durable preimage target is outside the output block table.");
        var outputBlock = output.Blocks[targetBlockIndex];
        if (!string.Equals(outputBlock.Type, "BSShaderTextureSet", StringComparison.Ordinal))
            throw Invalid("The durable preimage target is not a BSShaderTextureSet block.");
        if (sourcePreimage.IsEmpty)
            throw Invalid("The durable texture-set preimage is empty.");

        var sourceLength = checked(output.Data.Length - outputBlock.Size + sourcePreimage.Length);
        var source = new byte[sourceLength];
        output.Data.AsSpan(0, output.BlockDataOffset).CopyTo(source);
        BinaryPrimitives.WriteUInt32LittleEndian(
            source.AsSpan(
                output.BlockSizeTableOffset + (sizeof(uint) * targetBlockIndex),
                sizeof(uint)),
            checked((uint)sourcePreimage.Length));

        var destination = output.BlockDataOffset;
        foreach (var current in output.Blocks)
        {
            var bytes = current.Index == targetBlockIndex
                ? sourcePreimage
                : output.Data.AsSpan(current.Offset, current.Size);
            bytes.CopyTo(source.AsSpan(destination));
            destination += bytes.Length;
        }
        output.Data.AsSpan(output.FooterOffset).CopyTo(source.AsSpan(destination));
        return source;
    }

    private static byte[] RewriteTextureSet(
        SseNifDocument source,
        int targetBlockIndex,
        SseNifBlock block,
        ImmutableArray<string> textures)
    {
        if (textures.Length != block.Textures.Length)
            throw Invalid("The rewrite must preserve the BSShaderTextureSet slot count.");
        var replacement = SerializeTextureSet(textures);
        var outputLength = checked(source.Data.Length - block.Size + replacement.Length);
        var output = new byte[outputLength];
        source.Data.AsSpan(0, source.BlockDataOffset).CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(source.BlockSizeTableOffset + (sizeof(uint) * targetBlockIndex), sizeof(uint)),
            checked((uint)replacement.Length));

        var destination = source.BlockDataOffset;
        foreach (var current in source.Blocks)
        {
            var bytes = current.Index == targetBlockIndex
                ? replacement.AsSpan()
                : source.Data.AsSpan(current.Offset, current.Size);
            bytes.CopyTo(output.AsSpan(destination));
            destination += bytes.Length;
        }
        source.Data.AsSpan(source.FooterOffset).CopyTo(output.AsSpan(destination));
        return output;
    }

    internal static ImmutableArray<int> VerifyRewrite(
        SseNifDocument source,
        SseNifDocument output,
        NifTextureTarget target,
        AssetPath requestedPath,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        VerifyRewrite(
            source,
            output,
            target,
            requestedPath,
            null,
            QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape,
            diagnostics);

    internal static ImmutableArray<int> VerifyRewrite(
        SseNifDocument source,
        SseNifDocument output,
        NifTextureTarget target,
        AssetPath requestedPath,
        SkyrimPrivateHeadTexturePaths? requestedHeadTextures,
        QualifiedFaceGeomCarrierProfile profile,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var changed = ImmutableArray.CreateBuilder<int>();
        var blockCountMatches = source.Blocks.Length == output.Blocks.Length;
        if (!blockCountMatches ||
            !source.TypeNames.SequenceEqual(output.TypeNames, StringComparer.Ordinal) ||
            !source.Strings.SequenceEqual(output.Strings, StringComparer.Ordinal) ||
            !source.Roots.SequenceEqual(output.Roots) ||
            source.BlockDataOffset != output.BlockDataOffset)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-nif-envelope-drift", DiagnosticSeverity.Error,
                "The NIF type table, string table, roots, block count, or block-data boundary changed."));
        }

        if (!blockCountMatches || target.BlockIndex < 0 ||
            target.BlockIndex >= source.Blocks.Length || target.BlockIndex >= output.Blocks.Length)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-rewrite-target-unavailable", DiagnosticSeverity.Error,
                "The selected texture-set block is not present at the same index in both NIFs."));
            return changed.ToImmutable();
        }

        if (!HeaderMatchesExceptTargetSize(source, output, target.BlockIndex))
            diagnostics.Add(new Diagnostic("qualified-carrier-header-change-surface", DiagnosticSeverity.Error,
                "Header bytes changed outside the selected block-size table entry."));

        for (var index = 0; index < source.Blocks.Length; index++)
        {
            var before = source.Blocks[index];
            var after = output.Blocks[index];
            if (!source.Data.AsSpan(before.Offset, before.Size)
                    .SequenceEqual(output.Data.AsSpan(after.Offset, after.Size)))
                changed.Add(index);
            if (index != target.BlockIndex && before.Size != after.Size)
                diagnostics.Add(new Diagnostic("qualified-carrier-unapproved-block-size", DiagnosticSeverity.Error,
                    $"Unapproved block {index} changed size."));
        }

        if (!changed.SequenceEqual([target.BlockIndex]))
            diagnostics.Add(new Diagnostic("qualified-carrier-changed-blocks", DiagnosticSeverity.Error,
                $"Changed blocks were [{string.Join(", ", changed)}]; only BSShaderTextureSet block {target.BlockIndex} is allowed."));

        var sourceBlock = source.Blocks[target.BlockIndex];
        var outputBlock = output.Blocks[target.BlockIndex];
        var requestedWirePath = WirePath(requestedPath);
        int expectedFaceTintSlot = profile switch
        {
            QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape => 6,
            QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete => 6,
            QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete => 6,
            _ => -1
        };
        var privateRouteShapeMatches = requestedHeadTextures is null ||
                                       target.SlotIndex == expectedFaceTintSlot &&
                                       sourceBlock.Textures.Length >= 8;
        if (!privateRouteShapeMatches)
            diagnostics.Add(new Diagnostic("qualified-carrier-private-head-texture-shape",
                DiagnosticSeverity.Error,
                $"The private-head route for profile {profile} requires FaceTint slot {expectedFaceTintSlot} and at least eight texture slots."));
        if (!string.Equals(outputBlock.Type, "BSShaderTextureSet", StringComparison.Ordinal) ||
            sourceBlock.Textures.Length != outputBlock.Textures.Length)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-texture-set-shape", DiagnosticSeverity.Error,
                "The selected BSShaderTextureSet shape changed."));
        }
        else
        {
            if ((profile ==
                    QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete ||
                 profile ==
                    QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete) &&
                (outputBlock.Textures.IsDefaultOrEmpty ||
                 !IsValidHeadDiffusePath(outputBlock.Textures[0])))
                diagnostics.Add(new Diagnostic(
                    "qualified-carrier-head-diffuse",
                    DiagnosticSeverity.Error,
                    "The rewritten Manager head texture set must retain a nonempty DDS diffuse route in slot 0, separate from FaceTint."));
            for (var slot = 0; slot < sourceBlock.Textures.Length; slot++)
            {
                var expected = requestedHeadTextures is null
                    ? slot == target.SlotIndex ? requestedWirePath : sourceBlock.Textures[slot]
                    : profile switch
                    {
                        QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape =>
                            slot switch
                            {
                                0 => WirePath(requestedHeadTextures.Diffuse),
                                1 => WirePath(requestedHeadTextures.NormalOrGloss),
                                2 => WirePath(requestedHeadTextures.GlowOrDetailMap),
                                6 => requestedWirePath,
                                7 => WirePath(requestedHeadTextures.BacklightMaskOrSpecular),
                                _ => sourceBlock.Textures[slot]
                            },
                        QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete =>
                            slot switch
                            {
                                0 => WirePath(requestedHeadTextures.Diffuse),
                                1 => WirePath(requestedHeadTextures.NormalOrGloss),
                                2 => WirePath(requestedHeadTextures.GlowOrDetailMap),
                                6 => requestedWirePath,
                                7 => WirePath(requestedHeadTextures.BacklightMaskOrSpecular),
                                _ => sourceBlock.Textures[slot]
                            },
                        QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete =>
                            slot switch
                            {
                                0 => WirePath(requestedHeadTextures.Diffuse),
                                1 => WirePath(requestedHeadTextures.NormalOrGloss),
                                2 => WirePath(requestedHeadTextures.GlowOrDetailMap),
                                6 => requestedWirePath,
                                7 => WirePath(requestedHeadTextures.BacklightMaskOrSpecular),
                                _ => sourceBlock.Textures[slot]
                            },
                        _ => sourceBlock.Textures[slot]
                    };
                if (!string.Equals(outputBlock.Textures[slot], expected, StringComparison.Ordinal))
                    diagnostics.Add(new Diagnostic("qualified-carrier-texture-slot-drift", DiagnosticSeverity.Error,
                        $"Texture slot {slot} does not match the authorized rewrite."));
            }
        }

        var targetOccurrences = output.Blocks
            .Where(block => string.Equals(block.Type, "BSShaderTextureSet", StringComparison.Ordinal))
            .SelectMany(block => block.Textures)
            .Count(path => string.Equals(path, requestedWirePath, StringComparison.Ordinal));
        var oldOccurrences = output.Blocks
            .Where(block => string.Equals(block.Type, "BSShaderTextureSet", StringComparison.Ordinal))
            .SelectMany(block => block.Textures)
            .Count(path => string.Equals(path, target.OriginalPath, StringComparison.Ordinal));
        if (targetOccurrences != 1 || oldOccurrences != 0)
            diagnostics.Add(new Diagnostic("qualified-carrier-facetint-postwrite-path", DiagnosticSeverity.Error,
                "The output must contain the exact requested FaceTint route once and the original route zero times."));

        if (!source.Data.AsSpan(source.FooterOffset).SequenceEqual(output.Data.AsSpan(output.FooterOffset)))
            diagnostics.Add(new Diagnostic("qualified-carrier-footer-drift", DiagnosticSeverity.Error,
                "The NIF footer changed during carrier materialization."));
        return changed.ToImmutable();
    }

    private static byte[] SerializeTextureSet(ImmutableArray<string> textures)
    {
        var encoded = textures.Select(NifEncoding.GetBytes).ToImmutableArray();
        var length = checked(sizeof(uint) + encoded.Sum(item => checked(sizeof(uint) + item.Length)));
        var bytes = new byte[length];
        var position = 0;
        WriteUInt32(bytes, ref position, checked((uint)textures.Length));
        foreach (var item in encoded)
        {
            WriteUInt32(bytes, ref position, checked((uint)item.Length));
            item.CopyTo(bytes.AsSpan(position));
            position += item.Length;
        }
        if (position != bytes.Length) throw Invalid("Texture-set serialization length mismatch.");
        return bytes;
    }

    private static string WirePath(AssetPath path) => path.Value.Replace('/', '\\');

    private static bool HeaderMatchesExceptTargetSize(
        SseNifDocument source,
        SseNifDocument output,
        int targetBlock)
    {
        if (targetBlock < 0 || targetBlock >= source.Blocks.Length ||
            targetBlock >= output.Blocks.Length ||
            source.BlockDataOffset != output.BlockDataOffset ||
            source.BlockSizeTableOffset != output.BlockSizeTableOffset)
            return false;

        var skipStart = source.BlockSizeTableOffset + (sizeof(uint) * targetBlock);
        var skipEnd = skipStart + sizeof(uint);
        if (skipStart < 0 || skipEnd > source.BlockDataOffset || skipEnd > output.BlockDataOffset)
            return false;

        return source.Data.AsSpan(0, skipStart).SequenceEqual(output.Data.AsSpan(0, skipStart)) &&
               source.Data.AsSpan(skipEnd, source.BlockDataOffset - skipEnd)
                   .SequenceEqual(output.Data.AsSpan(skipEnd, output.BlockDataOffset - skipEnd));
    }

    private static bool IsFaceTintPath(string path)
    {
        var normalized = path.Trim().TrimStart('\\', '/').Replace('\\', '/');
        if (normalized.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[5..];
        if (!normalized.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
            normalized = "textures/" + normalized;
        var segments = normalized.Split('/');
        return segments.Length == 7 &&
               string.Equals(segments[0], "textures", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(segments[1], "actors", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(segments[2], "character", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(segments[3], "FaceGenData", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(segments[4], "FaceTint", StringComparison.OrdinalIgnoreCase) &&
               (segments[5].EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
                segments[5].EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
                segments[5].EndsWith(".esl", StringComparison.OrdinalIgnoreCase)) &&
               segments[6] is { Length: 12 } &&
               segments[6].EndsWith(".dds", StringComparison.OrdinalIgnoreCase) &&
               segments[6].AsSpan(0, 8).ContainsOnlyHexDigits();
    }

    private static bool IsRaceMenuCharGenFaceTintPath(string path)
    {
        string normalized = path.Trim()
            .TrimStart('\\', '/')
            .Replace('\\', '/');
        string[] segments = normalized.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        int offset = segments.Length > 0 &&
                     string.Equals(
                         segments[0],
                         "data",
                         StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
        return segments.Length - offset == 4 &&
               string.Equals(
                   segments[offset],
                   "skse",
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   segments[offset + 1],
                   "plugins",
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   segments[offset + 2],
                   "chargen",
                   StringComparison.OrdinalIgnoreCase) &&
               segments[offset + 3].Length is > 4 and <= 260 &&
               segments[offset + 3].EndsWith(
                   ".dds",
                   StringComparison.OrdinalIgnoreCase) &&
               segments[offset + 3].All(character =>
                   character is >= ' ' and <= '~' &&
                   character is not ':' and not '\\' and not '/');
    }

    internal static bool IsValidHeadDiffusePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string normalized = path.Trim().Replace('\\', '/');
        if (normalized[0] == '/' ||
            normalized.Contains(':') ||
            !normalized.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] segments = normalized.Split('/');
        if (segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                !string.Equals(segment, segment.Trim(),
                    StringComparison.Ordinal) ||
                segment is "." or ".."))
            return false;
        for (int index = 0; index <= segments.Length - 4; index++)
        {
            if (string.Equals(segments[index], "Actors",
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(segments[index + 1], "Character",
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(segments[index + 2], "FaceGenData",
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(segments[index + 3], "FaceTint",
                    StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}

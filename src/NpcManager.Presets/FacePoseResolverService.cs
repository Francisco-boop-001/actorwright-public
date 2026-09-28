using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

/// <summary>Reads the small, explicit face-pose interchange document used by the CLI.</summary>
internal static class FacePosePresetCodec
{
    internal const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxRegions = 4096;
    private const int MaxBones = 100_000;
    private const int MaxFaceMorphs = 4096;
    private const int MaxChannels = 2048;
    private const int MaxVertexRows = 200_000;
    private const int MaxNameLength = 256;
    private const float MaxCoordinate = 100_000F;

    internal static bool TryParse(byte[] bytes, GameEdition requestedEdition, FormId requestedNpc,
        out FacePoseInput? input, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        input = null;
        try
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                bytes = bytes[3..];
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 16
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("Face-pose preset root must be an object.");

            RequireFields(document.RootElement,
                ["version", "game", "npc", "facialMorphIntensity", "regions", "faceMorphs", "vertexMorphs"]);
            var version = ReadInt(document.RootElement, "version", 1, 1_000_000);
            if (version != 1) throw new FormatException("Face-pose preset version must be 1.");
            var editionText = ReadString(document.RootElement, "game", 32);
            if (!GameEditionExtensions.TryParseWireName(editionText, out var edition))
                throw new FormatException("Face-pose preset game must be fallout4 or skyrimse.");
            if (edition != requestedEdition)
            {
                diagnostics.Add(new Diagnostic("face-pose-game-mismatch", DiagnosticSeverity.Error,
                    "The preset game does not match --game."));
            }

            var npcText = ReadString(document.RootElement, "npc", 32);
            if (!FormId.TryParse(npcText, out var npc) || npc.Value == 0)
                throw new FormatException("Face-pose preset npc must be a non-zero hexadecimal FormID.");
            if (npc != requestedNpc)
            {
                diagnostics.Add(new Diagnostic("face-pose-npc-mismatch", DiagnosticSeverity.Error,
                    "The preset NPC does not match --npc."));
            }

            var intensity = ReadFiniteProperty(document.RootElement, "facialMorphIntensity", -100F, 100F);
            var regions = ReadRegions(document.RootElement.GetProperty("regions"), diagnostics);
            var faceMorphs = ReadFaceMorphs(document.RootElement.GetProperty("faceMorphs"));
            var vertexMorphs = ReadVertexMorphs(document.RootElement.GetProperty("vertexMorphs"));
            input = new FacePoseInput(version, edition, npc, intensity, regions, faceMorphs, vertexMorphs);
            return true;
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("face-pose-json-invalid", DiagnosticSeverity.Error, exception.Message));
        }
        catch (FormatException exception)
        {
            diagnostics.Add(new Diagnostic("face-pose-format-invalid", DiagnosticSeverity.Error, exception.Message));
        }
        return false;
    }

    private static ImmutableArray<FaceBoneRegion> ReadRegions(JsonElement element,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() is 0 or > MaxRegions)
            throw new FormatException($"regions must contain 1 to {MaxRegions} objects.");
        var result = ImmutableArray.CreateBuilder<FaceBoneRegion>();
        var ids = new HashSet<int>();
        var boneCount = 0;
        foreach (var regionElement in element.EnumerateArray())
        {
            RequireFields(regionElement, ["id", "name", "default", "bones"]);
            var id = ReadInt(regionElement, "id", 0, 1_000_000);
            if (!ids.Add(id)) throw new FormatException($"regions contains duplicate id {id}.");
            var name = ReadSafeName(regionElement, "name");
            var defaults = ReadTransform(regionElement.GetProperty("default"), "region default");
            var bonesElement = regionElement.GetProperty("bones");
            if (bonesElement.ValueKind != JsonValueKind.Array || bonesElement.GetArrayLength() is 0 or > MaxRegions)
                throw new FormatException($"Region {id} bones must contain 1 to {MaxRegions} objects.");
            var bones = ImmutableArray.CreateBuilder<FaceBoneRegionBone>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var boneElement in bonesElement.EnumerateArray())
            {
                RequireFields(boneElement, ["bone", "min", "max"]);
                var bone = ReadSafeName(boneElement, "bone");
                if (!names.Add(bone)) throw new FormatException($"Region {id} contains duplicate bone '{bone}'.");
                var minimum = ReadTransform(boneElement.GetProperty("min"), $"region {id} minimum");
                var maximum = ReadTransform(boneElement.GetProperty("max"), $"region {id} maximum");
                bones.Add(new FaceBoneRegionBone(bone, minimum.Position, maximum.Position,
                    minimum.Rotation, maximum.Rotation, minimum.Scale, maximum.Scale));
                boneCount++;
                if (boneCount > MaxBones) throw new FormatException($"Face-pose preset exceeds {MaxBones} bones.");
            }
            result.Add(new FaceBoneRegion(id, name, defaults.Position, defaults.Rotation, defaults.Scale,
                bones.ToImmutable()));
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<FaceMorphSlider> ReadFaceMorphs(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaxFaceMorphs)
            throw new FormatException($"faceMorphs may contain at most {MaxFaceMorphs} objects.");
        var result = ImmutableArray.CreateBuilder<FaceMorphSlider>();
        foreach (var morphElement in element.EnumerateArray())
        {
            RequireFields(morphElement, ["regionId", "position", "rotation", "scale"]);
            result.Add(new FaceMorphSlider(
                ReadInt(morphElement, "regionId", 0, 1_000_000),
                ReadVector(morphElement.GetProperty("position"), "face morph position"),
                ReadVector(morphElement.GetProperty("rotation"), "face morph rotation"),
                ReadFiniteProperty(morphElement, "scale", -100F, 100F)));
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<FaceVertexMorphChannel> ReadVertexMorphs(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaxChannels)
            throw new FormatException($"vertexMorphs may contain at most {MaxChannels} objects.");
        var result = ImmutableArray.CreateBuilder<FaceVertexMorphChannel>();
        var rowCount = 0;
        foreach (var channelElement in element.EnumerateArray())
        {
            RequireFields(channelElement, ["resolver", "name", "weight", "vertices"]);
            var resolver = ReadSafeName(channelElement, "resolver");
            var name = ReadSafeName(channelElement, "name");
            var weight = ReadFiniteProperty(channelElement, "weight", -1F, 1F);
            var verticesElement = channelElement.GetProperty("vertices");
            if (verticesElement.ValueKind != JsonValueKind.Array || verticesElement.GetArrayLength() > MaxVertexRows)
                throw new FormatException($"Vertex morph '{name}' has too many vertices.");
            var vertices = ImmutableArray.CreateBuilder<FaceVertexDelta>();
            var indices = new HashSet<int>();
            foreach (var vertexElement in verticesElement.EnumerateArray())
            {
                RequireFields(vertexElement, ["index", "delta"]);
                var index = ReadInt(vertexElement, "index", 0, 10_000_000);
                if (!indices.Add(index)) throw new FormatException($"Vertex morph '{name}' contains duplicate index {index}.");
                vertices.Add(new FaceVertexDelta(index, ReadVector(vertexElement.GetProperty("delta"), "vertex delta")));
                rowCount++;
                if (rowCount > MaxVertexRows) throw new FormatException($"Face-pose preset exceeds {MaxVertexRows} vertex rows.");
            }
            result.Add(new FaceVertexMorphChannel(resolver, name, weight, vertices.ToImmutable()));
        }
        return result.ToImmutable();
    }

    private static (FacePoseVector Position, FacePoseVector Rotation, FacePoseVector Scale) ReadTransform(
        JsonElement element, string description)
    {
        RequireFields(element, ["position", "rotation", "scale"]);
        return (ReadVector(element.GetProperty("position"), $"{description} position"),
            ReadVector(element.GetProperty("rotation"), $"{description} rotation"),
            ReadVector(element.GetProperty("scale"), $"{description} scale"));
    }

    private static FacePoseVector ReadVector(JsonElement element, string description)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 3)
            throw new FormatException($"{description} must be a three-number array.");
        var values = element.EnumerateArray().Select(item => ReadFinite(item, description, -MaxCoordinate, MaxCoordinate)).ToArray();
        return new FacePoseVector(values[0], values[1], values[2]);
    }

    private static int ReadInt(JsonElement parent, string name, int minimum, int maximum)
    {
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out var value) || value < minimum || value > maximum)
            throw new FormatException($"{name} must be an integer from {minimum} to {maximum}.");
        return value;
    }

    private static float ReadFiniteProperty(JsonElement parent, string name, float minimum, float maximum) =>
        !parent.TryGetProperty(name, out var element)
            ? throw new FormatException($"{name} is required.")
            : ReadFinite(element, name, minimum, maximum);

    private static float ReadFinite(JsonElement element, string name, float minimum, float maximum)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetSingle(out var value) ||
            !float.IsFinite(value) || value < minimum || value > maximum)
            throw new FormatException($"{name} must be a finite number from {minimum.ToString(CultureInfo.InvariantCulture)} to {maximum.ToString(CultureInfo.InvariantCulture)}.");
        return value == 0 ? 0 : value;
    }

    private static string ReadString(JsonElement parent, string name, int maxLength)
    {
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            throw new FormatException($"{name} must be a string.");
        var value = element.GetString() ?? string.Empty;
        if (value.Length == 0 || value.Length > maxLength || value.Any(char.IsControl))
            throw new FormatException($"{name} must be printable and at most {maxLength} characters.");
        return value;
    }

    private static string ReadSafeName(JsonElement parent, string name) => ReadString(parent, name, MaxNameLength);

    private static void RequireFields(JsonElement element, params string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException("Face-pose entries must be objects.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name)) throw new FormatException($"Duplicate field '{property.Name}'.");
        foreach (var name in required)
            if (!names.Contains(name)) throw new FormatException($"Required field '{name}' is missing.");
        if (names.Any(name => !required.Contains(name)))
            throw new FormatException("Face-pose input contains an unknown field.");
    }
}

/// <summary>Implements the pinned additive FMRS and MultiMorphResolver semantics.</summary>
public sealed class FacePoseResolverService(IWorkspacePolicy policy, WorkspacePath labRoot) : IFacePoseResolver
{
    private static readonly Sha256Hash EmptyHash = new(new string('0', 64));

    public async ValueTask<FacePoseResolveResult> ResolveAsync(FacePoseResolveRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ValidateSource(request.PresetPath).ToBuilder();
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return EmptyResult(request, diagnostics.ToImmutable());

        try
        {
            var bytes = await File.ReadAllBytesAsync(request.PresetPath.Value, cancellationToken);
            if (bytes.Length > FacePosePresetCodec.MaxBytes)
            {
                diagnostics.Add(new Diagnostic("face-pose-size-limit", DiagnosticSeverity.Error,
                    $"Face-pose input exceeds the {FacePosePresetCodec.MaxBytes} byte safety limit."));
                return EmptyResult(request, diagnostics.ToImmutable());
            }
            var hash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            var parseDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            if (!FacePosePresetCodec.TryParse(bytes, request.Edition, request.NpcFormId, out var input, parseDiagnostics) || input is null)
            {
                diagnostics.AddRange(parseDiagnostics);
                return EmptyResult(request, diagnostics.ToImmutable(), hash);
            }
            diagnostics.AddRange(parseDiagnostics);
            var resolved = ResolveMath(input, diagnostics);
            return resolved with { SourceHash = hash };
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("face-pose-read-failed", DiagnosticSeverity.Error, exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("face-pose-read-denied", DiagnosticSeverity.Error, exception.Message));
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(new Diagnostic("face-pose-math-invalid", DiagnosticSeverity.Error, exception.Message));
        }
        return EmptyResult(request, diagnostics.ToImmutable());
    }

    private static FacePoseResolveResult ResolveMath(FacePoseInput input, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var regionMap = input.Regions.ToDictionary(region => region.Id);
        var positions = new Dictionary<string, DoubleVector>(StringComparer.OrdinalIgnoreCase);
        var rotations = new Dictionary<string, DoubleVector>(StringComparer.OrdinalIgnoreCase);
        var scales = new Dictionary<string, DoubleVector>(StringComparer.OrdinalIgnoreCase);
        var fmin = input.FacialMorphIntensity <= 0 ? 1D : input.FacialMorphIntensity;
        var contributed = 0;

        foreach (var morph in input.FaceMorphs)
        {
            if (!regionMap.TryGetValue(morph.RegionId, out var region))
            {
                diagnostics.Add(new Diagnostic("face-pose-region-unresolved", DiagnosticSeverity.Warning,
                    $"Face morph region {morph.RegionId} is not declared and was skipped."));
                continue;
            }
            if (IsZero(morph.Position) && IsZero(morph.Rotation) && MathF.Abs(morph.Scale) < 0.0001F) continue;
            foreach (var bone in region.Bones)
            {
                var target = "skin_" + bone.Bone;
                var position = new DoubleVector(
                    Lerp(morph.Position.X, bone.MinPosition.X, bone.MaxPosition.X) * fmin,
                    Lerp(morph.Position.Y, bone.MinPosition.Y, bone.MaxPosition.Y) * fmin,
                    Lerp(morph.Position.Z, bone.MinPosition.Z, bone.MaxPosition.Z) * fmin);
                var rotation = new DoubleVector(
                    Lerp(morph.Rotation.X, bone.MinRotation.X, bone.MaxRotation.X) * fmin,
                    Lerp(morph.Rotation.Y, bone.MinRotation.Y, bone.MaxRotation.Y) * fmin,
                    Lerp(morph.Rotation.Z, bone.MinRotation.Z, bone.MaxRotation.Z) * fmin);
                var scale = new DoubleVector(
                    Lerp(morph.Scale, bone.MinScale.X, bone.MaxScale.X) * fmin,
                    Lerp(morph.Scale, bone.MinScale.Y, bone.MaxScale.Y) * fmin,
                    Lerp(morph.Scale, bone.MinScale.Z, bone.MaxScale.Z) * fmin);
                positions[target] = positions.GetValueOrDefault(target) + position;
                rotations[target] = rotations.GetValueOrDefault(target) + rotation;
                scales[target] = scales.GetValueOrDefault(target) + scale;
                contributed++;
            }
        }

        var bones = ImmutableArray.CreateBuilder<FaceBonePose>();
        foreach (var bone in positions.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var rotation = ToBoneRotation(rotations[bone.Key]);
            var scale = scales[bone.Key];
            var pose = new FaceBonePose(bone.Key, bone.Value.ToVector(), rotation,
                new FacePoseVector(ToFiniteFloat(1 + scale.X), ToFiniteFloat(1 + scale.Y), ToFiniteFloat(1 + scale.Z)));
            bones.Add(pose);
        }
        if (contributed == 0)
            diagnostics.Add(new Diagnostic("face-pose-no-contribution", DiagnosticSeverity.Warning,
                "No declared FMRS slider contributed a face-bone transform."));

        var vertexSums = new SortedDictionary<int, DoubleVector>();
        var appliedChannels = ImmutableArray.CreateBuilder<string>();
        var order = ImmutableArray.CreateBuilder<string>();
        order.Add("face-bones:region-declaration-order;bone-declaration-order");
        foreach (var (channel, index) in input.VertexMorphs.Select((value, index) => (value, index)))
        {
            var channelName = $"vertex[{index}]:{channel.Resolver}/{channel.Name}";
            order.Add(channelName);
            appliedChannels.Add(channelName);
            foreach (var vertex in channel.Vertices)
            {
                var delta = new DoubleVector(vertex.Delta.X * channel.Weight,
                    vertex.Delta.Y * channel.Weight, vertex.Delta.Z * channel.Weight);
                vertexSums[vertex.Index] = vertexSums.GetValueOrDefault(vertex.Index) + delta;
            }
        }
        var vertices = vertexSums.Select(item => new FaceVertexDeltaResult(item.Key, item.Value.ToVector())).ToImmutableArray();
        return new FacePoseResolveResult(input, EmptyHash, bones.ToImmutable(), vertices,
            appliedChannels.ToImmutable(), order.ToImmutable(), diagnostics.ToImmutable());
    }

    private ImmutableArray<Diagnostic> ValidateSource(WorkspacePath source)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!source.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("face-pose-input-outside-lab", DiagnosticSeverity.Error,
                "Face-pose input must remain under the K-only lab root."));
        if (!File.Exists(source.Value))
            diagnostics.Add(new Diagnostic("face-pose-input-missing", DiagnosticSeverity.Error,
                "The explicit face-pose input does not exist."));
        else if (File.GetAttributes(source.Value).HasFlag(FileAttributes.ReparsePoint))
            diagnostics.Add(new Diagnostic("face-pose-input-reparse", DiagnosticSeverity.Error,
                "Face-pose input may not be a reparse point."));
        var parent = Path.GetDirectoryName(source.Value);
        if (parent is null)
            diagnostics.Add(new Diagnostic("face-pose-input-parent-invalid", DiagnosticSeverity.Error,
                "Face-pose input has no parent directory."));
        else
            diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (File.Exists(source.Value) && new FileInfo(source.Value).Length > FacePosePresetCodec.MaxBytes)
            diagnostics.Add(new Diagnostic("face-pose-size-limit", DiagnosticSeverity.Error,
                $"Face-pose input exceeds the {FacePosePresetCodec.MaxBytes} byte safety limit."));
        return diagnostics.ToImmutable();
    }

    private static FacePoseResolveResult EmptyResult(FacePoseResolveRequest request,
        ImmutableArray<Diagnostic> diagnostics, Sha256Hash? sourceHash = null)
    {
        var input = new FacePoseInput(1, request.Edition, request.NpcFormId, 1, [], [], []);
        return new FacePoseResolveResult(input, sourceHash ?? EmptyHash, [], [], [], [], diagnostics);
    }

    private static bool IsZero(FacePoseVector value) => MathF.Abs(value.X) < 0.0001F &&
        MathF.Abs(value.Y) < 0.0001F && MathF.Abs(value.Z) < 0.0001F;

    private static double Lerp(float slider, float minimum, float maximum)
    {
        var s = Math.Clamp((double)slider, -1D, 1D);
        return s >= 0 ? s * maximum : -s * minimum;
    }

    private static FacePoseVector ToBoneRotation(DoubleVector source)
    {
        var yaw = -source.X * Math.PI / 180D;
        var pitch = -source.Y * Math.PI / 180D;
        var roll = -source.Z * Math.PI / 180D;
        var cz = Math.Cos(yaw); var sz = Math.Sin(yaw);
        var cy = Math.Cos(pitch); var sy = Math.Sin(pitch);
        var cx = Math.Cos(roll); var sx = Math.Sin(roll);
        var a11 = cy; var a12 = 0D; var a13 = sy;
        var a21 = sx * sy; var a22 = cx; var a23 = -sx * cy;
        var a31 = -cx * sy; var a32 = sx; var a33 = cx * cy;
        var m11 = a11 * cz + a12 * sz + a13 * 0;
        var m12 = -a11 * sz + a12 * cz + a13 * 0;
        var m13 = a13;
        var m21 = a21 * cz + a22 * sz + a23 * 0;
        var m22 = -a21 * sz + a22 * cz + a23 * 0;
        var m23 = a23;
        var m31 = a31 * cz + a32 * sz + a33 * 0;
        var m32 = -a31 * sz + a32 * cz + a33 * 0;
        var m33 = a33;
        var r11 = m33; var r12 = m32; var r13 = m31;
        var r21 = m23; var r22 = m22; var r23 = m21;
        var r31 = m13; var r32 = m12; var r33 = m11;
        var cosine = Math.Clamp((r11 + r22 + r33 - 1D) / 2D, -1D, 1D);
        var angle = Math.Acos(cosine);
        var sine = Math.Sin(angle);
        double ux, uy, uz;
        if (Math.Abs(sine) < 0.0001D)
        {
            ux = (r32 - r23) * 0.5D;
            uy = (r13 - r31) * 0.5D;
            uz = (r21 - r12) * 0.5D;
            var length = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            if (length > 0.000001D)
            {
                ux = ux / length * angle; uy = uy / length * angle; uz = uz / length * angle;
            }
            else { ux = angle; uy = 0; uz = 0; }
        }
        else
        {
            var factor = angle / (2D * sine);
            ux = (r32 - r23) * factor; uy = (r13 - r31) * factor; uz = (r21 - r12) * factor;
        }
        return new FacePoseVector(ToFiniteFloat(ux), ToFiniteFloat(uy), ToFiniteFloat(uz));
    }

    private static float ToFiniteFloat(double value)
    {
        if (!double.IsFinite(value) || value < float.MinValue || value > float.MaxValue)
            throw new InvalidDataException("Face-pose math produced a non-finite result.");
        return (float)value;
    }

    private readonly record struct DoubleVector(double X, double Y, double Z)
    {
        public static DoubleVector operator +(DoubleVector left, DoubleVector right) =>
            new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

        public FacePoseVector ToVector() => new(ToFiniteFloat(X), ToFiniteFloat(Y), ToFiniteFloat(Z));
    }
}

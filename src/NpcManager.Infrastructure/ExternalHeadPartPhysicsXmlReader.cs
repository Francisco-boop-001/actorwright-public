using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Xml;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

internal sealed record ExternalHeadPartPhysicsColliderEvidence(
    AssetPath Path,
    Sha256Hash Sha256,
    long ByteLength);

internal sealed record ExternalHeadPartPhysicsFileReadEvidence(
    byte[] Bytes,
    Sha256Hash Sha256,
    long ByteLength);

internal sealed class ExternalHeadPartPhysicsAssetMissingException : IOException
{
    internal ExternalHeadPartPhysicsAssetMissingException(string message)
        : base(message)
    {
    }
}

internal sealed class ExternalHeadPartPhysicsAssetDriftException : IOException
{
    internal ExternalHeadPartPhysicsAssetDriftException(string message)
        : base(message)
    {
    }
}

internal sealed record ExternalHeadPartPhysicsXmlDocument(
    AssetPath Path,
    Sha256Hash Sha256,
    long ByteLength,
    ImmutableArray<string> BoneNames,
    ImmutableArray<ExternalHeadPartPhysicsColliderEvidence> Colliders);

internal sealed record ExternalHeadPartPhysicsXmlReadResult(
    bool Accepted,
    ExternalHeadPartPhysicsXmlDocument? Document,
    ImmutableArray<Diagnostic> Diagnostics);

internal sealed record ExternalHeadPartDefaultBbpEntry(
    string ShapeName,
    AssetPath XmlPath);

internal sealed record ExternalHeadPartDefaultBbpMap(
    AssetPath Path,
    Sha256Hash Sha256,
    long ByteLength,
    ImmutableArray<ExternalHeadPartDefaultBbpEntry> Entries);

internal sealed record ExternalHeadPartDefaultBbpReadResult(
    bool Accepted,
    ExternalHeadPartDefaultBbpMap? Mapping,
    ImmutableArray<Diagnostic> Diagnostics);

internal static class ExternalHeadPartPhysicsPathRules
{
    private const int MaximumPortableTokenLength = 1024;
    private const int MaximumPortablePathLength = 4096;
    private static readonly ImmutableHashSet<string> ReservedDeviceNames =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "AUX", "CLOCK$", "COM1", "COM2", "COM3", "COM4", "COM5",
            "COM6", "COM7", "COM8", "COM9", "CON", "CONIN$", "CONOUT$",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7",
            "LPT8", "LPT9", "NUL", "PRN");

    internal static bool IsSafePortableToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumPortableTokenLength ||
            value.Any(char.IsControl) || value.Contains('\0') ||
            value.Contains(':') || value.Contains('/') || value.Contains('\\'))
            return false;
        return !value.Contains("..", StringComparison.Ordinal) &&
            !ContainsReservedDeviceSegment(value);
    }

    internal static bool TryCreateAssetPath(
        string raw,
        string requiredPrefix,
        string requiredExtension,
        out AssetPath path)
    {
        path = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string normalized = raw.Trim().Replace('\\', '/');
        if (normalized.Length > MaximumPortablePathLength ||
            normalized.Any(char.IsControl) || normalized.Contains('\0') ||
            normalized.StartsWith('/') ||
            normalized.Contains(':') || normalized.Contains("//",
                StringComparison.Ordinal) ||
            !normalized.StartsWith(requiredPrefix,
                StringComparison.OrdinalIgnoreCase) ||
            !normalized.EndsWith(requiredExtension,
                StringComparison.OrdinalIgnoreCase))
            return false;

        string[] segments = normalized.Split('/');
        if (segments.Any(segment => segment is "" or "." or "..") ||
            ContainsReservedDeviceSegment(normalized))
            return false;

        try
        {
            path = new AssetPath(normalized);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool ContainsReservedDeviceSegment(string value)
    {
        foreach (string segment in value.Replace('\\', '/').Split('/'))
        {
            string withoutTrailing = segment.TrimEnd(' ', '.');
            int extensionSeparator = withoutTrailing.IndexOf('.');
            string deviceName = extensionSeparator >= 0
                ? withoutTrailing[..extensionSeparator]
                : withoutTrailing;
            if (ReservedDeviceNames.Contains(deviceName))
                return true;
        }

        return false;
    }
}

internal sealed partial class ExternalHeadPartPhysicsXmlReader
{
    internal const string DefaultBbpRelativePath =
        "SKSE/Plugins/hdtSkinnedMeshConfigs/defaultBBPs.xml";
    private const string PhysicsXmlPrefix =
        "SKSE/Plugins/hdtSkinnedMeshConfigs/";
    private const string ColliderPrefix = "meshes/";
    private const int MaximumXmlBytes = 1024 * 1024;
    internal const int MaximumAssetBytes = 64 * 1024 * 1024;
    private const int MaximumXmlDepth = 64;
    private const int MaximumXmlCharacters = 4 * 1024 * 1024;
    private const int MaximumColliders = 32;
    private const int MaximumBones = 128;

    private readonly IWorkspacePolicy _workspacePolicy;
    private readonly WorkspacePath _labRoot;
    private readonly Action<string>? _afterInitialRead;

    internal ExternalHeadPartPhysicsXmlReader(
        IWorkspacePolicy workspacePolicy,
        WorkspacePath labRoot)
        : this(workspacePolicy, labRoot, null)
    {
    }

    internal ExternalHeadPartPhysicsXmlReader(
        IWorkspacePolicy workspacePolicy,
        WorkspacePath labRoot,
        Action<string>? afterInitialRead)
    {
        _workspacePolicy = workspacePolicy ??
            throw new ArgumentNullException(nameof(workspacePolicy));
        _labRoot = labRoot;
        _afterInitialRead = afterInitialRead;
    }

    internal async ValueTask<ExternalHeadPartPhysicsXmlReadResult>
        ReadPhysicsXmlAsync(
            WorkspacePath dataRoot,
            AssetPath xmlPath,
            CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        AddReadRootDiagnostics(dataRoot, diagnostics);
        if (HasErrors(diagnostics))
            return RefusedPhysics(diagnostics);
        if (!ExternalHeadPartPhysicsPathRules.TryCreateAssetPath(
                xmlPath.Value,
                PhysicsXmlPrefix,
                ".xml",
                out AssetPath canonicalPath))
        {
            diagnostics.Add(Error(
                "external-headpart-physics-xml-path",
                $"Physics XML path '{xmlPath.Value}' is not a safe hdtSkinnedMeshConfigs XML path."));
            return RefusedPhysics(diagnostics);
        }

        try
        {
            ExternalHeadPartPhysicsFileReadEvidence evidence =
                await ReadOrdinaryFileAsync(
                dataRoot,
                canonicalPath,
                MaximumXmlBytes,
                cancellationToken,
                _afterInitialRead);
            ParsedPhysicsXml parsed = ParsePhysicsXml(
                canonicalPath,
                evidence.Bytes);
            var colliderEvidence = ImmutableArray.CreateBuilder<
                ExternalHeadPartPhysicsColliderEvidence>(parsed.ColliderPaths.Length);
            foreach (AssetPath colliderPath in parsed.ColliderPaths)
            {
                ExternalHeadPartPhysicsFileReadEvidence collider =
                    await ReadOrdinaryFileAsync(
                    dataRoot,
                    colliderPath,
                    MaximumAssetBytes,
                    cancellationToken,
                    _afterInitialRead);
                colliderEvidence.Add(new(
                    colliderPath,
                    collider.Sha256,
                    collider.ByteLength));
            }

            return new ExternalHeadPartPhysicsXmlReadResult(
                true,
                new ExternalHeadPartPhysicsXmlDocument(
                    canonicalPath,
                    evidence.Sha256,
                    evidence.ByteLength,
                    parsed.BoneNames,
                    colliderEvidence.ToImmutable()),
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ExternalHeadPartPhysicsAssetMissingException exception)
        {
            diagnostics.Add(Error(
                "external-headpart-physics-asset-missing",
                $"Physics XML '{canonicalPath.Value}' references a missing asset: {exception.Message}"));
            return RefusedPhysics(diagnostics);
        }
        catch (ExternalHeadPartPhysicsAssetDriftException exception)
        {
            diagnostics.Add(Error(
                "external-headpart-physics-asset-drift",
                $"Physics XML '{canonicalPath.Value}' or its asset evidence drifted: {exception.Message}"));
            return RefusedPhysics(diagnostics);
        }
        catch (Exception exception) when (exception is InvalidDataException or
            XmlException or IOException or UnauthorizedAccessException or
            ArgumentException)
        {
            diagnostics.Add(Error(
                "external-headpart-physics-xml-refused",
                $"Physics XML '{canonicalPath.Value}' was refused: {exception.Message}"));
            return RefusedPhysics(diagnostics);
        }
    }

    internal async ValueTask<ExternalHeadPartDefaultBbpReadResult>
        ReadDefaultBbpAsync(
            WorkspacePath dataRoot,
            CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        AddReadRootDiagnostics(dataRoot, diagnostics);
        if (HasErrors(diagnostics))
            return RefusedMapping(diagnostics);

        AssetPath mappingPath = new(DefaultBbpRelativePath);
        try
        {
            ExternalHeadPartPhysicsFileReadEvidence evidence =
                await ReadOrdinaryFileAsync(
                dataRoot,
                mappingPath,
                MaximumXmlBytes,
                cancellationToken,
                _afterInitialRead);
            ImmutableArray<ExternalHeadPartDefaultBbpEntry> entries =
                ParseDefaultBbp(evidence.Bytes);
            return new ExternalHeadPartDefaultBbpReadResult(
                true,
                new ExternalHeadPartDefaultBbpMap(
                    mappingPath,
                    evidence.Sha256,
                    evidence.ByteLength,
                    entries),
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or
            XmlException or IOException or UnauthorizedAccessException or
            ArgumentException)
        {
            diagnostics.Add(Error(
                "external-headpart-default-bbp-refused",
                $"The exact default-BBP mapping '{mappingPath.Value}' was refused: {exception.Message}"));
            return RefusedMapping(diagnostics);
        }
    }

    private void AddReadRootDiagnostics(
        WorkspacePath dataRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            diagnostics.AddRange(_workspacePolicy.EvaluateReadRoot(
                _labRoot,
                dataRoot));
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or ArgumentException)
        {
            diagnostics.Add(Error(
                "external-headpart-physics-data-root",
                $"The physics Data root could not be inspected: {exception.Message}"));
        }
    }

    internal static async ValueTask<ExternalHeadPartPhysicsFileReadEvidence>
        ReadOrdinaryFileAsync(
        WorkspacePath dataRoot,
        AssetPath path,
        int maximumBytes,
        CancellationToken cancellationToken,
        Action<string>? afterInitialRead = null)
    {
        string physicalPath = GetPhysicalPath(dataRoot, path);
        EnsureNoReparsePath(physicalPath, dataRoot.Value);
        FileInfo info = new(physicalPath);
        if (!info.Exists)
            throw new ExternalHeadPartPhysicsAssetMissingException(
                $"The required physics asset '{path.Value}' is missing.");
        if ((info.Attributes & (FileAttributes.ReparsePoint |
                FileAttributes.Device)) != 0)
            throw new InvalidDataException(
                $"The physics asset '{path.Value}' is a reparse or device file.");
        if (info.Length <= 0 || info.Length > maximumBytes)
            throw new System.IO.InvalidDataException(
                $"The physics asset '{path.Value}' must contain 1 to {maximumBytes} bytes.");

        byte[] bytes;
        long initialLength;
        await using (var stream = new FileStream(
            physicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan))
        {
            initialLength = stream.Length;
            if (initialLength <= 0 || initialLength > maximumBytes)
                throw new ExternalHeadPartPhysicsAssetDriftException(
                    $"The physics asset '{path.Value}' changed outside the supported size bound.");
            bytes = new byte[checked((int)initialLength)];
            var offset = 0;
            while (offset < bytes.Length)
            {
                int read = await stream.ReadAsync(
                    bytes.AsMemory(offset),
                    cancellationToken);
                if (read == 0)
                    throw new ExternalHeadPartPhysicsAssetDriftException(
                        $"The physics asset '{path.Value}' was truncated while it was read.");
                offset += read;
            }
            if (stream.Length != initialLength || stream.Position != initialLength)
                throw new ExternalHeadPartPhysicsAssetDriftException(
                    $"The physics asset '{path.Value}' changed while it was read.");
        }

        afterInitialRead?.Invoke(physicalPath);

        info.Refresh();
        if (!info.Exists || info.Length != initialLength ||
            (info.Attributes & (FileAttributes.ReparsePoint |
                FileAttributes.Device)) != 0)
            throw new ExternalHeadPartPhysicsAssetDriftException(
                $"The physics asset '{path.Value}' drifted while it was read.");

        EnsureNoReparsePath(physicalPath, dataRoot.Value);
        byte[] reopenedBytes;
        long reopenedLength;
        await using (var stream = new FileStream(
            physicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan))
        {
            reopenedLength = stream.Length;
            if (reopenedLength <= 0 || reopenedLength > maximumBytes)
                throw new ExternalHeadPartPhysicsAssetDriftException(
                    $"The physics asset '{path.Value}' changed outside the supported size bound on reopen.");
            reopenedBytes = new byte[checked((int)reopenedLength)];
            var offset = 0;
            while (offset < reopenedBytes.Length)
            {
                int read = await stream.ReadAsync(
                    reopenedBytes.AsMemory(offset),
                    cancellationToken);
                if (read == 0)
                    throw new ExternalHeadPartPhysicsAssetDriftException(
                        $"The physics asset '{path.Value}' was truncated on reopen.");
                offset += read;
            }
            if (stream.Length != reopenedLength ||
                stream.Position != reopenedLength)
                throw new ExternalHeadPartPhysicsAssetDriftException(
                    $"The physics asset '{path.Value}' changed while it was reopened.");
        }

        info.Refresh();
        if (!info.Exists || info.Length != reopenedLength ||
            (info.Attributes & (FileAttributes.ReparsePoint |
                FileAttributes.Device)) != 0)
            throw new ExternalHeadPartPhysicsAssetDriftException(
                $"The physics asset '{path.Value}' drifted after reopen.");

        Sha256Hash initialHash = new(
            Convert.ToHexString(SHA256.HashData(bytes)));
        Sha256Hash reopenedHash = new(
            Convert.ToHexString(SHA256.HashData(reopenedBytes)));
        if (reopenedLength != initialLength || reopenedHash != initialHash)
            throw new ExternalHeadPartPhysicsAssetDriftException(
                $"The physics asset '{path.Value}' changed between reads.");

        return new ExternalHeadPartPhysicsFileReadEvidence(
            reopenedBytes,
            reopenedHash,
            reopenedLength);
    }

    private static ParsedPhysicsXml ParsePhysicsXml(
        AssetPath path,
        byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using XmlReader reader = CreateReader(stream);
        var bones = ImmutableArray.CreateBuilder<string>();
        var colliders = ImmutableArray.CreateBuilder<AssetPath>();
        var boneNames = new HashSet<string>(StringComparer.Ordinal);
        var colliderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ancestors = new Stack<string>();
        bool rootSeen = false;
        bool rootClosed = false;
        bool bonesSection = false;
        bool collidersSection = false;

        while (reader.Read())
        {
            if (reader.Depth > MaximumXmlDepth)
                throw Invalid("XML nesting exceeds the supported depth bound.");
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (!string.IsNullOrEmpty(reader.NamespaceURI) ||
                    !string.IsNullOrEmpty(reader.Prefix))
                    throw Invalid("Physics XML namespaces are not admitted.");
                string name = reader.LocalName;
                if (!rootSeen)
                {
                    if (reader.Depth != 0 ||
                        !string.Equals(name, "hdtSmp", StringComparison.Ordinal) ||
                        reader.IsEmptyElement)
                        throw Invalid("Physics XML must have one non-empty hdtSmp root element.");
                    rootSeen = true;
                }
                else if (reader.Depth == 0 || rootClosed)
                {
                    throw Invalid("Physics XML contains more than one root element.");
                }
                else if (reader.Depth == 1)
                {
                    if (ancestors.Count != 1 ||
                        !string.Equals(ancestors.Peek(), "hdtSmp",
                            StringComparison.Ordinal) ||
                        name is not ("bones" or "colliders"))
                        throw Invalid("Physics XML contains an unsupported top-level section.");
                    if (name == "bones" ? bonesSection : collidersSection)
                        throw Invalid($"Physics XML repeats the '{name}' section.");
                    if (name == "bones") bonesSection = true;
                    else collidersSection = true;
                }
                else if (reader.Depth == 2)
                {
                    if (ancestors.Count != 2)
                        throw Invalid("Physics XML has an invalid section nesting.");
                    string section = ancestors.Peek();
                    if (section == "bones" && name == "bone")
                    {
                        string boneName = RequiredAttribute(reader, "name");
                        if (!ExternalHeadPartPhysicsPathRules.IsSafePortableToken(
                                boneName) || !boneNames.Add(boneName))
                            throw Invalid("Physics XML contains an empty, unsafe, or duplicate bone name.");
                        if (bones.Count == MaximumBones)
                            throw Invalid("Physics XML exceeds the bone count bound.");
                        bones.Add(boneName);
                    }
                    else if (section == "colliders" && name == "collider")
                    {
                        string colliderValue = RequiredAttribute(reader, "path");
                        if (!ExternalHeadPartPhysicsPathRules.TryCreateAssetPath(
                                colliderValue,
                                ColliderPrefix,
                                ".nif",
                                out AssetPath colliderPath) ||
                            !colliderPaths.Add(colliderPath.Value))
                            throw Invalid("Physics XML contains an unsafe or duplicate collider path.");
                        if (colliders.Count == MaximumColliders)
                            throw Invalid("Physics XML exceeds the collider count bound.");
                        colliders.Add(colliderPath);
                    }
                    else
                    {
                        throw Invalid("Physics XML contains an unsupported child element.");
                    }
                }
                else
                {
                    throw Invalid("Physics XML exceeds the admitted section structure.");
                }

                RejectUnexpectedAttributes(reader);
                if (!reader.IsEmptyElement)
                    ancestors.Push(name);
            }
            else if ((reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA) &&
                !string.IsNullOrWhiteSpace(reader.Value))
            {
                throw Invalid("Physics XML contains unsupported character content.");
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (ancestors.Count == 0 ||
                    !string.Equals(ancestors.Pop(), reader.LocalName,
                        StringComparison.Ordinal))
                    throw Invalid("Physics XML element nesting is malformed.");
                if (reader.Depth == 0)
                    rootClosed = true;
            }
        }

        if (!rootSeen || !rootClosed || ancestors.Count != 0 ||
            !bonesSection || !collidersSection || bones.Count == 0 ||
            colliders.Count == 0)
            throw Invalid(
                $"Physics XML '{path.Value}' must declare non-empty bones and colliders sections.");
        return new ParsedPhysicsXml(
            bones.ToImmutable(),
            colliders.ToImmutable());
    }

    private static ImmutableArray<ExternalHeadPartDefaultBbpEntry> ParseDefaultBbp(
        byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using XmlReader reader = CreateReader(stream);
        var entries = ImmutableArray.CreateBuilder<ExternalHeadPartDefaultBbpEntry>();
        var shapeNames = new HashSet<string>(StringComparer.Ordinal);
        var ancestors = new Stack<string>();
        bool rootSeen = false;
        bool rootClosed = false;

        while (reader.Read())
        {
            if (reader.Depth > MaximumXmlDepth)
                throw Invalid("Default-BBP XML nesting exceeds the supported depth bound.");
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (!string.IsNullOrEmpty(reader.NamespaceURI) ||
                    !string.IsNullOrEmpty(reader.Prefix))
                    throw Invalid("defaultBBPs.xml namespaces are not admitted.");
                string name = reader.LocalName;
                if (!rootSeen)
                {
                    if (reader.Depth != 0 ||
                        !IsDefaultBbpRoot(name) ||
                        reader.IsEmptyElement)
                        throw Invalid("defaultBBPs.xml must have one non-empty defaultBBPs root.");
                    rootSeen = true;
                }
                else if (reader.Depth != 1 || ancestors.Count != 1 ||
                    !IsDefaultBbpRoot(ancestors.Peek()) || name != "map")
                {
                    throw Invalid("defaultBBPs.xml contains an unsupported element.");
                }

                if (reader.Depth == 1)
                {
                    string shapeName = RequiredAttribute(reader, "shape");
                    string xmlValue = RequiredAttribute(reader, "file");
                    if (!ExternalHeadPartPhysicsPathRules.IsSafePortableToken(
                            shapeName) || !shapeNames.Add(shapeName) ||
                        !ExternalHeadPartPhysicsPathRules.TryCreateAssetPath(
                            xmlValue,
                            PhysicsXmlPrefix,
                            ".xml",
                            out AssetPath xmlPath))
                        throw Invalid("defaultBBPs.xml contains an empty, duplicate, or unsafe map entry.");
                    entries.Add(new(shapeName, xmlPath));
                }

                RejectUnexpectedAttributes(reader);
                if (!reader.IsEmptyElement)
                    ancestors.Push(name);
            }
            else if ((reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA) &&
                !string.IsNullOrWhiteSpace(reader.Value))
            {
                throw Invalid("defaultBBPs.xml contains unsupported character content.");
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (ancestors.Count == 0 ||
                    !string.Equals(ancestors.Pop(), reader.LocalName,
                        StringComparison.Ordinal))
                    throw Invalid("defaultBBPs.xml element nesting is malformed.");
                if (reader.Depth == 0)
                    rootClosed = true;
            }
        }

        if (!rootSeen || !rootClosed || ancestors.Count != 0 || entries.Count == 0)
            throw Invalid("defaultBBPs.xml must declare at least one map entry.");
        return entries.ToImmutable();
    }

    private static bool IsDefaultBbpRoot(string name) =>
        string.Equals(name, "defaultBBPs", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "default-bbps", StringComparison.OrdinalIgnoreCase);

    private static XmlReader CreateReader(Stream stream) => XmlReader.Create(
        stream,
        new XmlReaderSettings
        {
            Async = false,
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = MaximumXmlCharacters,
            MaxCharactersFromEntities = 0,
            XmlResolver = null
        });

    private static string RequiredAttribute(XmlReader reader, string name)
    {
        string? value = reader.GetAttribute(name);
        if (string.IsNullOrWhiteSpace(value))
            throw Invalid($"XML element '{reader.LocalName}' requires a non-empty '{name}' attribute.");
        return value;
    }

    private static void RejectUnexpectedAttributes(XmlReader reader)
    {
        string elementName = reader.LocalName;
        for (var index = 0; index < reader.AttributeCount; index++)
        {
            reader.MoveToAttribute(index);
            string attributeName = reader.LocalName;
            bool allowed = elementName switch
            {
                "bone" => attributeName == "name",
                "collider" => attributeName == "path",
                "map" => attributeName is "shape" or "file",
                _ => false
            };
            if (allowed)
                continue;
            throw Invalid(
                $"XML element '{elementName}' has an unsupported attribute '{reader.Name}'.");
        }
        reader.MoveToElement();
    }

    private static string GetPhysicalPath(WorkspacePath dataRoot, AssetPath path)
    {
        string fullPath = Path.GetFullPath(Path.Combine(
            dataRoot.Value,
            path.Value.Replace('/', Path.DirectorySeparatorChar)));
        var physical = new WorkspacePath(fullPath);
        if (!physical.IsUnder(dataRoot))
            throw new System.IO.InvalidDataException(
                $"Physics path '{path.Value}' escapes the supplied Data root.");
        return fullPath;
    }

    private static void EnsureNoReparsePath(string fullPath, string dataRoot)
    {
        string current = Path.GetFullPath(fullPath);
        string boundary = Path.GetFullPath(dataRoot);
        while (true)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & (FileAttributes.ReparsePoint |
                        FileAttributes.Device)) != 0)
                    throw new System.IO.InvalidDataException(
                        $"Physics path traverses a reparse or device entry '{current}'.");
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }

            if (string.Equals(current, boundary,
                    StringComparison.OrdinalIgnoreCase))
                return;
            string? parent = Directory.GetParent(current)?.FullName;
            if (parent is null ||
                string.Equals(parent, current,
                    StringComparison.OrdinalIgnoreCase))
                throw new System.IO.InvalidDataException(
                    "Physics path ancestry did not reach the supplied Data root.");
            current = parent;
        }
    }

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) => diagnostics.Any(item =>
        item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static InvalidDataException Invalid(string message) => new(message);

    private static ExternalHeadPartPhysicsXmlReadResult RefusedPhysics(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private static ExternalHeadPartDefaultBbpReadResult RefusedMapping(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private sealed record ParsedPhysicsXml(
        ImmutableArray<string> BoneNames,
        ImmutableArray<AssetPath> ColliderPaths);
}

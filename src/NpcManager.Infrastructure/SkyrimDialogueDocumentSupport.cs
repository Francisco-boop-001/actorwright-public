using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

internal sealed class SkyrimDialogueDocumentSupport(WorkspacePath root)
{
    internal WorkspacePath Root => root;
    private readonly KOnlyWorkspacePolicy policy = new(root, ActorwrightWorkspace.ResolveProtectedRoot(root));

    internal WorkspacePath Admit(string path)
    {
        var value = new WorkspacePath(path);
        if (!root.Value.StartsWith(@"K:\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(WorkspaceDiagnosticCodes.RootNotKLocal + ": Dialogue needs a K-local workspace.");
        RefuseProtected(value);
        var errors = policy.EvaluateReadRoot(root, value).Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0) throw new InvalidDataException(errors[0].Code + ": " + errors[0].Message);
        return value;
    }
    internal WorkspacePath Relative(string parent, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("dialogue-verify-document-binding: Asset path must be relative.");
        var path = Admit(Path.Combine(parent, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.IsUnder(new WorkspacePath(parent))) throw new InvalidDataException("dialogue-verify-document-binding: Asset escaped its declared root.");
        return path;
    }
    internal byte[] Read(string path, Sha256Hash? expected = null, string mismatchCode = SkyrimNpcDialogueDiagnosticCodes.SourceHashMismatch, bool allowEmpty = false)
    {
        var admitted = Admit(path);
        using var file = new FileStream(admitted.Value, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length == 0 && !allowEmpty || file.Length > 512L * 1024 * 1024) throw new InvalidDataException("dialogue-manifest-invalid: Input length is outside the supported range.");
        byte[] bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        if (expected is { } hash && SkyrimNpcVoiceDocumentCodec.Hash(bytes) != hash) throw new InvalidDataException(mismatchCode + ": Bound input changed: " + path);
        return bytes;
    }
    internal T Read<T>(string path, Sha256Hash expected, string code = SkyrimNpcDialogueDiagnosticCodes.ManifestHashMismatch) => SkyrimNpcVoiceDocumentCodec.Parse<T>(Read(path, expected, code));
    internal Sha256Hash Write<T>(string path, T document) => WriteBytes(path, SkyrimNpcVoiceDocumentCodec.Serialize(document));
    internal Sha256Hash WriteBytes(string path, byte[] bytes)
    {
        var admitted = Admit(path); string parent = Path.GetDirectoryName(admitted.Value)!;
        Directory.CreateDirectory(parent); Admit(parent);
        using var output = new FileStream(admitted.Value, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes); output.Flush(flushToDisk: true);
        return SkyrimNpcVoiceDocumentCodec.Hash(bytes);
    }
    internal void Fresh(string path)
    {
        Admit(path);
        if (File.Exists(path) || Directory.Exists(path)) throw new InvalidDataException(SkyrimNpcDialogueDiagnosticCodes.OutputExists + ": Refusing existing output: " + path);
    }
    internal static void RefuseProtected(WorkspacePath path)
    {
        if (ActorwrightWorkspace.IsVoiceExcludedPath(path)) throw new InvalidDataException("protected-root-refused: Dialogue input/tool path is protected.");
    }
    internal static Diagnostic Diagnostic(Exception exception)
    {
        string message = exception.Message; int separator = message.IndexOf(':');
        string code = separator > 0 && !message[..separator].Any(char.IsWhiteSpace) && message[..separator].Contains('-', StringComparison.Ordinal)
            ? message[..separator] : SkyrimNpcDialogueDiagnosticCodes.ManifestInvalid;
        return new(code, DiagnosticSeverity.Error, message);
    }
    internal static bool Handled(Exception ex) => ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or Mutagen.Bethesda.Plugins.Exceptions.RecordException;
}

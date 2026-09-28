using System.Diagnostics;
using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class FaceFxLipSyncAdapter
{
    private readonly string tools;
    public bool SupportsFuz { get; }
    public FaceFxLipSyncAdapter(WorkspacePath toolDirectory)
    {
        tools = toolDirectory.Value;
        Admit(); Require("FaceFXWrapper.exe"); Require("FonixData.cdf");
        bool encoder = File.Exists(Path.Combine(tools, "xWMAEncode.exe"));
        bool fuzer = File.Exists(Path.Combine(tools, "fuz_extractor.exe"));
        if (encoder != fuzer) throw new InvalidDataException("dialogue-lip-tool-failed: FUZ requires both xWMAEncode.exe and fuz_extractor.exe.");
        SupportsFuz = encoder && fuzer;
    }
    private void Admit()
    {
        var path = new WorkspacePath(tools); SkyrimDialogueDocumentSupport.RefuseProtected(path);
        if (!path.Value.StartsWith(@"K:\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("dialogue-lip-tool-failed: Tools must be K-local.");
        var root = new WorkspacePath(@"K:\");
        var errors = new KOnlyWorkspacePolicy(root, ActorwrightWorkspace.ResolveProtectedRoot(root)).EvaluateReadRoot(root, path);
        if (errors.Any(x => x.Severity == DiagnosticSeverity.Error)) throw new InvalidDataException("dialogue-lip-tool-failed: Tool path failed ordinary-file admission.");
    }
    private string Require(string name)
    {
        Admit(); string path = Path.Combine(tools, name);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("dialogue-lip-tool-failed: Required ordinary nonempty tool missing: " + name);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var image = new PEReader(stream);
                if (image.PEHeaders.PEHeader is null) throw new BadImageFormatException("PE header is absent.");
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException)
            { throw new InvalidDataException("dialogue-lip-tool-failed: Invalid executable image: " + name, ex); }
        }
        return path;
    }
    public async Task<(string Lip, string? Fuz)> GenerateAsync(string wav, string text, CancellationToken cancellationToken)
    {
        var wavPath = new WorkspacePath(wav); SkyrimDialogueDocumentSupport.RefuseProtected(wavPath);
        var root = new WorkspacePath(@"K:\");
        if (!wavPath.Value.StartsWith(@"K:\", StringComparison.OrdinalIgnoreCase) ||
            new KOnlyWorkspacePolicy(root, ActorwrightWorkspace.ResolveProtectedRoot(root)).EvaluateReadRoot(root, wavPath).Any(x => x.Severity == DiagnosticSeverity.Error))
            throw new InvalidDataException("dialogue-lip-tool-failed: Audio requires an ordinary K-local output path.");
        string lip = Path.ChangeExtension(wav, ".lip"); string resampled = Path.ChangeExtension(wav, ".facefx.wav");
        if (File.Exists(lip) || File.Exists(resampled)) throw new InvalidDataException("dialogue-output-exists: Lip intermediate already exists.");
        string letters = new(text.Select(x => char.IsLetter(x) || char.IsWhiteSpace(x) ? x : ' ').ToArray());
        if (string.IsNullOrWhiteSpace(letters)) throw new InvalidDataException("dialogue-lip-tool-failed: Text needs letters for FaceFX.");
        await RunAsync("FaceFXWrapper.exe", ["Skyrim", "USEnglish", Require("FonixData.cdf"), wav, resampled, lip, letters], cancellationToken);
        Check(lip);
        if (File.Exists(resampled)) File.Delete(resampled);
        if (!SupportsFuz) return (lip, null);
        string xwm = Path.ChangeExtension(wav, ".xwm"); string fuz = Path.ChangeExtension(wav, ".fuz");
        if (File.Exists(xwm) || File.Exists(fuz)) throw new InvalidDataException("dialogue-output-exists: FUZ intermediate already exists.");
        await RunAsync("xWMAEncode.exe", ["-b", "160000", wav, xwm], cancellationToken); Check(xwm);
        await RunAsync("fuz_extractor.exe", ["-c", fuz, lip, xwm], cancellationToken); Check(fuz);
        if (!HasExpectedFuzPayload(File.ReadAllBytes(fuz), File.ReadAllBytes(lip), File.ReadAllBytes(xwm)))
            throw new InvalidDataException("dialogue-lip-tool-failed: FUZ header or embedded LIP/XWM differs from the tool inputs.");
        File.Delete(xwm); return (lip, fuz);
    }
    internal static bool HasExpectedFuzPayload(byte[] fuz, byte[] lip, byte[]? xwm = null) =>
        fuz.Length > 12 + lip.Length && fuz.AsSpan(0, 4).SequenceEqual("FUZE"u8) &&
        BinaryPrimitives.ReadUInt32LittleEndian(fuz.AsSpan(4)) == 1 && BinaryPrimitives.ReadUInt32LittleEndian(fuz.AsSpan(8)) == lip.Length &&
        fuz.AsSpan(12, lip.Length).SequenceEqual(lip) && (xwm is null || fuz.AsSpan(12 + lip.Length).SequenceEqual(xwm));
    private async Task RunAsync(string name, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Require(name)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = tools, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string arg in arguments) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new InvalidDataException("dialogue-lip-tool-failed: Tool could not start."); }
        catch (System.ComponentModel.Win32Exception ex) { throw new InvalidDataException("dialogue-lip-tool-failed: Tool process could not start.", ex); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        Task stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(stdout, stderr); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new InvalidDataException("dialogue-lip-tool-failed: Tool exceeded two minutes.");
        }
        if (process.ExitCode != 0) throw new InvalidDataException($"dialogue-lip-tool-failed: {name} exited {process.ExitCode}.");
    }
    private static void Check(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("dialogue-lip-tool-failed: Tool did not produce a nonempty ordinary output.");
    }
}

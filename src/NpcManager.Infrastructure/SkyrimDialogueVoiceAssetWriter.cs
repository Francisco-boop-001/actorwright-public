using System.Buffers.Binary;
using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

internal static class SkyrimDialogueVoiceAssetWriter
{
    internal const string ScriptRelative = "Scripts/ActorwrightFollowerDialogue.pex";
    internal static Sha256Hash ScriptHash { get; } = new("CDE13383F9DB288733CEA255C82E7B70747FFE4A39126CB6DBE992B0CEA8AD56");
    internal static byte[] Script()
    {
        using var resource = typeof(SkyrimDialogueVoiceAssetWriter).Assembly.GetManifestResourceStream("Actorwright.Runtime.SkyrimSE.FollowerDialogue.Pex")
            ?? throw new InvalidDataException("dialogue-verify-script-mismatch: Embedded follower script missing.");
        using var output = new MemoryStream(); resource.CopyTo(output); byte[] bytes = output.ToArray();
        if (bytes.Length != 1721 || SkyrimNpcVoiceDocumentCodec.Hash(bytes) != ScriptHash)
            throw new InvalidDataException("dialogue-verify-script-mismatch: Embedded follower script failed its product hash.");
        return bytes;
    }
    internal static async Task<ImmutableArray<SkyrimDialogueOutputAsset>> WriteAsync(SkyrimDialogueProposal proposal, SkyrimVoiceSynthesisManifest synthesis,
        string synthesisDirectory, string staging, FaceFxLipSyncAdapter? lip, SkyrimDialogueDocumentSupport documents, IProgress<string>? progress, CancellationToken token)
    {
        var assets = ImmutableArray.CreateBuilder<SkyrimDialogueOutputAsset>();
        foreach (var asset in proposal.Assets)
        {
            token.ThrowIfCancellationRequested();
            var line = synthesis.Lines.Single(x => x.LineId == asset.LineId);
            string source = documents.Relative(synthesisDirectory, line.OutputFile!).Value;
            byte[] wave = PrepareDelivery(documents.Read(source, line.OutputSha256, SkyrimNpcDialogueDiagnosticCodes.SynthesisIncomplete));
            string relative = asset.RelativeDirectory + "/" + asset.FileStem + ".wav";
            string wav = documents.Relative(staging, relative).Value;
            Sha256Hash hash = documents.WriteBytes(wav, wave);
            string? lipRelative = null, fuzRelative = null; Sha256Hash? lipHash = null, fuzHash = null;
            if (lip is not null)
            {
                var output = await lip.GenerateAsync(wav, line.Text, token);
                _ = documents.Read(wav, hash, SkyrimNpcDialogueDiagnosticCodes.LipToolFailed);
                lipRelative = Path.GetRelativePath(staging, output.Lip).Replace('\\', '/'); lipHash = SkyrimNpcVoiceDocumentCodec.Hash(documents.Read(output.Lip));
                if (output.Fuz is not null) { fuzRelative = Path.GetRelativePath(staging, output.Fuz).Replace('\\', '/'); fuzHash = SkyrimNpcVoiceDocumentCodec.Hash(documents.Read(output.Fuz)); }
            }
            assets.Add(new(asset.LineId, asset.InfoLocalFormId, relative, hash, lipRelative, lipHash, fuzRelative, fuzHash));
            progress?.Report("Dialogue asset completed: " + asset.LineId);
        }
        return assets.ToImmutable();
    }
    internal static byte[] PrepareDelivery(byte[] source)
    {
        CheckWave(source);
        var audio = WavSampleCodec.Decode(source);
        if (audio.Channels != 1 || audio.BitsPerSample != 16 || audio.Format != "pcm" || audio.SampleRate is not (24000 or 44100))
            throw new InvalidDataException("dialogue-synthesis-incomplete: Delivery input must be mono PCM16 at 24000 or 44100 Hz; retain the original and normalize other formats explicitly.");
        byte[] result = audio.SampleRate == 44100 ? source : WavSampleCodec.WritePcm16(
            WavSampleCodec.Resample(audio.Samples, audio.SampleRate, 44100), 44100);
        CheckDelivery(result);
        return result;
    }
    internal static void CheckDelivery(byte[] bytes)
    {
        CheckWave(bytes);
        var audio = WavSampleCodec.Decode(bytes);
        if (audio.SampleRate != 44100 || audio.Channels != 1 || audio.BitsPerSample != 16 || audio.Format != "pcm")
            throw new InvalidDataException("dialogue-synthesis-incomplete: Final loose WAV must be PCM16 mono 44100 Hz; synthesis success alone is not delivery qualification.");
    }
    internal static void CheckWave(byte[] bytes)
    {
        var audio = WavSampleCodec.Measure(WavSampleCodec.Decode(bytes));
        if (audio.SampleFrames <= 0 || audio.DurationSeconds <= 0 || !double.IsFinite(audio.RmsDbfs) || audio.RmsDbfs < -60 || audio.PeakDbfs < -50)
            throw new InvalidDataException("dialogue-synthesis-incomplete: Audio must be decodable and non-silent.");
    }
    internal static byte[] Seq(byte[]? existing, uint rawQuest)
    {
        if (existing is not null && existing.Length % 4 != 0) throw new InvalidDataException("dialogue-verify-seq-missing: Existing SEQ length is not a multiple of four.");
        byte[] seq = existing ?? [];
        for (int i = 0; i < seq.Length; i += 4) if (BinaryPrimitives.ReadUInt32LittleEndian(seq.AsSpan(i)) == rawQuest) return seq;
        byte[] value = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(value, rawQuest); return seq.Concat(value).ToArray();
    }
}

using System.Text;
using NpcManager.Application;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static void TestSkyrimVoiceSampleCodec()
    {
        TestDecodeStereoSixteenBitSine();
        TestDecodeEveryPcmWidthAndFloat();
        TestDecodeExtensibleWithChunksInAnyOrder();
        TestMeasureSilenceAndDbfs();
        TestRefuseNonDecodableBytes();
        TestResampleAndNormalize();
    }

    private static void TestDecodeStereoSixteenBitSine()
    {
        byte[] wav = SyntheticWav(44100, 2, 16, "pcm", 3.0, 0.8);
        WavDecodedAudio audio = WavSampleCodec.Decode(wav);
        Assert(audio.SampleRate == 44100 && audio.Channels == 2 && audio.BitsPerSample == 16 &&
               audio.Format == "pcm" && audio.Frames == 132300,
            $"Stereo 16-bit decode drifted: {audio.SampleRate}/{audio.Channels}/{audio.BitsPerSample}/{audio.Format}/{audio.Frames}.");
        SkyrimVoiceSampleAudio measured = WavSampleCodec.Measure(audio);
        Assert(Math.Abs(measured.DurationSeconds - 3.0) < 0.001 &&
               Math.Abs(measured.PeakDbfs - 20 * Math.Log10(0.8)) < 0.05 &&
               Math.Abs(measured.RmsDbfs - (20 * Math.Log10(0.8) - 3.01)) < 0.1,
            $"Measured sine drifted: duration={measured.DurationSeconds} peak={measured.PeakDbfs} rms={measured.RmsDbfs}.");
    }

    private static void TestDecodeEveryPcmWidthAndFloat()
    {
        foreach ((int bits, string format) in new[] { (8, "pcm"), (16, "pcm"), (24, "pcm"), (32, "pcm"), (32, "float") })
        {
            byte[] wav = SyntheticWav(16000, 1, bits, format, 0.5, 0.5);
            WavDecodedAudio audio = WavSampleCodec.Decode(wav);
            SkyrimVoiceSampleAudio measured = WavSampleCodec.Measure(audio);
            double tolerance = bits == 8 ? 0.2 : 0.02;
            Assert(audio.BitsPerSample == bits && audio.Format == format && audio.Frames == 8000 &&
                   Math.Abs(measured.PeakDbfs - 20 * Math.Log10(0.5)) < tolerance,
                $"{bits}-bit {format} decode drifted: peak={measured.PeakDbfs} frames={audio.Frames} format={audio.Format}.");
        }
    }

    private static void TestDecodeExtensibleWithChunksInAnyOrder()
    {
        byte[] wav = SyntheticWav(22050, 1, 16, "pcm", 1.0, 0.5, extensible: true, dataBeforeFormat: true, oddListChunk: true);
        WavDecodedAudio audio = WavSampleCodec.Decode(wav);
        Assert(audio.SampleRate == 22050 && audio.Channels == 1 && audio.BitsPerSample == 16 &&
               audio.Format == "pcm" && audio.Frames == 22050,
            "WAVE_FORMAT_EXTENSIBLE with data before fmt and an odd LIST chunk did not decode.");
    }

    private static void TestMeasureSilenceAndDbfs()
    {
        byte[] silent = SyntheticWav(22050, 1, 16, "pcm", 1.0, 0.0);
        SkyrimVoiceSampleAudio measured = WavSampleCodec.Measure(WavSampleCodec.Decode(silent));
        Assert(measured.PeakDbfs <= -200 && measured.RmsDbfs <= -200 && !double.IsInfinity(measured.PeakDbfs),
            $"Silence must measure at the finite floor, not {measured.PeakDbfs}/{measured.RmsDbfs}.");
        byte[] fullScale = SyntheticWav(22050, 1, 16, "pcm", 0.5, 1.0);
        SkyrimVoiceSampleAudio full = WavSampleCodec.Measure(WavSampleCodec.Decode(fullScale));
        Assert(full.PeakDbfs > -0.01 && full.PeakDbfs <= 0.0001,
            $"Full-scale sine must measure at 0 dBFS, not {full.PeakDbfs}.");
    }

    private static void TestRefuseNonDecodableBytes()
    {
        AssertThrows<InvalidDataException>(() => WavSampleCodec.Decode([]));
        AssertThrows<InvalidDataException>(() => WavSampleCodec.Decode(Encoding.ASCII.GetBytes("not a wav file at all, just text")));
        byte[] truncated = SyntheticWav(22050, 1, 16, "pcm", 1.0, 0.5)[..1000];
        AssertThrows<InvalidDataException>(() => WavSampleCodec.Decode(truncated));
        byte[] noFormat = SyntheticWav(22050, 1, 16, "pcm", 0.1, 0.5, omitFormat: true);
        AssertThrows<InvalidDataException>(() => WavSampleCodec.Decode(noFormat));
        byte[] unsupported = SyntheticWav(22050, 1, 16, "pcm", 0.1, 0.5, formatTag: 0x0055);
        AssertThrows<InvalidDataException>(() => WavSampleCodec.Decode(unsupported));
    }

    private static void TestResampleAndNormalize()
    {
        float[] ramp = Enumerable.Range(0, 8000).Select(index => index / 8000f).ToArray();
        float[] resampled = WavSampleCodec.Resample(ramp, 8000, 22050);
        Assert(resampled.Length == 22050 && Math.Abs(resampled[11025] - 0.5f) < 0.001f,
            $"Linear resampling drifted: length={resampled.Length} mid={(resampled.Length > 11025 ? resampled[11025] : -1)}.");
        byte[] wav = SyntheticWav(44100, 2, 16, "pcm", 3.0, 0.8);
        byte[] normalized = WavSampleCodec.Normalize(WavSampleCodec.Decode(wav));
        WavDecodedAudio audio = WavSampleCodec.Decode(normalized);
        SkyrimVoiceSampleAudio measured = WavSampleCodec.Measure(audio);
        Assert(audio.SampleRate == WavSampleCodec.NormalizedSampleRate && audio.Channels == 1 &&
               audio.BitsPerSample == 16 && audio.Format == "pcm" &&
               Math.Abs(measured.DurationSeconds - 3.0) < 0.001 &&
               Math.Abs(measured.PeakDbfs - 20 * Math.Log10(0.8)) < 0.1,
            $"Normalized copy drifted: {audio.SampleRate}/{audio.Channels}/{audio.BitsPerSample} duration={measured.DurationSeconds} peak={measured.PeakDbfs}.");
    }

    /// <summary>Builds a RIFF/WAVE sine at 440 Hz with the requested amplitude and shape.</summary>
    internal static byte[] SyntheticWav(
        int sampleRate, int channels, int bits, string format, double seconds, double amplitude,
        bool extensible = false, bool dataBeforeFormat = false, bool oddListChunk = false,
        bool omitFormat = false, ushort? formatTag = null)
    {
        int frames = (int)Math.Round(sampleRate * seconds);
        int blockAlign = channels * bits / 8;
        var data = new byte[frames * blockAlign];
        for (int frame = 0; frame < frames; frame++)
        {
            double value = amplitude * Math.Sin(2 * Math.PI * 440 * frame / sampleRate);
            for (int channel = 0; channel < channels; channel++)
            {
                int offset = (frame * channels + channel) * bits / 8;
                switch ((bits, format))
                {
                    case (8, _):
                        data[offset] = (byte)Math.Clamp(Math.Round(value * 127) + 128, 0, 255);
                        break;
                    case (16, _):
                        BitConverter.TryWriteBytes(data.AsSpan(offset), (short)Math.Clamp(Math.Round(value * 32767), -32768, 32767));
                        break;
                    case (24, _):
                        int sample24 = (int)Math.Clamp(Math.Round(value * 8388607), -8388608, 8388607);
                        data[offset] = (byte)sample24;
                        data[offset + 1] = (byte)(sample24 >> 8);
                        data[offset + 2] = (byte)(sample24 >> 16);
                        break;
                    case (32, "float"):
                        BitConverter.TryWriteBytes(data.AsSpan(offset), (float)value);
                        break;
                    default:
                        BitConverter.TryWriteBytes(data.AsSpan(offset), (int)Math.Clamp(Math.Round(value * int.MaxValue), int.MinValue, int.MaxValue));
                        break;
                }
            }
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(0);
        writer.Write("WAVE"u8);
        void WriteFormat()
        {
            if (omitFormat) return;
            ushort tag = formatTag ?? (extensible ? (ushort)0xFFFE : format == "float" ? (ushort)3 : (ushort)1);
            writer.Write("fmt "u8);
            writer.Write(extensible ? 40 : 16);
            writer.Write(tag);
            writer.Write((ushort)channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * blockAlign);
            writer.Write((ushort)blockAlign);
            writer.Write((ushort)bits);
            if (extensible)
            {
                writer.Write((ushort)22);
                writer.Write((ushort)bits);
                writer.Write(channels == 1 ? 4 : 3);
                writer.Write((ushort)(format == "float" ? 3 : 1));
                writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 });
            }
        }
        void WriteData()
        {
            writer.Write("data"u8);
            writer.Write(data.Length);
            writer.Write(data);
        }
        if (oddListChunk)
        {
            writer.Write("LIST"u8);
            writer.Write(5);
            writer.Write("INFOx"u8);
            writer.Write((byte)0);
        }
        if (dataBeforeFormat) { WriteData(); WriteFormat(); }
        else { WriteFormat(); WriteData(); }
        writer.Flush();
        byte[] bytes = stream.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(4), bytes.Length - 8);
        return bytes;
    }
}

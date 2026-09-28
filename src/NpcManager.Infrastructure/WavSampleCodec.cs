using System.Buffers.Binary;
using System.Text;
using NpcManager.Application;

namespace NpcManager.Infrastructure;

public sealed record WavDecodedAudio(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    string Format,
    long Frames,
    float[] Samples);

public static class WavSampleCodec
{
    public const int NormalizedSampleRate = 22_050;
    private const double SilenceFloorDbfs = -300;

    public static WavDecodedAudio Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !bytes[..4].SequenceEqual("RIFF"u8) ||
            !bytes.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("The sample is not a RIFF/WAVE file.");

        uint riffLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4));
        if (riffLength > bytes.Length - 8)
            throw new InvalidDataException("The RIFF/WAVE file is truncated.");

        WaveFormat? format = null;
        byte[]? data = null;
        int offset = 12;
        while (offset <= bytes.Length - 8)
        {
            ReadOnlySpan<byte> id = bytes.Slice(offset, 4);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            offset += 8;
            if (length > int.MaxValue || offset > bytes.Length - (int)length)
                throw new InvalidDataException("A RIFF/WAVE chunk is truncated.");

            ReadOnlySpan<byte> chunk = bytes.Slice(offset, (int)length);
            if (id.SequenceEqual("fmt "u8))
                format = ParseFormat(chunk);
            else if (id.SequenceEqual("data"u8) && data is null)
                data = chunk.ToArray();

            offset += (int)length;
            if ((length & 1) != 0)
            {
                if (offset >= bytes.Length)
                    throw new InvalidDataException("A padded RIFF/WAVE chunk is truncated.");
                offset++;
            }
        }

        if (format is null || data is null)
            throw new InvalidDataException("The RIFF/WAVE file must contain fmt and data chunks.");
        if (data.Length == 0 || data.Length % format.Value.BlockAlign != 0)
            throw new InvalidDataException("The RIFF/WAVE data chunk is empty or misaligned.");

        int sampleBytes = format.Value.BitsPerSample / 8;
        int sampleCount = data.Length / sampleBytes;
        var samples = new float[sampleCount];
        ReadOnlySpan<byte> source = data;
        for (int index = 0; index < sampleCount; index++)
        {
            int sampleOffset = index * sampleBytes;
            double value = (format.Value.Encoding, format.Value.BitsPerSample) switch
            {
                (WaveEncoding.Pcm, 8) => (source[sampleOffset] - 128) / 128.0,
                (WaveEncoding.Pcm, 16) => BinaryPrimitives.ReadInt16LittleEndian(source.Slice(sampleOffset, 2)) / 32768.0,
                (WaveEncoding.Pcm, 24) => ReadInt24(source.Slice(sampleOffset, 3)) / 8388608.0,
                (WaveEncoding.Pcm, 32) => BinaryPrimitives.ReadInt32LittleEndian(source.Slice(sampleOffset, 4)) / 2147483648.0,
                (WaveEncoding.Float, 32) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source.Slice(sampleOffset, 4))),
                _ => throw new InvalidDataException("The RIFF/WAVE sample encoding is unsupported.")
            };
            if (!double.IsFinite(value))
                throw new InvalidDataException("The RIFF/WAVE sample contains a non-finite value.");
            samples[index] = (float)Math.Clamp(value, -1, 1);
        }

        return new WavDecodedAudio(
            format.Value.SampleRate,
            format.Value.Channels,
            format.Value.BitsPerSample,
            format.Value.Encoding == WaveEncoding.Pcm ? "pcm" : "float",
            data.Length / format.Value.BlockAlign,
            samples);
    }

    public static SkyrimVoiceSampleAudio Measure(WavDecodedAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        double peak = 0;
        double sumSquares = 0;
        foreach (float sample in audio.Samples)
        {
            double magnitude = Math.Abs(sample);
            peak = Math.Max(peak, magnitude);
            sumSquares += sample * sample;
        }
        double rms = audio.Samples.Length == 0 ? 0 : Math.Sqrt(sumSquares / audio.Samples.Length);
        return new SkyrimVoiceSampleAudio(
            audio.SampleRate,
            audio.Channels,
            audio.BitsPerSample,
            audio.Format,
            audio.Frames,
            audio.Frames / (double)audio.SampleRate,
            ToDbfs(peak),
            ToDbfs(rms));
    }

    public static float[] Resample(ReadOnlySpan<float> samples, int sourceRate, int targetRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRate);
        if (samples.Length == 0)
            return [];
        if (sourceRate == targetRate)
            return samples.ToArray();

        int length = checked((int)Math.Round(samples.Length * (double)targetRate / sourceRate));
        var result = new float[length];
        for (int index = 0; index < length; index++)
        {
            double position = index * (double)sourceRate / targetRate;
            int left = Math.Min((int)position, samples.Length - 1);
            int right = Math.Min(left + 1, samples.Length - 1);
            double fraction = position - left;
            result[index] = (float)(samples[left] + (samples[right] - samples[left]) * fraction);
        }
        return result;
    }

    public static byte[] Normalize(WavDecodedAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var mono = new float[checked((int)audio.Frames)];
        for (int frame = 0; frame < mono.Length; frame++)
        {
            double sum = 0;
            for (int channel = 0; channel < audio.Channels; channel++)
                sum += audio.Samples[frame * audio.Channels + channel];
            mono[frame] = (float)(sum / audio.Channels);
        }
        return WritePcm16(Resample(mono, audio.SampleRate, NormalizedSampleRate), NormalizedSampleRate);
    }

    public static byte[] WritePcm16(ReadOnlySpan<float> samples, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        int dataLength = checked(samples.Length * 2);
        var bytes = new byte[checked(44 + dataLength)];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28, 4), checked(sampleRate * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34, 2), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40, 4), dataLength);
        for (int index = 0; index < samples.Length; index++)
        {
            short value = (short)Math.Clamp(
                Math.Round(Math.Clamp(samples[index], -1, 1) * 32767),
                short.MinValue,
                short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44 + index * 2, 2), value);
        }
        return bytes;
    }

    private static WaveFormat ParseFormat(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16)
            throw new InvalidDataException("The RIFF/WAVE fmt chunk is truncated.");
        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(2, 2));
        int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(4, 4));
        int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(12, 2));
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(14, 2));
        if (tag == 0xFFFE)
        {
            if (bytes.Length < 40 || BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(16, 2)) < 22)
                throw new InvalidDataException("The extensible RIFF/WAVE fmt chunk is truncated.");
            tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(24, 2));
        }
        WaveEncoding encoding = tag switch
        {
            1 when bits is 8 or 16 or 24 or 32 => WaveEncoding.Pcm,
            3 when bits == 32 => WaveEncoding.Float,
            _ => throw new InvalidDataException($"RIFF/WAVE format {tag} with {bits} bits is unsupported.")
        };
        int expectedAlign = checked(channels * bits / 8);
        if (channels <= 0 || sampleRate <= 0 || bits % 8 != 0 || blockAlign != expectedAlign)
            throw new InvalidDataException("The RIFF/WAVE fmt chunk is invalid.");
        return new WaveFormat(encoding, channels, sampleRate, bits, blockAlign);
    }

    private static int ReadInt24(ReadOnlySpan<byte> bytes)
    {
        int value = bytes[0] | bytes[1] << 8 | bytes[2] << 16;
        return (value & 0x800000) == 0 ? value : value | unchecked((int)0xFF000000);
    }

    private static double ToDbfs(double value) => value <= 0 ? SilenceFloorDbfs : 20 * Math.Log10(value);

    private enum WaveEncoding { Pcm, Float }
    private readonly record struct WaveFormat(WaveEncoding Encoding, int Channels, int SampleRate, int BitsPerSample, int BlockAlign);
}

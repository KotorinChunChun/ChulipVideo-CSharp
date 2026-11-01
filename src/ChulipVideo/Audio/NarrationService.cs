using System.Buffers.Binary;
using Windows.Media.SpeechSynthesis;

namespace ChulipVideo.Audio;

/// <summary>PCMのWAV（RIFF）の形式と音声データ。</summary>
public sealed record PcmWave(int Channels, int SampleRate, int BitsPerSample, byte[] Data)
{
    public int BlockAlign => Channels * BitsPerSample / 8;
    public double Duration => Data.Length / (double)(BlockAlign * SampleRate);

    public static PcmWave Parse(byte[] wav)
    {
        if (wav.Length < 44 || BinaryPrimitives.ReadUInt32BigEndian(wav) != 0x52494646 || BinaryPrimitives.ReadUInt32BigEndian(wav.AsSpan(8)) != 0x57415645)
            throw new InvalidDataException("WAVではありません。");
        int channels = 0, rate = 0, bits = 0;
        for (int offset = 12; offset + 8 <= wav.Length;)
        {
            uint id = BinaryPrimitives.ReadUInt32BigEndian(wav.AsSpan(offset));
            int size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(offset + 4));
            int body = offset + 8;
            if (size < 0 || body + size > wav.Length) size = wav.Length - body;
            if (id == 0x666D7420) // "fmt "
            {
                if (BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body)) != 1) throw new InvalidDataException("PCM以外のWAVには対応していません。");
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 14));
            }
            else if (id == 0x64617461) // "data"
            {
                if (channels == 0) throw new InvalidDataException("WAVの形式情報がありません。");
                return new(channels, rate, bits, wav.AsSpan(body, size).ToArray());
            }
            offset = body + size + (size & 1);
        }
        throw new InvalidDataException("WAVに音声データがありません。");
    }

    public byte[] ToBytes()
    {
        var wav = new byte[44 + Data.Length];
        "RIFF"u8.CopyTo(wav); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + Data.Length); "WAVE"u8.CopyTo(wav.AsSpan(8));
        "fmt "u8.CopyTo(wav.AsSpan(12)); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), (short)Channels); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), SampleRate * BlockAlign); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), (short)BlockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), (short)BitsPerSample);
        "data"u8.CopyTo(wav.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), Data.Length);
        Data.CopyTo(wav, 44);
        return wav;
    }
}

/// <summary>説明文をWindows標準の音声合成（オフライン）で読み上げ、手順の時刻に並べた1本の音声にする（R10）。</summary>
public static class NarrationService
{
    public sealed record Voice(string Id, string Name, string Language);

    public static IReadOnlyList<Voice> Voices()
    {
        try { return SpeechSynthesizer.AllVoices.Select(v => new Voice(v.Id, v.DisplayName, v.Language)).ToArray(); }
        catch (Exception ex) when (ex is TypeLoadException or System.Runtime.InteropServices.COMException or PlatformNotSupportedException) { return []; }
    }

    public static string? Unavailable() => Voices().Count == 0 ? "この Windows には音声合成の音声がありません。「設定」→「時刻と言語」→「音声認識」等で音声を追加してください。" : null;

    /// <summary>指定の音声、なければ日本語の音声、それもなければ既定の音声で読み上げたWAV。</summary>
    public static async Task<byte[]> SynthesizeAsync(string text, string? voiceId, double rate, CancellationToken ct = default)
    {
        using var synthesizer = new SpeechSynthesizer();
        var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == voiceId) ?? SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.StartsWith("ja", StringComparison.OrdinalIgnoreCase));
        if (voice is not null) synthesizer.Voice = voice;
        synthesizer.Options.SpeakingRate = Math.Clamp(rate, .5, 3);
        using var stream = await synthesizer.SynthesizeTextToStreamAsync(text).AsTask(ct).ConfigureAwait(false);
        using var input = stream.AsStreamForRead();
        using var output = new MemoryStream();
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
        return output.ToArray();
    }

    /// <summary>
    /// 各音声を指定の時刻（出力の秒）に置いた1本の音声。前の音声と重なる場合は、前の音声の後ろへずらす。
    /// すべて同じ形式であること（同じ音声合成で作った音声）。
    /// </summary>
    public static PcmWave Mix(IReadOnlyList<(double Time, PcmWave Wave)> clips, double minimumDuration = 0)
    {
        if (clips.Count == 0) throw new ArgumentException("ナレーションの音声がありません。");
        var format = clips[0].Wave;
        if (clips.Any(c => c.Wave.Channels != format.Channels || c.Wave.SampleRate != format.SampleRate || c.Wave.BitsPerSample != format.BitsPerSample))
            throw new InvalidDataException("ナレーションの音声の形式がそろっていません。");
        int align = format.BlockAlign;
        var placed = new List<(long Offset, byte[] Data)>();
        long cursor = 0;
        foreach (var (time, wave) in clips.OrderBy(c => c.Time))
        {
            long offset = Math.Max(cursor, (long)Math.Round(Math.Max(0, time) * format.SampleRate) * align);
            placed.Add((offset, wave.Data));
            cursor = offset + wave.Data.Length - wave.Data.Length % align;
        }
        long length = Math.Max(cursor, (long)Math.Ceiling(minimumDuration * format.SampleRate) * align);
        var data = new byte[checked((int)length)];
        foreach (var (offset, bytes) in placed) bytes.AsSpan(0, Math.Min(bytes.Length, data.Length - (int)offset)).CopyTo(data.AsSpan((int)offset));
        return format with { Data = data };
    }
}

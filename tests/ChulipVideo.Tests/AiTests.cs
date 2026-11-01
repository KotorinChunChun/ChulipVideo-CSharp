using System.Diagnostics;
using System.Text.Json;
using ChulipVideo.Ai;
using ChulipVideo.Audio;
using ChulipVideo.Core;
using ChulipVideo.Export;
using ChulipVideo.Media;
using ChulipVideo.Steps;
using EventLog = ChulipVideo.Export.EventLog;

namespace ChulipVideo.Tests;

/// <summary>v0.2.2 P9のAI説明文とナレーション（R10）を検証する。AIの実APIは呼ばず、送信を差し替えて確かめる。</summary>
internal static class AiTests
{
    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        Request();
        Parsing();
        await Describing();
        Settings(directory);
        Check(CredentialStore.Read("KotorinToys/ChulipVideo/存在しない試験用-" + Guid.NewGuid().ToString("N")) is null, "資格情報がなければnull（読み取りのみ・PCの設定は変えない）");
        Waves();
        string voice = NarrationService.Unavailable() is string reason ? "SKIP 音声合成: " + reason : await Speech();
        Console.WriteLine("PASS AI・ナレーション: 要求の内容・応答の解釈・失敗時の継続と打ち切り・設定・WAVの配置 / " + voice);
    }

    private static AiStepRequest Item(string description = "この位置をクリックします。", int number = 2) =>
        new(new StepItem { Kind = "Click", Label = "左クリック", Description = description }, number, [1, 2, 3, 4], "前の手順です。");

    private static void Request()
    {
        var request = ClaudeStepDescriber.BuildRequest("claude-opus-5-5", Item());
        string json = request.ToString() ?? "";
        Check(json.Contains("claude-opus-5-5", StringComparison.Ordinal) && json.Contains("\"low\"", StringComparison.Ordinal), "モデルとeffort low: " + json[..Math.Min(300, json.Length)]);
        Check(json.Contains("\"fallbacks\"", StringComparison.Ordinal) && json.Contains("default", StringComparison.Ordinal) && json.Contains("server-side-fallback-2026-07-01", StringComparison.Ordinal), "拒否時の代替モデル指定");
        Check(json.Contains("json_schema", StringComparison.Ordinal) && json.Contains("instruction", StringComparison.Ordinal) && json.Contains("additionalProperties", StringComparison.Ordinal), "構造化出力のスキーマ");
        Check(json.Contains(Convert.ToBase64String([1, 2, 3, 4]), StringComparison.Ordinal) && json.Contains("image/png", StringComparison.Ordinal), "画像をPNGのbase64で送る");
        string texts = string.Join("\n", Strings(JsonDocument.Parse(json).RootElement));
        Check(texts.Contains("前の手順です。", StringComparison.Ordinal) && texts.Contains("手順番号: 2", StringComparison.Ordinal) && texts.Contains("個人情報", StringComparison.Ordinal), "補足の文と、個人情報を書かない指示");
        Check(!json.Contains("temperature", StringComparison.Ordinal) && !json.Contains("budget_tokens", StringComparison.Ordinal), "使えないパラメーターを送らない");
    }

    // SDKは日本語を\uXXXXでJSONにするため、値を取り出して比べる。
    private static IEnumerable<string> Strings(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => [e.GetString()!],
        JsonValueKind.Object => e.EnumerateObject().SelectMany(p => Strings(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().SelectMany(Strings),
        _ => []
    };

    private static void Parsing()
    {
        Check(ClaudeStepDescriber.ParseInstruction(new AiReply("end_turn", "{\"instruction\":\"「保存」をクリックします。\",\"target\":\"保存\"}")) == "「保存」をクリックします。", "正常な応答");
        Check(ClaudeStepDescriber.ParseInstruction(new AiReply("refusal", "{\"instruction\":\"x\",\"target\":null}")) is null, "拒否は使わない");
        Check(ClaudeStepDescriber.ParseInstruction(new AiReply("max_tokens", "{\"instruction\":\"途中")) is null, "途中終了は使わない");
        Check(ClaudeStepDescriber.ParseInstruction("not json") is null && ClaudeStepDescriber.ParseInstruction("{\"target\":\"a\"}") is null && ClaudeStepDescriber.ParseInstruction("{\"instruction\":\"\"}") is null, "形式違い・空は使わない");
        Check(ClaudeStepDescriber.ParseInstruction("{\"instruction\":\"" + new string('あ', 201) + "\"}") is null, "長すぎる説明文は使わない");
        Check(ClaudeStepDescriber.ParseInstruction("{\"instruction\":\"1行目\\n2行目\"}") == "1行目 2行目", "改行を詰める");
    }

    private static async Task Describing()
    {
        int calls = 0;
        var describer = new ClaudeStepDescriber((request, ct) =>
        {
            calls++;
            return calls switch
            {
                1 => Task.FromResult(new AiReply("end_turn", "{\"instruction\":\"「送信」をクリックします。\",\"target\":\"送信\"}")),
                2 => Task.FromResult(new AiReply("refusal", "")),
                _ => throw new HttpRequestException("接続できません")
            };
        });
        AiStepRequest[] items = [Item(), Item(), Item()];
        var (updated, failures) = await describer.DescribeAsync("claude-opus-5-5", items);
        Check(updated == 1 && items[0].Step.Description == "「送信」をクリックします。" && items[0].Step.DescriptionSource == "Ai", "作れた説明文だけを反映");
        Check(items[1].Step.Description == "この位置をクリックします。" && failures.Count == 2 && failures[0].Contains("refusal", StringComparison.Ordinal) && failures[1].Contains("接続できません", StringComparison.Ordinal), "拒否・失敗の手順はそのまま残して理由を返す");
        int attempts = 0;
        var broken = new ClaudeStepDescriber((_, _) => { attempts++; throw new HttpRequestException("401"); });
        await broken.DescribeAsync("m", [Item(), Item(), Item(), Item(), Item()]);
        Check(attempts == 3, "1件も作れずに3件失敗したら打ち切る: " + attempts);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        try { await describer.DescribeAsync("m", [Item()], null, cancel.Token); Check(false, "中断を通知"); } catch (OperationCanceledException) { }
    }

    private static void Settings(string directory)
    {
        string path = Path.Combine(directory, "ai-settings.json");
        File.WriteAllText(path, "{\"Ai\":{\"Enabled\":true,\"Model\":\"  \"},\"Narration\":{\"Enabled\":true,\"VoiceId\":null,\"Rate\":99}}");
        var loaded = new SettingsStore(path).Load();
        Check(loaded.Ai is { Enabled: true, Model: "claude-opus-5-5" } && loaded.Narration is { Enabled: true, VoiceId: "", Rate: 3 }, "AI・ナレーションの設定の補正");
        File.WriteAllText(path, "{}");
        var defaults = new SettingsStore(path).Load();
        Check(!defaults.Ai.Enabled && !defaults.Narration.Enabled && !File.ReadAllText(path).Contains("ApiKey", StringComparison.OrdinalIgnoreCase), "既定ではAI・ナレーションを使わず、APIキーを設定ファイルに持たない");
    }

    private static PcmWave Tone(double seconds, short value) => new(1, 8000, 16, Enumerable.Range(0, (int)(seconds * 8000)).SelectMany(_ => BitConverter.GetBytes(value)).ToArray());

    private static void Waves()
    {
        var a = Tone(.5, 1000);
        var parsed = PcmWave.Parse(a.ToBytes());
        Check(parsed.SampleRate == 8000 && parsed.Data.SequenceEqual(a.Data) && Math.Abs(parsed.Duration - .5) < 1e-9, "WAVの書き出しと読み込み");
        var mixed = NarrationService.Mix([(0, a), (.1, Tone(.2, 2000)), (2.0, Tone(.1, 3000))], 3);
        short At(double t) => BitConverter.ToInt16(mixed.Data, (int)(t * 8000) * 2);
        Check(At(.05) == 1000 && At(.55) == 2000 && At(.75) == 0 && At(2.05) == 3000 && Math.Abs(mixed.Duration - 3) < 1e-9, "重なる読み上げは後ろへずらし、離れた読み上げは時刻どおり");
        try { NarrationService.Mix([(0, a), (1, new PcmWave(2, 8000, 16, new byte[8]))]); Check(false, "形式違いを拒否"); } catch (InvalidDataException) { }
    }

    private static async Task<string> Speech()
    {
        var wave = PcmWave.Parse(await NarrationService.SynthesizeAsync("保存ボタンをクリックします。", null, 1));
        Check(wave.Duration > .5 && wave.Data.Any(b => b != 0), "日本語の読み上げ");
        return $"音声合成 {NarrationService.Voices().Count} 音声";
    }

    public static async Task RunMediaAsync(string ffmpeg, string outputDirectory)
    {
        string directory = Path.Combine(outputDirectory, "AI検証 " + Guid.NewGuid().ToString("N"));
        string frames = Path.Combine(directory, "素材");
        Directory.CreateDirectory(frames);
        using (var bitmap = new Bitmap(2000, 1000))
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White);
            for (int i = 0; i < 20; i++) ExportService.SavePng(bitmap, Path.Combine(frames, $"f_{i:D3}.png"), 1);
        }
        string source = Path.Combine(directory, "大きい画面.mkv");
        await Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-framerate", "10", "-i", Path.Combine(frames, "f_%03d.png"), "-c:v", "ffv1", source]);
        var service = new FfmpegService(ffmpeg);
        var info = await service.ProbeAsync(source);
        var step = new StepItem { Kind = "Click", FrameTime = .5, Time = .7, X = 1000, Y = 500, Description = "この位置をクリックします。" };
        var document = new StepDocument { Crop = CropRect.Full(info), End = info.Duration, Steps = [step] };
        var mask = new MaskRegion { Rect = new(0, 0, 400, 200), Start = 0, End = 2, Mode = MaskMode.Fill, Color = "#00FF00" };
        var items = await ClaudeStepDescriber.PrepareAsync(service, info, EventLog.Empty, document, [step], new AppSettings(), [mask]);
        using (var sent = new Bitmap(new MemoryStream(items.Single().Png)))
        {
            Check(Math.Max(sent.Width, sent.Height) <= ClaudeStepDescriber.MaxImageEdge && sent.Width == 1568, "送る画像は長辺1568px以下: " + sent.Size);
            Check(sent.GetPixel(20, 20).G > 200 && sent.GetPixel(20, 20).R < 60, "送る画像にはマスクを適用");
        }
        Check(items.Single().Number == 1 && items.Single().Previous is null, "手順番号と前の手順");

        if (NarrationService.Unavailable() is string reason) { Console.WriteLine("SKIP ナレーションの書き出し: " + reason); Directory.Delete(directory, true); return; }
        string movie = Path.Combine(directory, "ナレーション.mp4");
        var settings = new AppSettings { ExportTsv = false, ExportSrt = false, ExportVtt = false, Narration = new NarrationSettings { Enabled = true } };
        await new ExportService(service).ExportAsync(new ExportRequest(info, new CropRect(0, 0, 640, 360), 0, info.Duration, movie, ExportKind.Mp4, settings)
            { Narration = [(.2, "保存します。"), (.3, "閉じます。")] }, EventLog.Empty, null, CancellationToken.None);
        string probe = await Run(ffmpeg, ["-hide_banner", "-i", movie], allowFailure: true);
        Check(probe.Contains("Audio: aac", StringComparison.Ordinal), "MP4に音声トラック: " + probe);
        var output = await service.ProbeAsync(movie);
        Check(Math.Abs(output.Duration - info.Duration) < .2, "読み上げが長くても動画の長さで切る: " + output.Duration);
        string silent = Path.Combine(directory, "音声なし.mp4");
        await new ExportService(service).ExportAsync(new ExportRequest(info, new CropRect(0, 0, 640, 360), 0, info.Duration, silent, ExportKind.Mp4, settings), EventLog.Empty, null, CancellationToken.None);
        Check(!(await Run(ffmpeg, ["-hide_banner", "-i", silent], allowFailure: true)).Contains("Audio:", StringComparison.Ordinal), "ナレーションがなければ音声なし");
        Directory.Delete(directory, true);
        Console.WriteLine("PASS AI・ナレーション出力: 送る画像の縮小とマスク・MP4のナレーション音声・動画の長さで切る・音声なし");
    }

    private static async Task<string> Run(string executable, IEnumerable<string> arguments, bool allowFailure = false)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("FFmpegを起動できません。");
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 && !allowFailure) throw new IOException(error);
        return error;
    }

    private static void Check(bool condition, string label) { if (!condition) throw new Exception("AI検証失敗: " + label); }
}

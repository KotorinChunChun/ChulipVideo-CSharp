using ChulipVideo.Core;
using ChulipVideo.Steps;

namespace ChulipVideo.UI;

/// <summary>MP4のナレーション（R10）。保存済みの手順の説明文を、手順の時刻に読み上げる。</summary>
public sealed partial class MainForm
{
    private IReadOnlyList<(double Time, string Text)> NarrationLines(ExportKind kind)
    {
        if (kind != ExportKind.Mp4 || !settings.Narration.Enabled || current is null) return [];
        if (Audio.NarrationService.Unavailable() is string reason) { Status("ナレーションなしで書き出します: " + reason); return []; }
        var document = StepStore.Load(current, out _);
        var lines = document?.Steps.Where(s => s.Enabled && s.Description.Length > 0 && s.Time >= rangeStart && s.Time < rangeEnd)
            .Select(s => (s.Time, s.Description)).ToArray() ?? [];
        if (lines.Length == 0) Status("ナレーションにする手順がありません。手順書エディターで手順を保存してください（ナレーションなしで書き出します）。");
        return lines;
    }
}

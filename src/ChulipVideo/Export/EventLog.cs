using System.Globalization;
using System.Text;
using ChulipVideo.Core;

namespace ChulipVideo.Export;

public sealed record InputEvent(double Timestamp, int Frame, int X, int Y, string Click, string Keys);
public sealed record InputCaption(string Text, double Start, double HeldUntil);
public sealed record ClickRipple(double Timestamp, int X, int Y, string Button);

/// <summary>録画済みTSVを読み取り、シークしても変わらない入力履歴を構築する。</summary>
public sealed class EventLog
{
    public static EventLog Empty { get; } = new([]);
    public IReadOnlyList<InputEvent> Events { get; }
    public IReadOnlyList<InputCaption> Captions { get; }
    public IReadOnlyList<ClickRipple> Ripples { get; }
    public int Count => Events.Count;

    public EventLog(IEnumerable<InputEvent> events)
    {
        Events = events.Where(e => double.IsFinite(e.Timestamp) && e.Timestamp >= 0)
            .OrderBy(e => e.Timestamp).ToArray();
        var captions = new List<InputCaption>();
        var ripples = new List<ClickRipple>();
        var previous = new HashSet<string>();
        var lastPress = new Dictionary<string, double>();
        var clickCounts = new Dictionary<string, int>();
        string previousText = "";
        InputEvent? previousEvent = null;
        foreach (var entry in Events)
        {
            var buttons = MouseButtons(entry.Click).ToHashSet();
            foreach (string button in new[] { "L-Click", "R-Click", "M-Click" })
            {
                if (buttons.Contains(button) && !previous.Contains(button))
                {
                    clickCounts[button] = lastPress.TryGetValue(button, out double old) && entry.Timestamp - old < .35
                        ? clickCounts.GetValueOrDefault(button, 1) + 1 : 1;
                    lastPress[button] = entry.Timestamp;
                }
                if (previous.Contains(button) && !buttons.Contains(button))
                    ripples.Add(new(entry.Timestamp, previousEvent?.X ?? entry.X, previousEvent?.Y ?? entry.Y, button));
            }
            string text = DisplayText(entry.Click, entry.Keys);
            foreach (var pair in new[] { ("L-Click", "左"), ("R-Click", "右"), ("M-Click", "中") })
                if (buttons.Contains(pair.Item1) && clickCounts.GetValueOrDefault(pair.Item1) >= 2)
                    text = text.Replace(pair.Item2 + "クリック", pair.Item2 + "ダブルクリック", StringComparison.Ordinal);

            // 離された時刻まで保持する。同じ入力を離して押し直した場合は別の履歴になる。
            if (previousText.Length > 0 && captions.Count > 0)
                captions[^1] = captions[^1] with { HeldUntil = entry.Timestamp };
            if (text.Length > 0 && text != previousText)
                captions.Add(new(text, entry.Timestamp, entry.Timestamp));
            previousText = text;
            previous = buttons;
            previousEvent = entry;
        }
        Captions = captions;
        Ripples = ripples;
    }

    public static EventLog Load(string tsvPath)
    {
        if (!File.Exists(tsvPath)) return Empty;
        var result = new List<InputEvent>();
        foreach (string line in File.ReadLines(tsvPath, Encoding.UTF8))
        {
            string[] cells = line.TrimEnd('\r', '\n').Split('\t');
            if (cells.Length < 4 || !double.TryParse(cells[0].TrimStart('\uFEFF'), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)) continue;
            if (!int.TryParse(cells[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
                !int.TryParse(cells[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)) continue;
            _ = int.TryParse(cells[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int frame);
            result.Add(new(seconds, frame, x, y, cells.Length > 4 ? cells[4] : "None", cells.Length > 5 ? cells[5] : "None"));
        }
        return new(result);
    }

    private PointerPath? pointerPath;

    /// <summary>描くポインターの位置（元動画の座標）。最初の記録より前はnull。</summary>
    public PointF? PointerAt(double seconds, string smoothing) => (pointerPath ??= new PointerPath(Events)).At(seconds, smoothing);

    public InputEvent? AtOrBefore(double seconds)
    {
        int lo = 0, hi = Events.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (Events[mid].Timestamp <= seconds + 1e-8) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found >= 0 ? Events[found] : null;
    }

    public IReadOnlyList<InputCaption> ActiveCaptions(double seconds, double fadeDuration, int maxStack)
    {
        int lo = 0, hi = Captions.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (Captions[mid].Start <= seconds) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        var active = new List<InputCaption>();
        int limit = Math.Clamp(maxStack, 1, 20);
        double fade = Math.Max(.01, fadeDuration);
        for (int i = found; i >= 0 && active.Count < limit; i--)
        {
            if (seconds >= Captions[i].HeldUntil + fade) break;
            active.Add(Captions[i]);
        }
        return active;
    }

    public IReadOnlyList<ClickRipple> ActiveRipples(double seconds, double duration)
    {
        int lo = 0, hi = Ripples.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (Ripples[mid].Timestamp <= seconds) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        var active = new List<ClickRipple>();
        for (int i = found; i >= 0 && seconds - Ripples[i].Timestamp < duration; i--) active.Add(Ripples[i]);
        return active;
    }

    public static IEnumerable<string> MouseButtons(string value)
    {
        foreach (string part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "None") continue;
            if (part.Contains('-')) { yield return part; continue; }
            // 旧形式はL/R/Mの連結。その他の文字をクリックに誤変換しない。
            if (part.All(c => c is 'L' or 'R' or 'M'))
                foreach (char c in part.Distinct()) yield return c + "-Click";
        }
    }

    public static string DisplayText(string click, string keys)
    {
        string[] order = ["Ctrl", "Shift", "Alt", "Win"];
        string[] raw = keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => k != "None").Select(k => k.Length == 1 ? k.ToUpperInvariant() : k).Distinct().ToArray();
        var parts = order.Where(raw.Contains).ToList();
        parts.AddRange(MouseButtons(click).Distinct().Select(m => m switch
        {
            "L-Click" => "左クリック", "R-Click" => "右クリック", "M-Click" => "中クリック",
            "X1-Click" => "戻る", "X2-Click" => "進む", "Wheel-Up" => "ホイール▲",
            "Wheel-Down" => "ホイール▼", "Wheel-Left" => "ホイール◀", "Wheel-Right" => "ホイール▶", _ => m
        }));
        parts.AddRange(raw.Where(k => !order.Contains(k)));
        return parts.Any(p => !order.Contains(p)) ? string.Join(" + ", parts) : "";
    }

    /// <summary>
    /// 切り出し開始を0秒・クロップ原点を(0,0)とする入力ログ。viewを渡すと（自動ズーム）、各行の時刻の表示範囲で
    /// 出力動画の座標へ変換する（生成した動画を再編集してもポインターがずれない）。範囲外(-1)の座標はそのまま残す。
    /// </summary>
    /// timeを渡すと（無操作区間のカット・早送り）、各行の時刻を出力の時刻へ写す。
    public string ToTsv(double start, double end, double fps, CropRect crop, Func<double, RectangleF>? view, Func<double, double>? time = null)
    {
        if (view is null && time is null) return ToTsv(start, end, fps, crop);
        var text = new StringBuilder("timestamp\tframe\tx\ty\tclick\tkeys\n");
        foreach (var row in Rows(start, end))
        {
            double relative = time?.Invoke(row.Timestamp) ?? Math.Max(0, row.Timestamp - start);
            int x = row.X - crop.X, y = row.Y - crop.Y;
            if (view is not null)
            {
                var v = view(row.Timestamp);
                x = row.X < 0 ? row.X : (int)Math.Round((row.X - v.X) * crop.Width / v.Width);
                y = row.Y < 0 ? row.Y : (int)Math.Round((row.Y - v.Y) * crop.Height / v.Height);
            }
            text.Append(CultureInfo.InvariantCulture, $"{relative:F3}\t{(int)Math.Floor(relative * fps + 1e-7)}\t{x}\t{y}\t{row.Click}\t{row.Keys}\n");
        }
        return text.ToString();
    }

    private List<InputEvent> Rows(double start, double end)
    {
        var rows = Events.Where(e => e.Timestamp >= start && e.Timestamp < end).ToList();
        InputEvent? atStart = AtOrBefore(start);
        if (atStart is not null && atStart.Timestamp < start && start < end)
            rows.Insert(0, atStart with { Timestamp = start });
        return rows;
    }

    public string ToTsv(double start, double end, double fps, CropRect crop)
    {
        var text = new StringBuilder("timestamp\tframe\tx\ty\tclick\tkeys\n");
        var rows = Events.Where(e => e.Timestamp >= start && e.Timestamp < end).ToList();
        // 切り出し開始で押されているキーとポインターを新しい動画の0秒にも再現する。
        InputEvent? atStart = AtOrBefore(start);
        if (atStart is not null && atStart.Timestamp < start && start < end)
            rows.Insert(0, atStart with { Timestamp = start });
        foreach (var row in rows)
        {
            double relative = Math.Max(0, row.Timestamp - start);
            text.Append(CultureInfo.InvariantCulture, $"{relative:F3}\t{(int)Math.Floor(relative * fps + 1e-7)}\t{row.X - crop.X}\t{row.Y - crop.Y}\t{row.Click}\t{row.Keys}\n");
        }
        return text.ToString();
    }

    /// <summary>timeを渡すと、字幕の開始・終了を出力の時刻へ写す。カットで長さが0になった字幕は出さない。</summary>
    public string ToSubtitles(double start, double end, OverlayOptions options, bool webVtt, Func<double, double>? time = null)
    {
        double fade = Math.Max(.01, options.FadeDuration);
        var points = Captions.SelectMany(c => new[] { c.Start, c.HeldUntil + fade })
            .Where(t => t > start && t < end).Append(start).Append(end).Distinct().Order().ToArray();
        var cues = new List<(double Start, double End, string Text)>();
        for (int i = 0; i + 1 < points.Length; i++)
        {
            double left = points[i], right = points[i + 1];
            if (right <= left) continue;
            string content = string.Join('\n', ActiveCaptions((left + right) / 2, fade, options.MaxStack).Select(c => c.Text));
            if (content.Length == 0) continue;
            if (cues.Count > 0 && cues[^1].Text == content && Math.Abs(cues[^1].End - left) < 1e-8)
                cues[^1] = (cues[^1].Start, right, content);
            else cues.Add((left, right, content));
        }
        var output = new StringBuilder(webVtt ? "WEBVTT\n\n" : "");
        int index = 0;
        foreach (var cue in cues)
        {
            double from = time?.Invoke(cue.Start) ?? cue.Start - start, to = time?.Invoke(cue.End) ?? cue.End - start;
            if (to - from < .001) continue;
            if (!webVtt) output.Append(++index).Append('\n');
            output.Append(Timestamp(from, webVtt)).Append(" --> ").Append(Timestamp(to, webVtt)).Append('\n');
            output.Append(cue.Text).Append("\n\n");
        }
        return output.ToString();
    }

    private static string Timestamp(double seconds, bool webVtt)
    {
        long milliseconds = Math.Max(0, (long)Math.Round(seconds * 1000));
        return FormattableString.Invariant($"{milliseconds / 3600000:D2}:{milliseconds / 60000 % 60:D2}:{milliseconds / 1000 % 60:D2}{(webVtt ? '.' : ',')}{milliseconds % 1000:D3}");
    }
}

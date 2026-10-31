namespace ChulipVideo.Export;

public enum InputActionKind { Click, DoubleClick, RightClick, MiddleClick, Drag, Wheel, Shortcut, Text }

/// <summary>入力ログから取り出した1回の操作。Timeは操作の開始（押下）時刻、座標は元動画のピクセル。</summary>
public sealed record InputAction(InputActionKind Kind, double Time, double EndTime, Point Point, Point? EndPoint, string Label, IReadOnlyList<string> Keys);

/// <summary>TSVの各行（その時点で押されている状態）から、押下の立ち上がりを操作として取り出す。</summary>
public static class InputActions
{
    public const double DoubleClickInterval = .35, TextMergeGap = 1.5, WheelMergeGap = 1;
    public const int DragDistance = 8;
    private static readonly string[] Modifiers = ["Ctrl", "Shift", "Alt", "Win"];

    public static IReadOnlyList<InputAction> Extract(EventLog log, double start = 0, double end = double.PositiveInfinity)
    {
        var actions = new List<InputAction>();
        var pressed = new Dictionary<string, (double Time, Point Point)>();
        var previousButtons = new HashSet<string>();
        var previousKeys = new HashSet<string>();
        Point last = Point.Empty;
        foreach (var entry in log.Events)
        {
            var point = new Point(entry.X, entry.Y);
            var buttons = EventLog.MouseButtons(entry.Click).ToHashSet();
            var keys = Keys(entry.Keys);
            var modifiers = Modifiers.Where(keys.Contains).ToArray();
            foreach (string button in buttons.Where(b => !previousButtons.Contains(b)))
            {
                if (button.StartsWith("Wheel-", StringComparison.Ordinal)) AddWheel(actions, entry.Timestamp, point, button, modifiers);
                else pressed[button] = (entry.Timestamp, point);
            }
            // 解放の行の座標ではなく、押している間の最後の座標でドラッグ終点を決める。
            foreach (string button in previousButtons.Where(b => !buttons.Contains(b)).ToArray())
                if (pressed.Remove(button, out var press)) AddRelease(actions, button, press.Time, press.Point, entry.Timestamp, last, modifiers);
            foreach (string key in keys.Where(k => !previousKeys.Contains(k) && !Modifiers.Contains(k)))
                AddKey(actions, entry.Timestamp, point, key, modifiers);
            previousButtons = buttons;
            previousKeys = keys;
            last = point;
        }
        double endOfLog = log.Events.Count > 0 ? log.Events[^1].Timestamp : 0;
        foreach (var (button, press) in pressed) AddRelease(actions, button, press.Time, press.Point, endOfLog, last, []);
        return actions.Where(a => a.Time >= start && a.Time < end).OrderBy(a => a.Time).ToArray();
    }

    private static HashSet<string> Keys(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => k != "None").Select(k => k.Length == 1 ? k.ToUpperInvariant() : k).ToHashSet();

    private static void AddWheel(List<InputAction> actions, double time, Point point, string button, string[] modifiers)
    {
        if (actions.Count > 0 && actions[^1] is { Kind: InputActionKind.Wheel } previous && time - previous.EndTime <= WheelMergeGap)
        {
            actions[^1] = previous with { EndTime = time };
            return;
        }
        actions.Add(new(InputActionKind.Wheel, time, time, point, null, EventLog.DisplayText(button, string.Join(',', modifiers)), modifiers));
    }

    private static void AddRelease(List<InputAction> actions, string button, double time, Point point, double endTime, Point endPoint, string[] modifiers)
    {
        string label = EventLog.DisplayText(button, string.Join(',', modifiers));
        if (button == "L-Click" && Distance(point, endPoint) > DragDistance)
        {
            actions.Add(new(InputActionKind.Drag, time, endTime, point, endPoint, label.Replace("左クリック", "ドラッグ", StringComparison.Ordinal), modifiers));
            return;
        }
        var kind = button switch
        {
            "L-Click" => InputActionKind.Click, "R-Click" => InputActionKind.RightClick, "M-Click" => InputActionKind.MiddleClick, _ => InputActionKind.Shortcut
        };
        // 同じボタンを短い間隔で同じ位置に押し直したら、1件のダブルクリックにまとめる。
        int index = actions.FindLastIndex(a => a.Kind is InputActionKind.Click or InputActionKind.DoubleClick);
        if (kind == InputActionKind.Click && index >= 0 && actions[index] is var previous && time - previous.Time <= DoubleClickInterval * (previous.Kind == InputActionKind.DoubleClick ? 2 : 1)
            && Distance(previous.Point, point) <= DragDistance)
        {
            actions[index] = previous with { Kind = InputActionKind.DoubleClick, EndTime = endTime, Label = previous.Label.Replace("左クリック", "左ダブルクリック", StringComparison.Ordinal) };
            return;
        }
        actions.Add(new(kind, time, endTime, point, null, label, modifiers));
    }

    private static void AddKey(List<InputAction> actions, double time, Point point, string key, string[] modifiers)
    {
        bool printable = key.Length == 1 || key == "Space";
        if (printable && !modifiers.Any(m => m != "Shift"))
        {
            if (actions.Count > 0 && actions[^1] is { Kind: InputActionKind.Text } previous && time - previous.EndTime <= TextMergeGap)
                actions[^1] = previous with { EndTime = time, Keys = [.. previous.Keys, key] };
            else actions.Add(new(InputActionKind.Text, time, time, point, null, "文字入力", [key]));
            return;
        }
        string[] combination = [.. modifiers, key];
        actions.Add(new(InputActionKind.Shortcut, time, time, point, null, EventLog.DisplayText("None", string.Join(',', combination)), combination));
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));
}

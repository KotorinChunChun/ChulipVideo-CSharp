using System.Globalization;
using System.Reflection;
using ChulipVideo.Core;
using ChulipVideo.UI;

namespace ChulipVideo.Tests;

public static class EditingControlTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { TimecodeEditing(); SettingsTabsAndCopy(); CanvasAppearance(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("時刻入力・設定タブ・枠描画の検証に失敗しました。", failure);
        Console.WriteLine("時刻入力・設定タブ・枠描画の検証: 成功 (解析/表示/上下限/編集確定/キー保護/設定分離/配色)");
    }

    private static void TimecodeEditing()
    {
        foreach (var (text, expected) in new (string, decimal)[]
        {
            ("0", 0), ("123.456", 123.456m), (" 1:02.345 ", 62.345m),
            ("01:02:03.004", 3723.004m), ("25:00:00", 90000), ("240:00:00.000", 864000),
            ("150:15.250", 9015.25m), (".5", 0.5m)
        })
        {
            True(TimecodeUpDown.TryParseTimecode(text, out var parsed), $"時刻を解析: {text}");
            Equal(expected, parsed, $"解析後の秒数: {text}");
        }
        foreach (string text in new[] { "", " ", "abc", "1:60", "1:60:00", "1:2:60", "-1:02", "1::2", "1:2:3:4", "NaN", "Infinity", "1e3", "1,5", "999999999999999999999999999999999999:01" })
            True(!TimecodeUpDown.TryParseTimecode(text, out _), $"不正な時刻を拒否: {text}");
        Equal("00:00:00.000", TimecodeUpDown.FormatTimecode(0), "時刻の初期表示");
        Equal("01:02:03.004", TimecodeUpDown.FormatTimecode(3723.004m), "ミリ秒のゼロ埋め");
        Equal("25:00:00.000", TimecodeUpDown.FormatTimecode(90000), "24時間で折り返さない");
        Equal("00:01:00.000", TimecodeUpDown.FormatTimecode(59.9996m), "ミリ秒丸めの繰上り");

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            True(TimecodeUpDown.TryParseTimecode("1:02.125", out var parsed), "小数点はOS地域によらず一定");
            Equal(62.125m, parsed, "地域設定で秒数が変わらない");
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }

        using var parent = new ShortcutProbeForm();
        using var input = new TimecodeUpDown();
        parent.Controls.Add(input);
        var editor = input.Controls.OfType<TextBox>().Single();
        Equal(0m, input.Minimum, "時刻の下限");
        Equal(864000m, input.Maximum, "時刻の上限");
        Equal("00:00:00.000", input.Text, "生成直後の表示");
        input.Value = 12.5m;
        Equal("00:00:12.500", input.Text, "外部更新を時刻表示");
        input.Value = 12.123456m;
        input.CommitEdit();
        Equal(12.123456m, input.Value, "未編集の確定では元の秒数精度を保つ");

        int changed = 0;
        input.ValueChanged += (_, _) => changed++;
        editor.Text = "1:";
        Equal(12.123456m, ((NumericUpDown)input).Value, "Value参照は未確定入力を確定しない");
        Equal("1:", input.Text, "入力途中の区切り文字をValue参照で巻き戻さない");
        editor.Text = "01:02:03.125";
        Equal("01:02:03.125", input.Text, "編集中のテキストを保持");
        Equal(12.123456m, input.Value, "完成した入力も明示確定までは元の秒数を返す");
        Equal(0, changed, "入力途中では確定通知しない");
        var enter = new KeyEventArgs(Keys.Enter);
        Invoke(input, "OnTextBoxKeyDown", editor, enter);
        Equal(3723.125m, input.Value, "Enterで確定");
        True(enter.Handled && enter.SuppressKeyPress, "Enterを親の既定ボタンへ渡さない");
        Equal(1, changed, "確定の通知は一度");

        editor.Text = "無効";
        True(!input.CommitEdit(), "無効入力を報告");
        Equal(3723.125m, input.Value, "無効入力で前の値を維持");
        Equal("01:02:03.125", input.Text, "無効入力を前の時刻表示に戻す");
        Equal(1, changed, "無効入力で変更通知しない");

        editor.Text = "1:02.5";
        Invoke(input, "OnTextBoxLostFocus", editor, EventArgs.Empty);
        Equal(62.5m, input.Value, "フォーカス離脱で確定");
        Equal("00:01:02.500", input.Text, "確定後の表示を統一");
        editor.Text = "2:00.125";
        input.UpButton();
        Equal(120.225m, input.Value, "未確定の時刻からスピン加算");
        editor.Text = "1:00.5";
        input.DownButton();
        Equal(60.4m, input.Value, "未確定の時刻からスピン減算");
        editor.Text = "999999";
        input.CommitEdit();
        Equal(864000m, input.Value, "範囲外入力を上限へ制限");
        Equal("240:00:00.000", input.Text, "上限の時刻表示");
        input.UpButton();
        Equal(864000m, input.Value, "上限で増加しない");
        editor.Text = "-1";
        input.CommitEdit();
        Equal(0m, input.Value, "負の入力を下限へ制限");
        input.DownButton();
        Equal(0m, input.Value, "下限で減少しない");
        editor.Text = "5:00";
        input.Value = 42.125m;
        Equal(42.125m, input.Value, "外部更新を古い編集中テキストで巻き戻さない");
        Equal("00:00:42.125", input.Text, "外部更新後の表示");
        input.Text = "2:30.5";
        Equal(150.5m, input.Value, "プログラムからのText設定は確定");

        foreach (char character in new[] { '1', ':', '.', '\b', '\u0003', '\u0016' })
        {
            var key = new KeyPressEventArgs(character);
            Invoke(input, "OnTextBoxKeyPress", editor, key);
            True(!key.Handled, $"時刻と編集用の文字を入力可能: {(int)character}");
        }
        var invalidKey = new KeyPressEventArgs('x');
        Invoke(input, "OnTextBoxKeyPress", editor, invalidKey);
        True(invalidKey.Handled, "通常の英字を数値欄へ入れない");
        foreach (var key in new[] { Keys.Left, Keys.Right | Keys.Control, Keys.Home, Keys.End | Keys.Shift, Keys.Delete, Keys.Space, Keys.Control | Keys.C, Keys.Control | Keys.V })
        {
            True(!Command(input, key), $"通常編集を内部TextBoxへ渡す: {key}");
            Equal(0, parent.CommandCount, $"親のショートカットを呼ばない: {key}");
        }
        True(Command(input, Keys.Control | Keys.A), "Ctrl+Aを処理");
        Equal(input.Text.Length, editor.SelectionLength, "Ctrl+Aで時刻全体を選択");
        editor.Text = "1:05.250";
        True(Command(input, Keys.Enter), "コマンド経由でもEnterを確定");
        Equal(65.25m, input.Value, "コマンド経由の確定値");
    }

    private static void SettingsTabsAndCopy()
    {
        var original = new AppSettings { CanvasColor = "#123456", CropColor = "#345678", FfmpegPath = "" };
        using var form = new SettingsForm(original);
        var tabs = Descendants(form).OfType<TabControl>().Single();
        Equal("ポインター・クリック|入力字幕|テーマ|出力・FFmpeg|演出", string.Join('|', tabs.TabPages.Cast<TabPage>().Select(page => page.Text)), "設定タブの順序");
        Equal("保存", ((Button)form.AcceptButton!).Text, "適用ボタンの表記");
        Equal("#123456", form.Settings.CanvasColor, "既存キャンバス色を維持");
        Equal("#345678", form.Settings.CropColor, "既存枠色を維持");
        form.Settings.CanvasColor = "#FFFFFF";
        form.Settings.Overlay.PointerColor = "#ABCDEF";
        form.Settings.Presets.Clear();
        form.DialogResult = DialogResult.Cancel;
        Equal("#123456", original.CanvasColor, "キャンセルで元の色を変更しない");
        True(original.Overlay.PointerColor != "#ABCDEF", "入力表示設定を独立して編集");
        True(original.Presets.Count > 0, "プリセット一覧を独立して編集");
    }

    private static void CanvasAppearance()
    {
        using var canvas = new VideoCanvas { Size = new Size(672, 392), VideoSize = new Size(640, 360), Crop = new CropRect(100, 80, 300, 180) };
        Equal("#787878", canvas.CanvasColor, "キャンバスの既定色");
        Equal("#FF8C00", canvas.CropColor, "枠の既定色");
        using (var normal = Paint(canvas))
        {
            Equal(Color.FromArgb(120, 120, 120).ToArgb(), normal.GetPixel(2, 2).ToArgb(), "既定背景を描画");
            Equal(Color.White.ToArgb(), normal.GetPixel(116, 96).ToArgb(), "ハンドルは白塗り");
            True(CountColor(normal, Color.DarkOrange) > 50, "通常の枠は橙色");
            int colored = Enumerable.Range(130, 100).Count(x => normal.GetPixel(x, 96).ToArgb() == Color.DarkOrange.ToArgb());
            True(colored > 5 && colored < 95, "通常の枠は破線");
        }
        var crop = canvas.Crop;
        Invoke(canvas, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, 150, 140, 0));
        using (var hover = Paint(canvas)) True(CountColor(hover, Color.Red) > 50, "ホバー時は赤で強調");
        Equal(crop, canvas.Crop, "ホバーで切り出し座標を変更しない");
        canvas.CropColor = "#00FF00";
        using (var custom = Paint(canvas)) True(CountColor(custom, Color.Lime) > 50, "ホバー中も保存した独自色を保持");
        Invoke(canvas, "OnMouseLeave", EventArgs.Empty);
        canvas.SizeLocked = true;
        using var locked = Paint(canvas);
        True(locked.GetPixel(116, 96).ToArgb() != Color.White.ToArgb(), "サイズロック時に白ハンドルを隠す");
    }

    private static Bitmap Paint(VideoCanvas canvas)
    {
        var bitmap = new Bitmap(canvas.Width, canvas.Height);
        using var graphics = Graphics.FromImage(bitmap);
        Invoke(canvas, "OnPaint", new PaintEventArgs(graphics, canvas.ClientRectangle));
        return bitmap;
    }

    private static int CountColor(Bitmap bitmap, Color color)
    {
        int count = 0;
        for (int x = 100; x < 430; x++)
            if (bitmap.GetPixel(x, 96).ToArgb() == color.ToArgb()) count++;
        return count;
    }

    private static IEnumerable<Control> Descendants(Control control)
    {
        foreach (Control child in control.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static bool Command(TimecodeUpDown control, Keys key) =>
        (bool)Invoke(control, "ProcessCmdKey", Message.Create(IntPtr.Zero, 0x100, IntPtr.Zero, IntPtr.Zero), key)!;

    private static object? Invoke(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, Hidden)!.Invoke(target, args);

    private static void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{name}: expected={expected}, actual={actual}");
    }

    private static void True(bool condition, string name) { if (!condition) throw new Exception(name); }

    private sealed class ShortcutProbeForm : Form
    {
        public int CommandCount;
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) { CommandCount++; return true; }
    }
}

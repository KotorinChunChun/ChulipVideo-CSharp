using System.Reflection;
using ChulipVideo.Core;
using ChulipVideo.UI;

namespace ChulipVideo.Tests;

public static class CanvasTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunOnSta(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("キャンバス・設定画面の部品検証に失敗しました。", failure);
        Console.WriteLine("キャンバス・設定画面の部品検証: 成功 (座標/移動/リサイズ/ズーム/境界/所有権/設定分離)");
    }

    private static void RunOnSta()
    {
        using var canvas = new VideoCanvas { Size = new Size(672, 392), VideoSize = new Size(1280, 720), Crop = new CropRect(200, 100, 400, 200) };
        int changed = 0, committed = 0;
        canvas.CropChanged += (_, _) => changed++;
        canvas.CropCommitted += (_, _) => committed++;
        Mouse(canvas, "OnMouseDown", MouseButtons.Left, 216, 116);
        Mouse(canvas, "OnMouseMove", MouseButtons.Left, 236, 126);
        Equal(new CropRect(240, 120, 400, 200), canvas.Crop, "縮小表示での移動を元動画pxへ変換");
        Mouse(canvas, "OnMouseUp", MouseButtons.Left, 236, 126);
        Mouse(canvas, "OnMouseUp", MouseButtons.Left, 236, 126);
        Equal(1, committed, "1ドラッグにつき確定通知は1回");
        True(changed > 0, "ドラッグ中の変更通知");

        canvas.Crop = new CropRect(200, 100, 400, 200);
        Mouse(canvas, "OnMouseDown", MouseButtons.Left, 316, 166);
        Mouse(canvas, "OnMouseMove", MouseButtons.Left, 336, 186);
        Mouse(canvas, "OnMouseUp", MouseButtons.Left, 336, 186);
        Equal(new CropRect(200, 100, 440, 240), canvas.Crop, "右下ハンドルのリサイズ");
        Equal(2, committed, "リサイズの確定通知");

        canvas.Crop = new CropRect(200, 100, 400, 200);
        Mouse(canvas, "OnMouseDown", MouseButtons.Left, 116, 66);
        Mouse(canvas, "OnMouseMove", MouseButtons.Left, -100, -100);
        Mouse(canvas, "OnMouseUp", MouseButtons.Left, -100, -100);
        Equal(new CropRect(0, 0, 600, 300), canvas.Crop, "左上リサイズで反対側の辺を固定して境界へ制約");

        canvas.Crop = new CropRect(200, 100, 400, 200);
        Mouse(canvas, "OnMouseDown", MouseButtons.Left, 316, 166);
        var ratio = (CropRect)Call(canvas, "ResizeCrop", 100, 20, true, false)!;
        Equal(new CropRect(200, 100, 500, 250), ratio, "比率固定リサイズ");
        var center = (CropRect)Call(canvas, "ResizeCrop", 100, 20, false, true)!;
        Equal(new CropRect(100, 80, 600, 240), center, "中心固定リサイズ");
        var ratioCenter = (CropRect)Call(canvas, "ResizeCrop", 100, 20, true, true)!;
        Equal(new CropRect(100, 50, 600, 300), ratioCenter, "比率と中心を同時固定");
        Mouse(canvas, "OnMouseUp", MouseButtons.Left, 316, 166);

        int beforeCancel = committed;
        var before = canvas.Crop;
        Mouse(canvas, "OnMouseDown", MouseButtons.Right, 400, 220);
        Mouse(canvas, "OnMouseMove", MouseButtons.Right, 430, 240);
        True(canvas.Crop != before, "右ドラッグで枠外から移動");
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.Escape));
        Equal(before, canvas.Crop, "Escapeでドラッグ開始前へ復元");
        Equal(beforeCancel, committed, "取消時は確定通知なし");

        canvas.MoveLocked = true;
        Mouse(canvas, "OnMouseDown", MouseButtons.Right, 400, 220);
        Mouse(canvas, "OnMouseMove", MouseButtons.Right, 430, 240);
        Mouse(canvas, "OnMouseUp", MouseButtons.Right, 430, 240);
        Equal(before, canvas.Crop, "移動ロック");
        canvas.MoveLocked = false;

        Mouse(canvas, "OnMouseDown", MouseButtons.Left, 416, 216);
        Mouse(canvas, "OnMouseMove", MouseButtons.Left, 466, 266);
        Mouse(canvas, "OnMouseUp", MouseButtons.Left, 466, 266);
        Equal(new CropRect(800, 400, 100, 100), canvas.Crop, "枠外の背景ドラッグで新しい選択範囲を作成");
        canvas.SizeLocked = true;
        canvas.MoveLocked = true;
        Mouse(canvas, "OnMouseDown", MouseButtons.Left, 466, 266);
        Mouse(canvas, "OnMouseMove", MouseButtons.Left, 486, 286);
        Mouse(canvas, "OnMouseUp", MouseButtons.Left, 486, 286);
        Equal(new CropRect(800, 400, 100, 100), canvas.Crop, "サイズ・移動ロック時の枠を保持");
        canvas.SizeLocked = false;
        canvas.MoveLocked = false;

        var anchor = new Point(160, 90);
        var videoBefore = (PointF)Call(canvas, "ToVideo", anchor)!;
        Mouse(canvas, "OnMouseWheel", MouseButtons.None, anchor.X, anchor.Y, 120);
        var videoAfter = (PointF)Call(canvas, "ToVideo", anchor)!;
        Near(videoBefore.X, videoAfter.X, "ホイールズームの指位置X固定");
        Near(videoBefore.Y, videoAfter.Y, "ホイールズームの指位置Y固定");
        Near(1.2, canvas.Zoom, "ズーム倍率");
        Mouse(canvas, "OnMouseDown", MouseButtons.Middle, 300, 180);
        Mouse(canvas, "OnMouseMove", MouseButtons.Middle, 330, 200);
        Mouse(canvas, "OnMouseUp", MouseButtons.Middle, 330, 200);
        var panned = (PointF)Call(canvas, "ToVideo", anchor)!;
        True(Math.Abs(panned.X - videoAfter.X) > 1, "中ボタンでパン");
        Mouse(canvas, "OnMouseDoubleClick", MouseButtons.Middle, 330, 200);
        Near(1, canvas.Zoom, "中ダブルクリックで全体表示");
        var restored = (PointF)Call(canvas, "ToVideo", anchor)!;
        Near(videoBefore.X, restored.X, "パンとズームの初期化");

        canvas.AllowOutside = true;
        canvas.Crop = new CropRect(-50, -30, 1500, 900);
        Equal(new CropRect(-50, -30, 1500, 900), canvas.Crop, "枠外選択");
        canvas.AllowOutside = false;
        Equal(new CropRect(0, 0, 1280, 720), canvas.Crop, "枠外許可解除で共通Clampを適用");

        var first = new Bitmap(32, 18);
        var second = new Bitmap(64, 36);
        canvas.Frame = first;
        canvas.Frame = second;
        IsDisposed(first, "画像置換時に前画像を解放");
        var positionAfterSmallFrame = (PointF)Call(canvas, "ToVideo", anchor)!;
        Near(videoBefore.X, positionAfterSmallFrame.X, "表示用bitmapの解像度が元動画座標へ影響しない");
        canvas.Frame = second;
        Equal(64, second.Width, "同じ画像の再設定は解放しない");
        canvas.Frame = null;
        IsDisposed(second, "画像のクリア時に解放");

        using (var painted = new VideoCanvas { Size = new Size(672, 392), VideoSize = new Size(640, 360), AllowOutside = true, SizeLocked = true, Crop = new CropRect(-12, -12, 100, 100) })
        {
            painted.Frame = new Bitmap(64, 36);
            using (var imageGraphics = Graphics.FromImage(painted.Frame)) imageGraphics.Clear(Color.Lime);
            using var bitmap = new Bitmap(painted.Width, painted.Height);
            using var graphics = Graphics.FromImage(bitmap);
            Call(painted, "OnPaint", new PaintEventArgs(graphics, painted.ClientRectangle));
            Equal(Color.Black.ToArgb(), bitmap.GetPixel(10, 50).ToArgb(), "動画外の選択部分は黒埋め");
            Equal(Color.Lime.ToArgb(), bitmap.GetPixel(40, 50).ToArgb(), "縮小bitmapを元動画領域へ正しく描画");
        }

        SettingsCloneAndLayout();
        MainFormLayout();
    }

    private static void SettingsCloneAndLayout()
    {
        var original = new AppSettings();
        using var form = new SettingsForm(original);
        form.Settings.PngCompression = 8;
        form.Settings.Overlay.FontSize = 44;
        form.Settings.Presets.Add(new ResolutionPreset("試験", 10, 20));
        form.DialogResult = DialogResult.Cancel;
        Equal(3, original.PngCompression, "キャンセル時に出力設定を変更しない");
        Equal(24, original.Overlay.FontSize, "Overlayを深いコピーで編集");
        Equal(7, original.Presets.Count, "プリセット一覧を深いコピーで編集");
        form.Size = form.MinimumSize;
        Layout(form);
        foreach (var control in new[] { (Control)form.AcceptButton!, (Control)form.CancelButton! })
        {
            var rect = control.Bounds;
            for (var parent = control.Parent; parent is not null && parent != form; parent = parent.Parent)
                rect.Offset(parent.Location);
            True(rect.Bottom <= form.ClientSize.Height && rect.Right <= form.ClientSize.Width && rect.Left >= 0 && rect.Top >= 0,
                "最小ウィンドウでもOK/キャンセルが画面内");
        }
    }

    private static void Layout(Control control)
    {
        control.PerformLayout();
        foreach (Control child in control.Controls) Layout(child);
    }

    private static void MainFormLayout()
    {
        var failures = new List<string>();
        foreach (var size in new[] { new Size(800, 650), new Size(1000, 850) })
        {
            var settings = new AppSettings { WindowWidth = size.Width, WindowHeight = size.Height, LastVideoPath = "" };
            using var form = new MainForm(new SettingsStore(Path.Combine(Path.GetTempPath(), "ChulipVideo-layout-not-written.json")), settings, null);
            try
            {
                // 非表示のFormでは通常のCreateControlは子のHWNDを作らない。
                // ignoreVisibleでHWNDだけを初期化し、TabPageを実寸へレイアウトする。
                // Show/表示状態の変更/メッセージループは使用しない。
                typeof(Control).GetMethod("CreateControl", Hidden, null, [typeof(bool)], null)!.Invoke(form, [true]);
                Layout(form);
                var tabs = Descendants(form).OfType<TabControl>().Single();
                for (int tab = 0; tab < tabs.TabCount; tab++)
                {
                    tabs.SelectedIndex = tab;
                    Layout(form);
                    int checkedCount = 0;
                    foreach (var control in Descendants(form).Where(IsOperationControl))
                    {
                        if (!IsInSelectedTab(control)) continue;
                        checkedCount++;
                        string name = control.AccessibleName ?? control.Text;
                        if (string.IsNullOrWhiteSpace(name)) name = control.GetType().Name;
                        string prefix = $"{size.Width}x{size.Height} / {tabs.SelectedTab!.Text} / {name}";
                        if (control.Width < 1 || control.Height < 1) failures.Add($"{prefix}: 操作部品のサイズが0です。");
                        var rect = control.Bounds;
                        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
                        {
                            if (!parent.ClientRectangle.Contains(rect))
                                failures.Add($"{prefix}: {control.GetType().Name} bounds={rect} が祖先 {parent.GetType().Name} client={parent.ClientRectangle} からはみ出します。");
                            rect.Offset(parent.Location);
                        }
                        if (control is Button button)
                        {
                            var preferred = button.GetPreferredSize(Size.Empty);
                            if (button.Width < preferred.Width || button.Height < preferred.Height)
                                failures.Add($"{prefix}: ボタン実寸 {button.Size} < 推奨寸法 {preferred}");
                        }
                        if (control is NumericUpDown number && number.Height < number.PreferredHeight)
                            failures.Add($"{prefix}: 数値欄の高さ {number.Height} < {number.PreferredHeight}");
                        if (control is Timeline && control.Height < 37)
                            failures.Add($"{prefix}: 下端ハンドルの描画に必要な高さ37pxに対して実寸{control.Height}px");
                    }
                    True(checkedCount > 20, "現在選択タブと共通操作を配置試験の対象に含む");
                    Console.WriteLine($"主画面配置検証: {size.Width}x{size.Height} / {tabs.SelectedTab!.Text} / 操作部品 {checkedCount} 個 / canvas={Descendants(form).OfType<VideoCanvas>().Single().Size}");
                }
                // 設定ダイアログ適用後は新しい設定インスタンスを各操作が更新する。
                // ダイアログ表示をせず、OpenSettingsで行うインスタンス置換だけ再現する。
                var replacement = new AppSettings();
                typeof(MainForm).GetField("settings", Hidden)!.SetValue(form, replacement);
                ((NumericUpDown)typeof(MainForm).GetField("speed", Hidden)!.GetValue(form)!).Value = 2;
                ((CheckBox)typeof(MainForm).GetField("range", Hidden)!.GetValue(form)!).Checked = true;
                ((CheckBox)typeof(MainForm).GetField("keys", Hidden)!.GetValue(form)!).Checked = false;
                Equal(2d, replacement.Speed, "詳細設定適用後も再生速度を現設定へ反映");
                True(replacement.RangeOnly, "詳細設定適用後も区間再生を現設定へ反映");
                True(!replacement.Overlay.ShowKeys, "詳細設定適用後もキー字幕表示を現設定へ反映");
            }
            finally
            {
                // Show/Close は呼ばず、設定保存やメディア読込を起動しない。
                // フォーム終了イベントで解放する補助部品も、この非表示試験では直接解放する。
                foreach (string field in new[] { "timer", "tips", "lifetime", "mediaLifetime" })
                    (typeof(MainForm).GetField(field, Hidden)?.GetValue(form) as IDisposable)?.Dispose();
            }
        }
        if (failures.Count > 0) throw new Exception("主画面の配置に問題があります:\n" + string.Join("\n", failures.Distinct()));
    }

    private static bool IsOperationControl(Control control) => control is ButtonBase or NumericUpDown or ComboBox or Timeline;

    private static bool IsInSelectedTab(Control control)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
            if (parent is TabPage page && page.Parent is TabControl tabs && tabs.SelectedTab != page) return false;
        return true;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void Mouse(VideoCanvas canvas, string method, MouseButtons button, int x, int y, int delta = 0) =>
        Call(canvas, method, new MouseEventArgs(button, method == "OnMouseDoubleClick" ? 2 : 1, x, y, delta));

    private static object? Call(VideoCanvas canvas, string method, params object[] args) =>
        typeof(VideoCanvas).GetMethod(method, Hidden)!.Invoke(canvas, args);

    private static void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{name}: expected={expected}, actual={actual}");
    }

    private static void Near(double expected, double actual, string name)
    {
        if (Math.Abs(expected - actual) > 0.001) throw new Exception($"{name}: expected={expected}, actual={actual}");
    }

    private static void True(bool condition, string name) { if (!condition) throw new Exception(name); }

    private static void IsDisposed(Bitmap bitmap, string name)
    {
        try { _ = bitmap.Width; }
        catch (ArgumentException) { return; }
        catch (ObjectDisposedException) { return; }
        throw new Exception(name);
    }
}

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
        var ratio = (CropRect)Call(canvas, "ResizeCrop", 100, 20, true, false, canvas.AllowOutside)!;
        Equal(new CropRect(200, 100, 500, 250), ratio, "比率固定リサイズ");
        var center = (CropRect)Call(canvas, "ResizeCrop", 100, 20, false, true, canvas.AllowOutside)!;
        Equal(new CropRect(100, 80, 600, 240), center, "中心固定リサイズ");
        var ratioCenter = (CropRect)Call(canvas, "ResizeCrop", 100, 20, true, true, canvas.AllowOutside)!;
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
        foreach (var size in new[] { new Size(800, 650), new Size(1000, 850), new Size(1200, 850) })
        {
            var settings = new AppSettings { WindowWidth = size.Width, WindowHeight = size.Height, LastVideoPath = "" };
            using var form = new MainForm(new SettingsStore(Path.Combine(Path.GetTempPath(), "ChulipVideo-layout-not-written.json")), settings, null);
            try
            {
                // Showや設定保存をせず、非表示のHWNDとレイアウトだけ初期化する。
                typeof(Control).GetMethod("CreateControl", Hidden, null, [typeof(bool)], null)!.Invoke(form, [true]);
                Layout(form);
                True(!Descendants(form).OfType<TabControl>().Any(), "主画面の操作をタブに隠さない");
                int checkedCount = 0;
                foreach (var control in Descendants(form).Where(c => IsOperationControl(c) || c is TextBox))
                {
                    if (control.Parent is NumericUpDown) continue;
                    checkedCount++;
                    string name = control.AccessibleName ?? control.Text;
                    if (string.IsNullOrWhiteSpace(name)) name = control.GetType().Name;
                    string prefix = $"{size.Width}x{size.Height} / {name}";
                    if (control.Width < 1 || control.Height < 1) failures.Add($"{prefix}: 操作部品のサイズが0です。");
                    var rect = control.Bounds;
                    for (var parent = control.Parent; parent is not null; parent = parent.Parent)
                    {
                        if (!parent.ClientRectangle.Contains(rect))
                            failures.Add($"{prefix}: {control.GetType().Name} bounds={rect} が祖先 {parent.Name} ({parent.GetType().Name}) client={parent.ClientRectangle} からはみ出します。");
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
                True(checkedCount >= 45, "再生・編集・演出・すべての出力操作を同時に配置試験する");
                var main = (TableLayoutPanel)Descendants(form).Single(c => c.Name == "mainLayout");
                string[] order = ["sourceToolbar", "videoPreview", "transportPanel", "trimPanel", "cropPanel", "exportPanel"];
                for (int i = 0; i < order.Length; i++) Equal(order[i], main.GetControlFromPosition(0, i)!.Name, "原型と同じ主画面の6領域順");
                var timeColumns = (TableLayoutPanel)Descendants(form).Single(c => c.Name == "timeColumns");
                Equal(3, timeColumns.ColumnCount, "開始・現在・終了の3列");
                string[] timeNames = ["startColumn", "currentColumn", "endColumn"];
                for (int i = 0; i < timeNames.Length; i++)
                {
                    var column = timeColumns.GetControlFromPosition(i, 0)!;
                    Equal(timeNames[i], column.Name, "時刻欄の順序");
                    Equal(1, Descendants(column).OfType<TimecodeUpDown>().Count(), "各列に時分秒入力欄を配置");
                    True(Descendants(column).OfType<Button>().Any(b => b.Text == "−1s") && Descendants(column).OfType<Button>().Any(b => b.Text == "+1s"), "各時刻に1秒増減を配置");
                }
                var sections = (TableLayoutPanel)Descendants(form).Single(c => c.Name == "exportSections");
                Equal(3, sections.ColumnCount, "出力は操作の軌跡・静止画・動画の3区画");
                True(MainField<TextBox>(form, "sourcePath").Width >= 80, "狭い画面でも動画パス領域を確保");
                SaveHiddenLayoutEvidence(form, size);
                MainField<TextBox>(form, "sourcePath").Text = @"C:\非常に長いフォルダー名\操作動画の保存場所\長い日本語ファイル名の動画.mp4";
                Layout(form);
                True(MainField<TextBox>(form, "sourcePath").ReadOnly, "動画パスは表示専用");
                True(MainField<VideoCanvas>(form, "canvas").Height >= 100, "最小画面でもプレビュー領域を確保");
                Console.WriteLine($"主画面配置検証: {size.Width}x{size.Height} / 全操作 {checkedCount} 個 / canvas={MainField<VideoCanvas>(form, "canvas").Size}");

                // 詳細設定適用時の代入でイベントが既存値を上書きしない。
                var replacement = new AppSettings { PngCompression = 5, Overlay = new OverlayOptions { ShowPointer = false, ShowClicks = true } };
                typeof(MainForm).GetField("settings", Hidden)!.SetValue(form, replacement);
                InvokeMain(form, "ApplySettings");
                True(!replacement.Overlay.ShowPointer && replacement.Overlay.ShowClicks, "設定同期でポインターとクリックの個別状態を保持");
                Equal(5m, MainField<NumericUpDown>(form, "pngCompression").Value, "詳細設定のPNG圧縮を主画面へ反映");
                MainField<NumericUpDown>(form, "speed").Value = 2;
                MainField<CheckBox>(form, "range").Checked = true;
                MainField<CheckBox>(form, "keys").Checked = false;
                MainField<NumericUpDown>(form, "pngCompression").Value = 7;
                Equal(2d, replacement.Speed, "詳細設定適用後も再生速度を現設定へ反映");
                True(replacement.RangeOnly, "詳細設定適用後も区間再生を現設定へ反映");
                True(!replacement.Overlay.ShowKeys, "詳細設定適用後もキー字幕表示を現設定へ反映");
                Equal(7, replacement.PngCompression, "常設PNG圧縮を現設定へ反映");

                var canvas = MainField<VideoCanvas>(form, "canvas");
                canvas.Crop = new CropRect(10, 20, 300, 200);
                var cropBefore = canvas.Crop;
                var ratio = MainField<ComboBox>(form, "presetRatio");
                ratio.SelectedIndex = 1;
                var filtered = MainField<ComboBox>(form, "presets").Items.OfType<ResolutionPreset>().ToArray();
                True(filtered.Length > 0 && filtered.All(p => Math.Abs(p.Width / (double)p.Height - 16d / 9) < .01), "16:9に合うプリセットのみを表示");
                Equal(cropBefore, canvas.Crop, "比率の絞り込み自体はクロップを変更・固定しない");
                ratio.SelectedIndex = 0;
                Equal(replacement.Presets.Count, MainField<ComboBox>(form, "presets").Items.OfType<ResolutionPreset>().Count(), "未指定で全プリセットへ復帰");
                MainField<CheckBox>(form, "sizeLock").Checked = true;
                True(!MainField<ComboBox>(form, "presets").Enabled && !MainField<NumericUpDown>(form, "cropW").Enabled, "寸法固定で幅とプリセットの変更を禁止");
                MainField<CheckBox>(form, "sizeLock").Checked = false;
                InvokeMain(form, "SetBusy", true);
                True(!MainField<NumericUpDown>(form, "pngCompression").Enabled && MainField<Button>(form, "cancel").Parent!.Enabled, "出力中は設定変更を止め、中断行は操作可能なまま");
                InvokeMain(form, "SetBusy", false);
                MainInteractionRegression(form);
            }
            finally
            {
                foreach (string field in new[] { "timer", "tips", "lifetime", "mediaLifetime" })
                    (typeof(MainForm).GetField(field, Hidden)?.GetValue(form) as IDisposable)?.Dispose();
            }
        }
        if (failures.Count > 0) throw new Exception("主画面の配置に問題があります:\n" + string.Join("\n", failures.Distinct()));
    }

    private static T MainField<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, Hidden)!.GetValue(form)!;
    private static void SetMainField(MainForm form, string name, object value) => typeof(MainForm).GetField(name, Hidden)!.SetValue(form, value);
    private static object? InvokeMain(MainForm form, string name, params object[] args) => typeof(MainForm).GetMethod(name, Hidden)!.Invoke(form, args);
    private static bool IsOperationControl(Control control) => control is ButtonBase or NumericUpDown or ComboBox or Timeline;

    private static void SaveHiddenLayoutEvidence(MainForm form, Size size)
    {
        string? folder = Environment.GetEnvironmentVariable("CHULIPVIDEO_LAYOUT_EVIDENCE");
        if (string.IsNullOrWhiteSpace(folder) || size.Width is not (800 or 1200)) return;
        True(!form.Visible, "描画証跡を作るためにフォームを表示しない");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"main-hidden-draw-{size.Width}x{size.Height}.png");
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        True(!form.Visible, "描画後もフォームは非表示のまま");
        Console.WriteLine($"非表示フォームのDrawToBitmap描画証跡（実デスクトップ画像ではありません）: {path}");
    }

    private static void MainInteractionRegression(MainForm form)
    {
        // メタデータのみを設定し、実ファイル・FFmpeg・デスクトップ操作は使用しない。
        SetMainField(form, "current", new MediaInfo("layout-fixture.mp4", 1280, 720, 30, 30));
        var canvas = MainField<VideoCanvas>(form, "canvas");
        canvas.VideoSize = new Size(1280, 720);
        canvas.Crop = new CropRect(10, 20, 300, 200);
        var presets = MainField<ComboBox>(form, "presets");
        presets.SelectedItem = presets.Items.OfType<ResolutionPreset>().Single(p => p.Name == "VGA");
        typeof(ComboBox).GetMethod("OnSelectionChangeCommitted", Hidden)!.Invoke(presets, [EventArgs.Empty]);
        Equal(new CropRect(10, 20, 640, 480), canvas.Crop, "プリセットの選択確定で座標を保って幅・高さを適用");
        MainField<CheckBox>(form, "sizeLock").Checked = true;
        presets.SelectedItem = presets.Items.OfType<ResolutionPreset>().Single(p => p.Name == "HD");
        typeof(ComboBox).GetMethod("OnSelectionChangeCommitted", Hidden)!.Invoke(presets, [EventArgs.Empty]);
        Equal(new CropRect(10, 20, 640, 480), canvas.Crop, "寸法固定中はプリセットイベントでも枠を変更しない");
        MainField<CheckBox>(form, "sizeLock").Checked = false;

        SetMainField(form, "current", new MediaInfo("layout-small-fixture.mp4", 640, 480, 30, 30));
        canvas.VideoSize = new Size(640, 480);
        var chosenPreset = presets.Items.OfType<ResolutionPreset>().Single(p => p.Name == "FHD");
        presets.SelectedItem = chosenPreset;
        typeof(ComboBox).GetMethod("OnSelectionChangeCommitted", Hidden)!.Invoke(presets, [EventArgs.Empty]);
        Equal(new CropRect(0, 0, 640, 480), canvas.Crop, "大きいプリセットを動画内へ制限");
        True(ReferenceEquals(chosenPreset, presets.SelectedItem), "寸法制限後も選んだFHDを保持して削除対象を取り違えない");
        True(MainField<Label>(form, "status").Text.Contains("640×480"), "寸法制限時は実寸を通知");
        var duplicateSize = new ResolutionPreset("同寸法別名", 640, 480);
        MainField<AppSettings>(form, "settings").Presets.Add(duplicateSize);
        InvokeMain(form, "RefreshPresets");
        presets.SelectedItem = duplicateSize;
        typeof(ComboBox).GetMethod("OnSelectionChangeCommitted", Hidden)!.Invoke(presets, [EventArgs.Empty]);
        True(ReferenceEquals(duplicateSize, presets.SelectedItem), "同寸法のプリセットでもユーザーが選んだ名前を保持");
        InvokeMain(form, "SyncCrop");
        True(ReferenceEquals(duplicateSize, presets.SelectedItem), "座標同期でも同寸法の別名プリセットへ変更しない");

        InvokeMain(form, "SetRange", 0d, 30d);
        SetMainField(form, "currentTime", 12d);
        form.ActiveControl = canvas;
        True(SendMainKey(form, Keys.I), "Iショートカットを受理");
        Equal(12d, MainField<double>(form, "rangeStart"), "Iで現在位置を開始に設定");
        SetMainField(form, "currentTime", 20d);
        foreach (var input in new Control[] { MainField<TimecodeUpDown>(form, "start"), presets, MainField<TextBox>(form, "sourcePath") })
        {
            form.ActiveControl = input;
            SendMainKey(form, Keys.O);
            Equal(30d, MainField<double>(form, "rangeEnd"), "文字・数値・候補入力中はOを区間操作へ使わない");
        }
        form.ActiveControl = canvas;
        True(SendMainKey(form, Keys.O), "Oショートカットを受理");
        Equal(20d, MainField<double>(form, "rangeEnd"), "Oで現在位置を終了に設定");

        foreach (string field in new[] { "start", "position", "end" })
        {
            InvokeMain(form, "SetRange", 0d, 30d);
            SetMainField(form, "currentTime", 12d);
            InvokeMain(form, "SyncTimes");
            form.ActiveControl = canvas;
            InvokeMain(form, "TogglePlay");
            True(MainField<bool>(form, "playing"), "入力前は再生中");
            var editor = MainField<TimecodeUpDown>(form, field);
            typeof(Control).GetMethod("OnEnter", Hidden)!.Invoke(editor, [EventArgs.Empty]);
            True(!MainField<bool>(form, "playing"), $"{field}への入力開始で再生停止");
            editor.Controls.OfType<TextBox>().Single().Text = "00:00:07.";
            InvokeMain(form, "PlaybackTick");
            InvokeMain(form, "PlaybackTick");
            Equal("00:00:07.", editor.Text, $"{field}の未確定入力を再生タイマーが巻き戻さない");
            editor.CommitEdit();
            Equal(7m, editor.Value, $"{field}の入力を停止状態で確定できる");
        }
    }

    private static bool SendMainKey(MainForm form, Keys key)
    {
        object[] args = [Message.Create(IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero), key];
        return (bool)typeof(MainForm).GetMethod("ProcessCmdKey", Hidden)!.Invoke(form, args)!;
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

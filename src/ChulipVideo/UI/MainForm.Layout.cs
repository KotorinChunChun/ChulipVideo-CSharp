using ChulipVideo.Core;

namespace ChulipVideo.UI;

public sealed partial class MainForm
{
    private static readonly Color StartTone = Color.FromArgb(185, 239, 200);
    private static readonly Color PlayTone = Color.FromArgb(174, 226, 244);
    private static readonly Color EndTone = Color.FromArgb(255, 159, 166);
    private static readonly Color SaveTone = Color.FromArgb(141, 195, 237);
    private static readonly Color DetailTone = Color.FromArgb(255, 218, 105);
    private static readonly Color PngTone = Color.FromArgb(242, 150, 191);
    private static readonly Color GifTone = Color.FromArgb(255, 208, 126);

    private static TimecodeUpDown Timecode(string label) => new()
    {
        Minimum = 0, Maximum = 864000, DecimalPlaces = 3, Increment = 1,
        Width = 140, TextAlign = HorizontalAlignment.Center, AccessibleName = label
    };

    private void BuildLayout()
    {
        BackColor = Color.FromArgb(242, 242, 242);
        var layout = Grid("mainLayout", 1, 44, -1, 36, 140, 90, 154, 22);
        layout.Padding = new Padding(6);
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(BuildToolbar(), 0, 0);
        canvas.Name = "videoPreview";
        canvas.Margin = new Padding(2, 0, 2, 0);
        layout.Controls.Add(canvas, 0, 1);
        layout.Controls.Add(BuildTransport(), 0, 2);
        layout.Controls.Add(BuildTrim(), 0, 3);
        layout.Controls.Add(BuildCrop(), 0, 4);
        layout.Controls.Add(BuildExport(), 0, 5);
        status.Name = "mainStatus";
        status.Margin = new Padding(4, 0, 4, 0);
        layout.Controls.Add(status, 0, 6);
        busyControls.Add(canvas);
        Controls.Add(layout);
        tips.SetToolTip(canvas, "左ドラッグ: 選択/移動　枠: サイズ変更　中ドラッグ: パン　ホイール: 拡大\nShift: 比率/軸固定　Ctrl: 中心固定　Alt: 10px刻み");
    }

    private Control BuildToolbar()
    {
        var toolbar = Grid("sourceToolbar", 4, -1);
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        toolbar.Controls.Add(ActionButton("動画選択", ChooseVideo, null, "動画を選択 (Ctrl+O)", true), 0, 0);
        toolbar.Controls.Add(sourcePath, 1, 0);
        var actions = new FlowLayoutPanel
        {
            Name = "toolbarActions", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 3, 0, 0)
        };
        foreach (var button in new[]
        {
            ActionButton("場所", OpenFolder, null, "選択動画のフォルダーを開く", true),
            ActionButton("TSV", OpenInputLog, null, "対応する入力履歴TSVを開く", true),
            ActionButton("手順", OpenStepEditor, PngTone, "入力ログから手順書の手順を抽出・編集", true),
            ActionButton("設定", OpenSettings, null, "全般・詳細設定", true),
            ActionButton("再読込", () => RunUiAsync(() => LoadAsync(current?.Path)), DetailTone, "動画・設定・TSVを読み直す", true),
            ActionButton("保存", SaveVideo, SaveTone, "動画別設定を保存", true),
            ActionButton("?", ShowHelp, DetailTone, "使い方とショートカット")
        })
        {
            button.Dock = DockStyle.None;
            button.MinimumSize = new Size(button.Text == "?" ? 28 : 42, 30);
            actions.Controls.Add(button);
        }
        toolbar.Controls.Add(actions, 2, 0);
        info.Name = "mediaInfo";
        toolbar.Controls.Add(info, 3, 0);
        return toolbar;
    }

    private Control BuildTransport()
    {
        var host = Grid("transportPanel", 1, -1);
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var row = new FlowLayoutPanel
        {
            Name = "transportControls", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.None, WrapContents = false, Margin = Padding.Empty
        };
        row.Controls.Add(ActionButton("|◀ 先頭", () => Seek(0), null, "動画の先頭 (Ctrl+Home)"));
        row.Controls.Add(ActionButton("◀ 開始", () => Seek(rangeStart), StartTone, "開始位置へ移動 (Home)"));
        play.Click += (_, _) => TogglePlay();
        play.BackColor = PlayTone;
        play.UseVisualStyleBackColor = false;
        play.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        play.MinimumSize = new Size(76, 28);
        play.Margin = new Padding(2, 1, 2, 1);
        row.Controls.Add(play);
        row.Controls.Add(ActionButton("終了 ▶", () => Seek(rangeEnd), EndTone, "終了位置へ移動 (End)"));
        row.Controls.Add(ActionButton("末尾 ▶|", () => Seek(current?.LastFrameTime ?? 0), null, "動画の末尾 (Ctrl+End)"));
        foreach (var check in new[] { range, loop, pingpong })
        {
            check.Margin = new Padding(4, 5, 0, 0);
            row.Controls.Add(check);
        }
        row.Controls.Add(new Label { Text = "速度", AutoSize = true, Margin = new Padding(8, 6, 2, 0) });
        speed.Width = 62;
        speed.Margin = new Padding(0, 3, 0, 0);
        row.Controls.Add(speed);
        foreach (Control control in row.Controls)
            if (control is Button) control.Dock = DockStyle.None;
        host.Controls.Add(row, 0, 0);
        busyControls.Add(host);
        tips.SetToolTip(range, "開始から終了までを再生");
        tips.SetToolTip(pingpong, "ループ再生時、区間の端で再生方向を反転");
        return host;
    }

    private Control BuildTrim()
    {
        var group = Group("trimPanel", "トリミング範囲");
        var contents = Grid("trimContents", 1, 40, -1);
        contents.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        timeline.Name = "trimTimeline";
        contents.Controls.Add(timeline, 0, 0);
        var times = Grid("timeColumns", 3, -1);
        for (int i = 0; i < 3; i++) times.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        times.Controls.Add(TimeColumn("startColumn", "▼ Start（開始）", start, StartTone,
            d => SetRange(rangeStart + d, rangeEnd), "現在位置を開始に (I)", () => SetRange(currentTime, rangeEnd)), 0, 0);
        times.Controls.Add(TimeColumn("currentColumn", "● Current（現在）", position, null,
            d => Seek(currentTime + d), null, null), 1, 0);
        times.Controls.Add(TimeColumn("endColumn", "▲ End（終了）", end, EndTone,
            d => SetRange(rangeStart, rangeEnd + d), "現在位置を終了に (O)", () => SetRange(rangeStart, currentTime)), 2, 0);
        contents.Controls.Add(times, 0, 1);
        group.Controls.Add(contents);
        busyControls.Add(group);
        return group;
    }

    private Control TimeColumn(string name, string title, TimecodeUpDown editor, Color? color,
        Action<double> step, string? applyText, Action? apply)
    {
        var column = Grid(name, 1, 17, 30, -1);
        column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        column.Padding = new Padding(4, 0, 4, 0);
        column.Controls.Add(new Label
        {
            Text = title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Margin = Padding.Empty,
            ForeColor = name == "startColumn" ? Color.ForestGreen : name == "endColumn" ? Color.Firebrick : SystemColors.ControlText
        }, 0, 0);
        var input = Grid(name + "Input", 3, -1);
        input.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        input.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        input.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        input.Controls.Add(ActionButton("−1s", () => step(-1), color, title + "を1秒戻す"), 0, 0);
        editor.Dock = DockStyle.Fill;
        editor.Margin = new Padding(1, 2, 1, 0);
        input.Controls.Add(editor, 1, 0);
        input.Controls.Add(ActionButton("+1s", () => step(1), color, title + "を1秒進める"), 2, 0);
        column.Controls.Add(input, 0, 1);
        if (apply is not null && applyText is not null)
            column.Controls.Add(ActionButton(applyText, apply, color), 0, 2);
        else column.Controls.Add(new Label
        {
            Text = "時刻または秒数を直接入力", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter, Margin = Padding.Empty, ForeColor = SystemColors.GrayText
        }, 0, 2);
        return column;
    }

    private Control BuildCrop()
    {
        var group = Group("cropPanel", "クロップ範囲");
        var row = Grid("cropControls", 5, -1);
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 152));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        var historyControls = Grid("cropHistory", 2, -1, -1);
        historyControls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        historyControls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        historyControls.Controls.Add(ActionButton("↶", Undo, PlayTone, "元に戻す (Ctrl+Z)"), 0, 0);
        historyControls.Controls.Add(ActionButton("↷", Redo, PlayTone, "やり直し (Ctrl+Y)"), 1, 0);
        var reset = ActionButton("表示戻す", canvas.ResetView, PlayTone, "表示リセット (中ボタンをダブルクリック)");
        historyControls.Controls.Add(reset, 0, 1);
        historyControls.SetColumnSpan(reset, 2);
        row.Controls.Add(historyControls, 0, 0);
        var selection = Grid("cropSelection", 1, -1, -1);
        selection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outside.Anchor = AnchorStyles.None;
        outside.Margin = Padding.Empty;
        selection.Controls.Add(outside, 0, 0);
        selection.Controls.Add(ActionButton("全体選択", () => SetCrop(current is null ? new(0, 0, 1, 1) : CropRect.Full(current)), null, "動画の全画面を選択"), 0, 1);
        row.Controls.Add(selection, 1, 0);
        var coordinates = Grid("cropCoordinates", 2, -1);
        coordinates.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        coordinates.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        LockButton(moveLock, "座標\n固定", "X/Yの変更とドラッグ移動を固定");
        coordinates.Controls.Add(moveLock, 0, 0);
        coordinates.Controls.Add(NumberPair("X", cropX, "Y", cropY), 1, 0);
        row.Controls.Add(coordinates, 2, 0);
        var dimensions = Grid("cropDimensions", 3, -1);
        dimensions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        dimensions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        dimensions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        LockButton(sizeLock, "寸法\n固定", "幅・高さの変更とリサイズを固定");
        dimensions.Controls.Add(sizeLock, 0, 0);
        var choices = Grid("presetChoices", 1, -1, -1);
        choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        presetRatio.Items.AddRange(["比率: 未指定", "比率: 16:9", "比率: 4:3", "比率: 1:1"]);
        presetRatio.SelectedIndex = 0;
        presetRatio.Margin = new Padding(2, 3, 2, 0);
        presets.Margin = new Padding(2, 3, 2, 0);
        presets.DropDownWidth = 250;
        choices.Controls.Add(presetRatio, 0, 0);
        choices.Controls.Add(presets, 0, 1);
        tips.SetToolTip(presetRatio, "プリセットを比率で絞り込みます。切り出し枠の比率は固定しません。\nドラッグ中の比率固定はShiftキーです。");
        tips.SetToolTip(presets, "選ぶと幅・高さを適用します。枠外許可がOFFのときは動画内へ収めます。");
        dimensions.Controls.Add(choices, 1, 0);
        dimensions.Controls.Add(NumberPair("幅", cropW, "高さ", cropH), 2, 0);
        row.Controls.Add(dimensions, 3, 0);
        var presetActions = Grid("presetActions", 1, -1, -1);
        presetActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        presetActions.Controls.Add(ActionButton("登録", SavePreset, DetailTone, "現在の幅・高さをプリセットへ登録"), 0, 0);
        presetActions.Controls.Add(ActionButton("削除", DeletePreset, PlayTone, "選択したプリセットを削除"), 0, 1);
        row.Controls.Add(presetActions, 4, 0);
        group.Controls.Add(row);
        busyControls.Add(group);
        return group;
    }

    private void LockButton(CheckBox control, string text, string tip)
    {
        control.Text = text;
        control.Appearance = Appearance.Button;
        control.AutoSize = false;
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(3, 2, 3, 2);
        control.TextAlign = ContentAlignment.MiddleCenter;
        control.BackColor = StartTone;
        control.UseVisualStyleBackColor = false;
        control.CheckedChanged += (_, _) => control.BackColor = control.Checked ? DetailTone : StartTone;
        tips.SetToolTip(control, tip);
    }

    private static Control NumberPair(string firstLabel, NumericUpDown first, string secondLabel, NumericUpDown second)
    {
        var pair = Grid("cropNumberPair", 2, -1, -1);
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var numbers = new[] { first, second };
        var labels = new[] { firstLabel, secondLabel };
        for (int i = 0; i < numbers.Length; i++)
        {
            pair.Controls.Add(new Label { Text = labels[i], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(0, 0, 2, 0) }, 0, i);
            numbers[i].Dock = DockStyle.Fill;
            numbers[i].Margin = new Padding(1, 3, 2, 0);
            pair.Controls.Add(numbers[i], 1, i);
        }
        return pair;
    }

    private Control BuildExport()
    {
        var group = Group("exportPanel", "エクスポート");
        var contents = Grid("exportContents", 1, -1, 30);
        contents.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var sections = Grid("exportSections", 3, -1);
        sections.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        sections.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        sections.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        var overlay = Grid("overlayControls", 1, 23, 23, -1);
        overlay.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pointer.Margin = keys.Margin = new Padding(2, 1, 0, 0);
        overlay.Controls.Add(pointer, 0, 0);
        overlay.Controls.Add(keys, 0, 1);
        overlay.Controls.Add(ActionButton("⚙ 詳細設定", OpenSettings, DetailTone, "ポインター・字幕・色・出力の設定"), 0, 2);
        sections.Controls.Add(ExportSection("overlaySection", "操作の軌跡", overlay), 0, 0);

        var still = Grid("stillControls", 3, -1);
        still.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        still.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        still.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        var currentFrame = Grid("currentFrameOutput", 1, -1, -1);
        currentFrame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        currentFrame.Controls.Add(ActionButton("表示をCOPY", () => RunUiAsync(CopyAsync), PlayTone, "表示内容を画像としてコピー (Ctrl+C)"), 0, 0);
        currentFrame.Controls.Add(ActionButton("PNG保存", () => RunUiAsync(SaveImageAsync), PlayTone, "現在の1フレームをPNG保存 (Ctrl+S)"), 0, 1);
        still.Controls.Add(currentFrame, 0, 0);
        var options = Grid("pngOptions", 1, 23, 23, -1);
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stable.Margin = duplicates.Margin = new Padding(2, 1, 0, 0);
        options.Controls.Add(stable, 0, 0);
        options.Controls.Add(duplicates, 0, 1);
        tips.SetToolTip(stable, "前後フレームと同じ静止場面だけをPNG連番へ出力（判定の厳しさは詳細設定）");
        tips.SetToolTip(duplicates, "直前と同じ画像をPNG連番から除外（判定の厳しさは詳細設定）");
        var compression = Grid("pngCompressionRow", 2, -1);
        compression.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 59));
        compression.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        compression.Controls.Add(new Label { Text = "PNG圧縮", Dock = DockStyle.Fill, Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        pngCompression.Dock = DockStyle.Fill;
        pngCompression.Margin = new Padding(0, 2, 2, 0);
        compression.Controls.Add(pngCompression, 1, 0);
        options.Controls.Add(compression, 0, 2);
        still.Controls.Add(options, 1, 0);
        still.Controls.Add(ActionButton("PNG連番\n保存", () => BeginExport(ExportKind.PngSequence), PngTone, "指定区間の画像をPNG連番で保存"), 2, 0);
        sections.Controls.Add(ExportSection("stillSection", "静止画出力", still), 1, 0);

        var video = Grid("videoOutputControls", 1, 23, -1, -1);
        video.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        burn.Margin = new Padding(2, 1, 0, 0);
        video.Controls.Add(burn, 0, 0);
        video.Controls.Add(ActionButton("MP4 動画保存", () => BeginExport(ExportKind.Mp4), StartTone), 0, 1);
        video.Controls.Add(ActionButton("GIF アニメ保存", () => BeginExport(ExportKind.Gif), GifTone), 0, 2);
        sections.Controls.Add(ExportSection("videoSection", "動画出力", video), 2, 0);
        busyControls.AddRange([overlay, still, video]);
        contents.Controls.Add(sections, 0, 0);

        var progressRow = Grid("exportProgress", 3, -1);
        progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        progress.Dock = DockStyle.Fill;
        progress.Margin = new Padding(3, 5, 5, 4);
        progressRow.Controls.Add(progress, 0, 0);
        cancel.Dock = DockStyle.Fill;
        cancel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        cancel.Margin = new Padding(1, 1, 3, 1);
        progressRow.Controls.Add(cancel, 1, 0);
        progressRow.Controls.Add(new Label
        {
            Text = "開始を含み終了を含まない / 動画・GIFは音声なし", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = new Padding(5, 0, 0, 0)
        }, 2, 0);
        contents.Controls.Add(progressRow, 0, 1);
        group.Controls.Add(contents);
        return group;
    }

    private static Control ExportSection(string name, string title, Control content)
    {
        var section = Grid(name, 1, 18, -1);
        section.Margin = new Padding(3, 0, 3, 0);
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        section.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, Margin = new Padding(1, 0, 0, 0) }, 0, 0);
        section.Controls.Add(content, 0, 1);
        return section;
    }

    private static GroupBox Group(string name, string title) => new()
    {
        Name = name, Text = title, Dock = DockStyle.Fill,
        Margin = new Padding(2, 1, 2, 1), Padding = new Padding(5, 1, 5, 3)
    };

    private static TableLayoutPanel Grid(string name, int columns, params float[] rows)
    {
        var grid = new TableLayoutPanel { Name = name, Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows.Length, Margin = Padding.Empty, Padding = Padding.Empty };
        foreach (float height in rows)
            grid.RowStyles.Add(height < 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
        return grid;
    }

    private Button ActionButton(string text, Action action, Color? color = null, string? tip = null, bool busy = false)
    {
        var button = new Button
        {
            Text = text, AccessibleName = tip ?? text.Replace('\n', ' '), Dock = DockStyle.Fill,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 24), Size = new Size(10, 24),
            Margin = new Padding(2, 1, 2, 1), Padding = new Padding(2, 0, 2, 0)
        };
        if (color is Color tone) { button.BackColor = tone; button.UseVisualStyleBackColor = false; }
        button.Click += (_, _) => action();
        if (tip is not null) tips.SetToolTip(button, tip);
        if (busy) busyControls.Add(button);
        return button;
    }
}

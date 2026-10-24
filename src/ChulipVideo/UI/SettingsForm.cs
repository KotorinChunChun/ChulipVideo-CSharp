using System.ComponentModel;
using System.Drawing.Drawing2D;
using ChulipVideo.Core;

namespace ChulipVideo.UI;

public sealed class SettingsForm : BrandedForm
{
    private readonly Panel preview = new() { Dock = DockStyle.Fill, Margin = new Padding(8, 4, 8, 4) };
    private readonly List<(TextBox Box, bool Optional, string Label)> colorInputs = [];
    private TextBox ffmpegPath = null!;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public AppSettings Settings { get; }

    public SettingsForm(AppSettings settings)
    {
        Settings = settings with { Overlay = settings.Overlay with { }, Presets = settings.Presets.Select(x => x with { }).ToList() };
        Text = WindowTitle.Create("詳細設定");
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(540, 420);
        ClientSize = new Size(700, 650);
        MaximizeBox = false;
        ShowInTaskbar = false;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var pointer = AddTab(tabs, "ポインター・クリック");
        var subtitle = AddTab(tabs, "入力字幕");
        var theme = AddTab(tabs, "テーマ");
        var export = AddTab(tabs, "出力・FFmpeg");
        BuildPointer(pointer);
        BuildSubtitle(subtitle);
        BuildTheme(theme);
        BuildExport(export);
        root.Controls.Add(tabs, 0, 0);
        preview.Paint += PaintPreview;
        preview.Resize += (_, _) => preview.Invalidate();
        root.Controls.Add(preview, 0, 1);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Padding = new Padding(0, 5, 0, 0)
        };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Size = new Size(110, 30) };
        var accept = new Button { Text = "保存", Size = new Size(110, 30) };
        accept.Click += (_, _) => AcceptSettings();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(accept);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
        AcceptButton = accept;
        CancelButton = cancel;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var workArea = Screen.FromControl(this).WorkingArea;
        if (Height > workArea.Height) Height = Math.Max(MinimumSize.Height, workArea.Height);
        if (Top < workArea.Top || Bottom > workArea.Bottom) Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
    }

    private static TableLayoutPanel AddTab(TabControl tabs, string title)
    {
        var page = new TabPage(title) { AutoScroll = true, Padding = new Padding(10) };
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.Controls.Add(table);
        tabs.TabPages.Add(page);
        return table;
    }

    private static void Row(TableLayoutPanel table, string label, Control control)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var caption = new Label { Text = label, AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 5, 8, 5) };
        control.Margin = new Padding(0, 4, 0, 4);
        table.Controls.Add(caption, 0, row);
        table.Controls.Add(control, 1, row);
    }

    private static void Note(TableLayoutPanel table, string text)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var note = new Label { Text = text, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 8), ForeColor = SystemColors.GrayText };
        table.Controls.Add(note, 0, row);
        table.SetColumnSpan(note, 2);
    }

    private void BuildExport(TableLayoutPanel table)
    {
        var pathPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        ffmpegPath = new TextBox { Text = Settings.FfmpegPath, Dock = DockStyle.Fill };
        ffmpegPath.TextChanged += (_, _) => Settings.FfmpegPath = ffmpegPath.Text.Trim();
        var browse = new Button { Text = "参照…", Dock = DockStyle.Fill, AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Title = "ffmpeg.exe を選択", Filter = "FFmpeg (ffmpeg.exe)|ffmpeg.exe|実行ファイル (*.exe)|*.exe", CheckFileExists = true };
            if (File.Exists(ffmpegPath.Text)) picker.FileName = ffmpegPath.Text;
            if (picker.ShowDialog(this) == DialogResult.OK) ffmpegPath.Text = picker.FileName;
        };
        pathPanel.Controls.Add(ffmpegPath, 0, 0);
        pathPanel.Controls.Add(browse, 1, 0);
        Row(table, "FFmpeg 実行ファイル", pathPanel);
        Note(table, "空欄ではアプリの設定に従って FFmpeg を自動検索します。動画の読み込みと書き出しに使用します。");
        Row(table, "PNG 圧縮レベル (0～9)", Number(Settings.PngCompression, 0, 9, x => Settings.PngCompression = (int)x));
        Row(table, "静止した場面のみ出力", Check(Settings.StableOnly, x => Settings.StableOnly = x));
        Row(table, "重複画像を省く", Check(Settings.SkipDuplicates, x => Settings.SkipDuplicates = x));
        Row(table, "入力表示を映像に焼き込む", Check(Settings.BurnOverlay, x => Settings.BurnOverlay = x));
        Row(table, "入力ログ TSV を出力", Check(Settings.ExportTsv, x => Settings.ExportTsv = x));
        Row(table, "字幕 SRT を出力", Check(Settings.ExportSrt, x => Settings.ExportSrt = x));
        Row(table, "字幕 VTT を出力", Check(Settings.ExportVtt, x => Settings.ExportVtt = x));
        Note(table, "静止・重複の設定は PNG 連番出力に適用します。入力表示と字幕には動画に対応する入力ログが必要です。");
    }

    private void BuildPointer(TableLayoutPanel table)
    {
        var o = Settings.Overlay;
        Row(table, "ポインターを表示", Check(o.ShowPointer, x => o.ShowPointer = x));
        Row(table, "ポインターの形", Choice(o.PointerShape, [("cursor", "矢印"), ("circle", "円"), ("square", "四角"), ("star", "星"), ("rote_star", "回転する星")], x => o.PointerShape = x));
        Row(table, "ポインター色", ColorInput("ポインター色", o.PointerColor, x => o.PointerColor = x));
        Row(table, "塗り色 (空欄: 標準)", ColorInput("ポインター塗り色", o.PointerFill, x => o.PointerFill = x, true));
        Row(table, "ポインターサイズ (px)", Number(o.PointerSize, 1, 200, x => o.PointerSize = (int)x));
        Row(table, "ポインター線幅 (px)", Number(o.PointerWidth, 1, 32, x => o.PointerWidth = (int)x));
        Row(table, "クリックを表示", Check(o.ShowClicks, x => o.ShowClicks = x));
        Row(table, "クリックの形", Choice(o.ClickShape, [("circle", "円"), ("square", "四角"), ("star", "星"), ("rote_star", "回転する星")], x => o.ClickShape = x));
        Row(table, "左クリック色", ColorInput("左クリック色", o.LeftColor, x => o.LeftColor = x));
        Row(table, "右クリック色", ColorInput("右クリック色", o.RightColor, x => o.RightColor = x));
        Row(table, "中クリック色", ColorInput("中クリック色", o.MiddleColor, x => o.MiddleColor = x));
        Row(table, "クリック線幅 (px)", Number(o.ClickWidth, 1, 32, x => o.ClickWidth = (int)x));
        Row(table, "波紋サイズ (px)", Number(o.RippleSize, 1, 300, x => o.RippleSize = (int)x));
        Row(table, "波紋の表示時間 (秒)", Number((decimal)o.RippleDuration, 0.05m, 10, x => o.RippleDuration = (double)x, 2, 0.05m));
    }

    private void BuildSubtitle(TableLayoutPanel table)
    {
        var o = Settings.Overlay;
        Row(table, "キー入力を表示", Check(o.ShowKeys, x => o.ShowKeys = x));
        var font = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill, Text = o.FontFamily };
        using (var fonts = new System.Drawing.Text.InstalledFontCollection())
            font.Items.AddRange(fonts.Families.Select(x => (object)x.Name).ToArray());
        font.TextChanged += (_, _) => { o.FontFamily = font.Text.Trim(); preview.Invalidate(); };
        Row(table, "フォント", font);
        Row(table, "文字サイズ (px)", Number(o.FontSize, 8, 160, x => o.FontSize = (int)x));
        Row(table, "文字色", ColorInput("文字色", o.TextColor, x => o.TextColor = x));
        Row(table, "背景色 (空欄: なし)", ColorInput("字幕背景色", o.BackgroundColor, x => o.BackgroundColor = x, true));
        Row(table, "輪郭色 (空欄: なし)", ColorInput("字幕輪郭色", o.OutlineColor, x => o.OutlineColor = x, true));
        Row(table, "表示時間 (秒)", Number((decimal)o.FadeDuration, 0.1m, 30, x => o.FadeDuration = (double)x, 1, 0.1m));
        Row(table, "最大表示段数", Number(o.MaxStack, 1, 12, x => o.MaxStack = (int)x));
        Row(table, "左右の位置", Choice(o.Horizontal, [("left", "左"), ("center", "中央"), ("right", "右")], x => o.Horizontal = x));
        Row(table, "上下の位置", Choice(o.Vertical, [("top", "上"), ("center", "中央"), ("bottom", "下")], x => o.Vertical = x));
        Row(table, "左右オフセット (px)", Number(o.OffsetX, -8192, 8192, x => o.OffsetX = (int)x));
        Row(table, "上下オフセット (px)", Number(o.OffsetY, -8192, 8192, x => o.OffsetY = (int)x));
        Row(table, "文字の上下調整 (px)", Number(o.TextOffsetY, -100, 100, x => o.TextOffsetY = (int)x));
        Row(table, "背景の上下余白 (px)", Number(o.PaddingY, 0, 100, x => o.PaddingY = (int)x));
    }

    private void BuildTheme(TableLayoutPanel table)
    {
        Row(table, "キャンバス背景色", ColorInput("キャンバス背景色", Settings.CanvasColor, x => Settings.CanvasColor = x));
        Row(table, "切り出し枠の色", ColorInput("切り出し枠の色", Settings.CropColor, x => Settings.CropColor = x));
        Note(table, "下の表示は色と形の見本です。動画外の切り出し部分は常に黒く出力します。\n設定は「保存」で適用し、キャンセルすると編集内容を破棄します。");
    }

    private CheckBox Check(bool value, Action<bool> apply)
    {
        var box = new CheckBox { Checked = value, AutoSize = true, Text = "有効", Padding = new Padding(0, 2, 0, 2) };
        box.CheckedChanged += (_, _) => { apply(box.Checked); preview.Invalidate(); };
        return box;
    }

    private NumericUpDown Number(decimal value, decimal min, decimal max, Action<decimal> apply, int decimals = 0, decimal increment = 1)
    {
        var number = new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = increment, Value = Math.Clamp(value, min, max), Width = 125 };
        number.ValueChanged += (_, _) => { apply(number.Value); preview.Invalidate(); };
        return number;
    }

    private ComboBox Choice(string value, (string Key, string Label)[] items, Action<string> apply)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180, DisplayMember = nameof(Option.Label), ValueMember = nameof(Option.Key) };
        foreach (var (key, label) in items) combo.Items.Add(new Option(key, label));
        combo.SelectedIndex = Math.Max(0, Array.FindIndex(items, x => x.Key == value));
        combo.SelectedIndexChanged += (_, _) => { if (combo.SelectedItem is Option option) apply(option.Key); preview.Invalidate(); };
        return combo;
    }

    private sealed record Option(string Key, string Label);

    private Control ColorInput(string label, string value, Action<string> apply, bool optional = false)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = Padding.Empty };
        var text = new TextBox { Text = value, Width = 120 };
        var button = new Button { Text = "選択…", AutoSize = true, MinimumSize = new Size(65, 25) };
        colorInputs.Add((text, optional, label));
        text.TextChanged += (_, _) =>
        {
            bool valid = IsColor(text.Text, optional);
            text.ForeColor = valid ? SystemColors.WindowText : Color.Firebrick;
            if (valid) { apply(text.Text.Trim()); preview.Invalidate(); }
        };
        button.Click += (_, _) =>
        {
            using var picker = new ColorDialog { FullOpen = true, Color = ColorOf(text.Text, Color.White) };
            if (picker.ShowDialog(this) == DialogResult.OK) text.Text = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
        };
        panel.Controls.Add(text);
        panel.Controls.Add(button);
        return panel;
    }

    private void AcceptSettings()
    {
        foreach (var (box, optional, label) in colorInputs)
        {
            if (IsColor(box.Text, optional)) continue;
            MessageBox.Show(this, $"{label}を #RRGGBB 形式の色で入力してください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            SelectInput(box);
            return;
        }
        if (Settings.FfmpegPath.Length > 0 && !File.Exists(Settings.FfmpegPath))
        {
            MessageBox.Show(this, "指定された FFmpeg 実行ファイルが見つかりません。参照から選択するか、空欄にしてください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            SelectInput(ffmpegPath);
            return;
        }
        if (string.IsNullOrWhiteSpace(Settings.Overlay.FontFamily)) Settings.Overlay.FontFamily = "Yu Gothic UI";
        DialogResult = DialogResult.OK;
        Close();
    }

    private static void SelectInput(Control input)
    {
        for (Control? parent = input.Parent; parent is not null; parent = parent.Parent)
            if (parent is TabPage page && page.Parent is TabControl tabs) tabs.SelectedTab = page;
        input.Focus();
    }

    private static bool IsColor(string value, bool optional) => ColorText.IsValid(value, optional);

    private static Color ColorOf(string value, Color fallback) => ColorText.Parse(value, fallback);

    private void PaintPreview(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        var o = Settings.Overlay;
        g.Clear(ColorOf(Settings.CanvasColor, Color.FromArgb(120, 120, 120)));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var cropPen = new Pen(ColorOf(Settings.CropColor, Color.DarkOrange), 2) { DashStyle = DashStyle.Dash };
        var bounds = new Rectangle(8, 8, Math.Max(1, preview.Width - 17), Math.Max(1, preview.Height - 17));
        g.DrawRectangle(cropPen, bounds);
        using var labelFont = new Font(Font.FontFamily, 9);
        g.DrawString("色・形の見本", labelFont, Brushes.White, 16, 12);
        if (o.ShowClicks)
        {
            string[] colors = [o.LeftColor, o.RightColor, o.MiddleColor];
            float size = Math.Clamp(o.RippleSize, 8, 55);
            for (int i = 0; i < colors.Length; i++)
            {
                using var pen = new Pen(ColorOf(colors[i], Color.Red), Math.Min(o.ClickWidth, 10));
                var rect = new RectangleF(24 + i * 65, 55 - size / 2, size, size);
                using var path = SampleShape(o.ClickShape, rect);
                g.DrawPath(pen, path);
            }
        }
        if (o.ShowPointer)
        {
            float size = Math.Clamp(o.PointerSize, 4, 35);
            float x = 42, y = 55;
            using var pen = new Pen(ColorOf(o.PointerColor, Color.Black), Math.Min(o.PointerWidth, 10));
            using var fill = new SolidBrush(ColorOf(o.PointerFill, Color.Transparent));
            if (o.PointerShape == "cursor")
            {
                PointF[] points = [new(x, y), new(x + size / 3, y + size), new(x + size / 2, y + size / 2), new(x + size, y + size / 3)];
                g.FillPolygon(o.PointerFill.Length == 0 ? Brushes.White : fill, points); g.DrawPolygon(pen, points);
            }
            else { using var path = SampleShape(o.PointerShape, new RectangleF(x - size / 2, y - size / 2, size, size)); g.FillPath(fill, path); g.DrawPath(pen, path); }
        }
        if (!o.ShowKeys) return;
        using var font = new Font(string.IsNullOrWhiteSpace(o.FontFamily) ? "Yu Gothic UI" : o.FontFamily, Math.Clamp(o.FontSize * 0.6f, 10, 30), GraphicsUnit.Pixel);
        const string sample = "Ctrl + S  /  日本語入力";
        var measured = g.MeasureString(sample, font);
        float px = Math.Min(10, o.PaddingY), boxW = measured.Width + 16, boxH = measured.Height + px * 2;
        float bx = o.Horizontal == "left" ? 16 : o.Horizontal == "right" ? preview.Width - boxW - 16 : (preview.Width - boxW) / 2;
        float by = o.Vertical == "top" ? 15 : o.Vertical == "center" ? (preview.Height - boxH) / 2 : preview.Height - boxH - 15;
        bx += Math.Clamp(o.OffsetX * 0.15f, -40, 40); by += Math.Clamp(o.OffsetY * 0.15f, -15, 15);
        using var background = new SolidBrush(ColorOf(o.BackgroundColor, Color.Transparent));
        using var foreground = new SolidBrush(ColorOf(o.TextColor, Color.White));
        g.FillRectangle(background, bx, by, boxW, boxH);
        if (o.OutlineColor.Length > 0)
        {
            using var outline = new Pen(ColorOf(o.OutlineColor, Color.White));
            g.DrawRectangle(outline, bx, by, boxW, boxH);
        }
        g.DrawString(sample, font, foreground, bx + 8, by + px + Math.Clamp(o.TextOffsetY, -8, 8));
    }

    private static GraphicsPath SampleShape(string shape, RectangleF bounds)
    {
        var path = new GraphicsPath();
        if (shape == "square") path.AddRectangle(bounds);
        else if (shape is "star" or "rote_star")
        {
            PointF[] points = new PointF[10];
            for (int i = 0; i < points.Length; i++)
            {
                double angle = (i * 36 - 90 + (shape == "rote_star" ? 36 : 0)) * Math.PI / 180;
                float radius = bounds.Width / 2 * (i % 2 == 0 ? 1 : .45f);
                points[i] = new(bounds.Left + bounds.Width / 2 + radius * (float)Math.Cos(angle), bounds.Top + bounds.Height / 2 + radius * (float)Math.Sin(angle));
            }
            path.AddPolygon(points);
        }
        else path.AddEllipse(bounds);
        return path;
    }
}

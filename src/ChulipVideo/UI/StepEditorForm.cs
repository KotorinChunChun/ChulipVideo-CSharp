using System.ComponentModel;
using ChulipVideo.Core;
using ChulipVideo.Media;
using ChulipVideo.Steps;
using EventLog = ChulipVideo.Export.EventLog;

namespace ChulipVideo.UI;

/// <summary>入力ログから抽出したステップを確認・修正し、動画ごとの手順ファイルへ保存する画面。</summary>
public sealed class StepEditorForm : BrandedForm
{
    private const int CacheLimit = 40;
    private readonly FfmpegService media;
    private readonly MediaInfo info;
    private readonly EventLog events;
    private readonly AppSettings settings;
    private readonly double rangeStart, rangeEnd, currentTime;
    private readonly CropRect crop;
    private readonly IReadOnlyList<MaskRegion> masks;
    private readonly ListView list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true, AccessibleName = "ステップ一覧" };
    private readonly StepPreview preview = new() { Dock = DockStyle.Fill, AccessibleName = "ステップの画像" };
    private readonly TextBox description = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AccessibleName = "説明文" };
    private readonly CheckBox enabled = new() { Text = "手順書に含める", AutoSize = true };
    private readonly Label detail = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Text = "準備完了" };
    private readonly Button cancel = new() { Text = "中断", AutoSize = true, Enabled = false };
    private readonly List<Control> editors = [];
    private readonly Dictionary<double, Bitmap> cache = [];
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private bool syncing, busy, released;
    private int imageVersion;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public StepDocument Document { get; private set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Dirty { get; private set; }

    public StepEditorForm(FfmpegService media, MediaInfo info, EventLog events, AppSettings settings, double rangeStart, double rangeEnd, CropRect crop, double currentTime,
        IReadOnlyList<MaskRegion>? masks = null)
    {
        this.masks = masks ?? [];
        this.media = media; this.info = info; this.events = events; this.settings = settings;
        this.rangeStart = rangeStart; this.rangeEnd = rangeEnd; this.crop = crop; this.currentTime = currentTime;
        Document = new StepDocument { Start = rangeStart, End = rangeEnd, Crop = crop };
        Text = WindowTitle.Create("手順書エディター");
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 520);
        ClientSize = new Size(1000, 680);
        ShowInTaskbar = false;
        KeyPreview = true;
        BuildLayout();
        list.SelectedIndexChanged += (_, _) => ShowSelected();
        description.TextChanged += (_, _) =>
        {
            if (syncing || Single() is not StepItem step) return;
            step.Description = description.Text; step.DescriptionSource = "Manual"; MarkDirty(); UpdateItem(list.SelectedIndices[0]);
        };
        enabled.CheckedChanged += (_, _) =>
        {
            if (syncing) return;
            foreach (int index in list.SelectedIndices) Document.Steps[index].Enabled = enabled.Checked;
            // 除外すると後ろの手順の番号が詰まるため、全行を更新する。
            for (int i = 0; i < Document.Steps.Count; i++) UpdateItem(i);
            MarkDirty();
        };
        cancel.Click += (_, _) => operation?.Cancel();
        Shown += (_, _) => LoadOrDetect();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        toolbar.Controls.AddRange([
            Tool("再検出", Redetect, "入力ログから手順を作り直す"), Tool("現在位置に追加", AddAtCurrent, "主画面の現在位置に手順を追加"),
            Tool("削除", DeleteSelected, "選択した手順を削除 (Delete)"), Tool("結合", MergeSelected, "選択した手順を1つにまとめる"),
            Tool("↑", () => MoveSelected(-1), "上へ移動"), Tool("↓", () => MoveSelected(1), "下へ移動"),
            Tool("保存", () => Save(), "手順ファイルへ保存 (Ctrl+S)"), cancel
        ]);
        root.Controls.Add(toolbar, 0, 0);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 420, FixedPanel = FixedPanel.Panel1 };
        list.Columns.Add("番号", 48); list.Columns.Add("時刻", 96); list.Columns.Add("操作", 120); list.Columns.Add("説明", 400);
        split.Panel1.Controls.Add(list);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        right.Controls.Add(preview, 0, 0);
        var frames = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        frames.Controls.AddRange([
            Tool("−0.1秒", () => ShiftFrame(-.1), "画像を0.1秒前へ"), Tool("◀ 1フレーム", () => ShiftFrame(-1 / info.Fps), "画像を1フレーム前へ"),
            Tool("1フレーム ▶", () => ShiftFrame(1 / info.Fps), "画像を1フレーム後へ"), Tool("+0.1秒", () => ShiftFrame(.1), "画像を0.1秒後へ")
        ]);
        right.Controls.Add(frames, 0, 1);
        right.Controls.Add(detail, 0, 2);
        right.Controls.Add(description, 0, 3);
        right.Controls.Add(enabled, 0, 4);
        editors.AddRange([frames, description, enabled]);
        split.Panel2.Controls.Add(right);
        root.Controls.Add(split, 0, 1);
        root.Controls.Add(status, 0, 2);
        Controls.Add(root);
        editors.AddRange(toolbar.Controls.Cast<Control>().Where(c => c != cancel));
    }

    private Button Tool(string text, Action action, string tip)
    {
        var button = new Button { Text = text, AutoSize = true, AccessibleName = tip, Margin = new Padding(2) };
        button.Click += (_, _) => action();
        return button;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (busy) return base.ProcessCmdKey(ref msg, keyData);
        if (keyData == (Keys.Control | Keys.S)) { Save(); return true; }
        if (keyData == Keys.Delete && ActiveControl == list) { DeleteSelected(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void LoadOrDetect()
    {
        var saved = StepStore.Load(info, out string? warning);
        if (saved is not null)
        {
            Document = saved;
            RefreshList(0);
            Status(warning ?? $"保存済みの手順 {Document.Steps.Count} 件を読み込みました。");
            return;
        }
        if (warning is not null) Status(warning);
        _ = DetectAsync();
    }

    private void Redetect()
    {
        if (Document.Steps.Count > 0 && MessageBox.Show(this, "現在の手順を破棄して、入力ログから作り直しますか。", Text,
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _ = DetectAsync();
    }

    private async Task DetectAsync()
    {
        if (events.Count == 0) { Status("この動画には入力ログ（同名のTSV）がありません。「現在位置に追加」で手順を作れます。"); return; }
        await RunAsync("手順を抽出しています…", async token =>
        {
            var progress = new Progress<double>(p => Status($"画面の変化を計測しています… {p * 100:0}%"));
            var detected = await StepDetector.DetectAsync(media, info, events, rangeStart, rangeEnd, crop, settings, progress, token);
            if (IsDisposed) return;
            Document = detected;
            MarkDirty();
            RefreshList(0);
            Status(Document.Steps.Count == 0 ? "区間内に手順となる操作がありませんでした。" : $"{Document.Steps.Count} 件の手順を抽出しました（未保存）。");
        });
    }

    private async Task RunAsync(string message, Func<CancellationToken, Task> action)
    {
        if (busy) return;
        busy = true; SetBusy(true); Status(message);
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try { await action(operation.Token); }
        catch (OperationCanceledException) { if (!IsDisposed) Status("中断しました。"); }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or ArgumentException or Win32Exception)
        {
            if (!IsDisposed) { Status(ex.Message); MessageBox.Show(this, ex.Message, WindowTitle.Create("エラー"), MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        finally
        {
            operation.Dispose(); operation = null; busy = false;
            if (!IsDisposed) SetBusy(false);
        }
    }

    private void SetBusy(bool value)
    {
        foreach (var control in editors) control.Enabled = !value;
        list.Enabled = !value;
        cancel.Enabled = value;
    }

    private void RefreshList(int select)
    {
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            for (int i = 0; i < Document.Steps.Count; i++) list.Items.Add(new ListViewItem(["", "", "", ""]));
            for (int i = 0; i < Document.Steps.Count; i++) UpdateItem(i);
        }
        finally { list.EndUpdate(); }
        if (Document.Steps.Count > 0) { list.SelectedIndices.Clear(); list.SelectedIndices.Add(Math.Clamp(select, 0, Document.Steps.Count - 1)); }
        else ShowSelected();
    }

    private void UpdateItem(int index)
    {
        var step = Document.Steps[index];
        int number = Document.Steps.Take(index + 1).Count(s => s.Enabled);
        var item = list.Items[index];
        item.SubItems[0].Text = step.Enabled ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "除外";
        item.SubItems[1].Text = TimecodeUpDown.FormatTimecode((decimal)step.Time);
        item.SubItems[2].Text = step.Label.Length > 0 ? step.Label : "手動";
        item.SubItems[3].Text = step.Description.ReplaceLineEndings(" ");
        item.ForeColor = step.Enabled ? SystemColors.WindowText : SystemColors.GrayText;
    }

    private StepItem? Single() => list.SelectedIndices.Count == 1 ? Document.Steps[list.SelectedIndices[0]] : null;

    private void ShowSelected()
    {
        syncing = true;
        try
        {
            var step = Single();
            description.Enabled = step is not null && !busy;
            description.Text = step?.Description ?? "";
            enabled.Checked = list.SelectedIndices.Count > 0 && list.SelectedIndices.Cast<int>().All(i => Document.Steps[i].Enabled);
            detail.Text = step is null ? (list.SelectedIndices.Count > 1 ? $"{list.SelectedIndices.Count} 件を選択中" : "") :
                $"操作 {TimecodeUpDown.FormatTimecode((decimal)step.Time)}　画像 {TimecodeUpDown.FormatTimecode((decimal)step.FrameTime)}　{(step.Label.Length > 0 ? step.Label : "手動")}";
        }
        finally { syncing = false; }
        _ = LoadImageAsync(Single());
    }

    private async Task LoadImageAsync(StepItem? step)
    {
        int version = ++imageVersion;
        if (step is null) { preview.SetImage(null, null, null, crop); return; }
        try
        {
            if (!cache.TryGetValue(step.FrameTime, out var image))
            {
                image = await StepDetector.RenderImageAsync(media, info, events, crop, settings, step.FrameTime, masks, lifetime.Token);
                if (IsDisposed) { image.Dispose(); return; }
                // 表示中の画像は追い出さない（破棄すると描画中に参照が切れる）。
                if (cache.Count >= CacheLimit && cache.FirstOrDefault(c => !ReferenceEquals(c.Value, preview.Image)) is { Value: not null } oldest)
                { cache.Remove(oldest.Key); oldest.Value.Dispose(); }
                cache[step.FrameTime] = image;
            }
            if (version != imageVersion || IsDisposed) return;
            preview.SetImage(image, new Point(step.X, step.Y), step.EndX is int x && step.EndY is int y ? new Point(x, y) : null, crop);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or Win32Exception)
        {
            if (version == imageVersion && !IsDisposed) Status("画像を取得できませんでした: " + ex.Message);
        }
    }

    private void ShiftFrame(double seconds)
    {
        if (Single() is not StepItem step) return;
        step.FrameTime = info.FrameTime(Math.Clamp(step.FrameTime + seconds, 0, info.LastFrameTime));
        MarkDirty(); ShowSelected();
    }

    private void AddAtCurrent()
    {
        double time = info.FrameTime(currentTime);
        var step = new StepItem { Time = time, FrameTime = time, X = crop.X + crop.Width / 2, Y = crop.Y + crop.Height / 2, DescriptionSource = "Manual" };
        int index = Document.Steps.FindIndex(s => s.Time > time);
        if (index < 0) index = Document.Steps.Count;
        Document.Steps.Insert(index, step);
        MarkDirty(); RefreshList(index);
        description.Focus();
    }

    private void DeleteSelected()
    {
        var indices = list.SelectedIndices.Cast<int>().OrderDescending().ToArray();
        if (indices.Length == 0) return;
        foreach (int index in indices) Document.Steps.RemoveAt(index);
        MarkDirty(); RefreshList(indices[^1]);
    }

    private void MergeSelected()
    {
        var indices = list.SelectedIndices.Cast<int>().Order().ToArray();
        if (indices.Length < 2) { Status("結合する手順を2件以上選んでください。"); return; }
        var first = Document.Steps[indices[0]];
        first.Description = string.Join("\n", indices.Select(i => Document.Steps[i].Description).Where(d => d.Length > 0));
        first.DescriptionSource = "Manual";
        foreach (int index in indices.Skip(1).OrderDescending()) Document.Steps.RemoveAt(index);
        MarkDirty(); RefreshList(indices[0]);
    }

    private void MoveSelected(int direction)
    {
        if (list.SelectedIndices.Count != 1) return;
        int index = list.SelectedIndices[0], target = index + direction;
        if (target < 0 || target >= Document.Steps.Count) return;
        (Document.Steps[index], Document.Steps[target]) = (Document.Steps[target], Document.Steps[index]);
        MarkDirty(); RefreshList(target);
    }

    private bool Save()
    {
        try
        {
            StepStore.Save(info, Document);
            Dirty = false; UpdateTitle();
            Status("保存しました: " + StepStore.PathFor(info.Path));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "手順を保存できません: " + ex.Message, WindowTitle.Create("エラー"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void MarkDirty() { Dirty = true; UpdateTitle(); }
    private void UpdateTitle() => Text = WindowTitle.Create("手順書エディター" + (Dirty ? " *" : ""));
    private void Status(string message) { if (!IsDisposed) status.Text = message; }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (Dirty && e.CloseReason != CloseReason.WindowsShutDown)
        {
            var answer = MessageBox.Show(this, "手順の変更を保存しますか。", Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel || answer == DialogResult.Yes && !Save()) { e.Cancel = true; return; }
        }
        lifetime.Cancel();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !released)
        {
            released = true;
            lifetime.Cancel(); lifetime.Dispose(); operation?.Dispose();
            foreach (var image in cache.Values) image.Dispose();
            cache.Clear();
        }
        base.Dispose(disposing);
    }

    /// <summary>ステップの画像と操作位置の目印を表示する。画像は所有せず、呼び出し側が破棄する。</summary>
    private sealed class StepPreview : Control
    {
        private Point? point, endPoint;
        private CropRect area;
        public Bitmap? Image { get; private set; }

        public StepPreview()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(120, 120, 120);
        }

        public void SetImage(Bitmap? image, Point? point, Point? endPoint, CropRect area)
        {
            Image = image; this.point = point; this.endPoint = endPoint; this.area = area;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.Clear(BackColor);
            if (Image is null)
            {
                TextRenderer.DrawText(g, "手順を選ぶと画像を表示します", Font, ClientRectangle, Color.Gainsboro, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            float scale = Math.Min((Width - 8f) / Image.Width, (Height - 8f) / Image.Height);
            var target = new RectangleF((Width - Image.Width * scale) / 2, (Height - Image.Height * scale) / 2, Image.Width * scale, Image.Height * scale);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(Image, target);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            PointF Map(Point p) => new(target.X + (p.X - area.X) * scale, target.Y + (p.Y - area.Y) * scale);
            using var pen = new Pen(Color.Red, 3);
            if (point is Point start)
            {
                var p = Map(start);
                g.DrawEllipse(pen, p.X - 12, p.Y - 12, 24, 24);
                if (endPoint is Point end)
                {
                    using var arrow = new Pen(Color.Red, 3) { CustomEndCap = new System.Drawing.Drawing2D.AdjustableArrowCap(5, 5) };
                    g.DrawLine(arrow, p, Map(end));
                }
            }
        }
    }
}

using System.ComponentModel;
using System.Globalization;
using ChulipVideo.Core;

namespace ChulipVideo.UI;

/// <summary>区間とマーカーを編集し、区間を主画面へ反映したり一括書き出しを指示したりする（R11）。編集はコピーに対して行う。</summary>
public sealed class ClipListForm : BrandedForm
{
    private readonly MediaInfo media;
    private readonly double rangeStart, rangeEnd, currentTime;
    private readonly CropRect crop;
    private readonly string defaultFolder;
    private readonly DataGridView clipGrid = Grid("区間一覧");
    private readonly DataGridView markerGrid = Grid("マーカー一覧");
    private bool loading;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public List<ClipRegion> Clips { get; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public List<Marker> Markers { get; }
    /// <summary>「主画面へ反映」で選んだ区間。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public ClipRegion? Apply { get; private set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public ClipBatchRequest? Batch { get; private set; }

    public ClipListForm(IEnumerable<ClipRegion> clips, IEnumerable<Marker> markers, MediaInfo media, double rangeStart, double rangeEnd, CropRect crop, double currentTime, string defaultFolder)
    {
        this.media = media; this.rangeStart = rangeStart; this.rangeEnd = rangeEnd; this.crop = crop; this.currentTime = currentTime; this.defaultFolder = defaultFolder;
        Clips = clips.Select(c => c with { }).ToList();
        Markers = markers.Select(m => m with { }).OrderBy(m => m.Time).ToList();
        Text = WindowTitle.Create("区間とマーカー");
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(720, 480);
        ClientSize = new Size(860, 560);
        ShowInTaskbar = false;
        clipGrid.Columns.AddRange([
            new DataGridViewCheckBoxColumn { HeaderText = "有効", Width = 44 },
            new DataGridViewTextBoxColumn { HeaderText = "名前", Width = 180 },
            new DataGridViewTextBoxColumn { HeaderText = "開始", Width = 104 },
            new DataGridViewTextBoxColumn { HeaderText = "終了", Width = 104 },
            new DataGridViewTextBoxColumn { HeaderText = "切り出し", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill }
        ]);
        markerGrid.Columns.AddRange([
            new DataGridViewTextBoxColumn { HeaderText = "時刻", Width = 104 },
            new DataGridViewTextBoxColumn { HeaderText = "名前", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill }
        ]);
        clipGrid.CellValidating += (_, e) => Validate(clipGrid, e, e.ColumnIndex is 2 or 3);
        markerGrid.CellValidating += (_, e) => Validate(markerGrid, e, e.ColumnIndex == 0);
        clipGrid.CellEndEdit += (_, e) => ApplyClip(e.RowIndex);
        clipGrid.CurrentCellDirtyStateChanged += (_, _) => { if (clipGrid.CurrentCell is DataGridViewCheckBoxCell) clipGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        clipGrid.CellValueChanged += (_, e) => { if (e.ColumnIndex == 0) ApplyClip(e.RowIndex); };
        markerGrid.CellEndEdit += (_, e) => ApplyMarker(e.RowIndex);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(6) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.Controls.Add(clipGrid, 0, 0);
        root.Controls.Add(Row(Tool("現在の区間を追加", AddCurrent), Tool("マーカー間から作成", FromMarkers), Tool("主画面へ反映", ApplySelected),
            Tool("↑", () => MoveClip(-1)), Tool("↓", () => MoveClip(1)), Tool("区間を削除", () => Delete(clipGrid, Clips))), 0, 1);
        root.Controls.Add(markerGrid, 0, 2);
        root.Controls.Add(Row(Tool("現在位置にマーカー", AddMarker), Tool("マーカーを削除", () => Delete(markerGrid, Markers))), 0, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Size = new Size(100, 30) };
        var accept = new Button { Text = "適用", DialogResult = DialogResult.OK, Size = new Size(100, 30) };
        buttons.Controls.AddRange([cancel, accept, Tool("一括書き出し…", StartBatch)]);
        root.Controls.Add(buttons, 0, 4);
        Controls.Add(root);
        AcceptButton = accept; CancelButton = cancel;
        Reload();
    }

    private static DataGridView Grid(string name) => new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true, AccessibleName = name
    };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(controls);
        return row;
    }

    private static Button Tool(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(0, 30), Margin = new Padding(2) };
        button.Click += (_, _) => action();
        return button;
    }

    private static string Time(double seconds) => TimecodeUpDown.FormatTimecode((decimal)seconds);
    private static string CropText(CropRect? c) => c is CropRect r ? $"{r.X}, {r.Y}　{r.Width}×{r.Height}" : "書き出し時の切り出し範囲";

    private void Reload()
    {
        loading = true;
        try
        {
            clipGrid.Rows.Clear();
            foreach (var c in Clips) clipGrid.Rows.Add(c.Enabled, c.Name, Time(c.Start), Time(c.End), CropText(c.Crop));
            markerGrid.Rows.Clear();
            foreach (var m in Markers) markerGrid.Rows.Add(Time(m.Time), m.Label);
        }
        finally { loading = false; }
    }

    private static void Validate(DataGridView grid, DataGridViewCellValidatingEventArgs e, bool isTime)
    {
        bool invalid = isTime && !TimecodeUpDown.TryParseTimecode(e.FormattedValue?.ToString(), out _);
        grid.Rows[e.RowIndex].ErrorText = invalid ? "時刻は秒数または時:分:秒で入力してください。" : "";
        e.Cancel = invalid;
    }

    private void ApplyClip(int row)
    {
        if (loading || row < 0 || row >= Clips.Count) return;
        var cells = clipGrid.Rows[row].Cells;
        var clip = Clips[row];
        clip.Enabled = cells[0].Value is true;
        clip.Name = cells[1].Value?.ToString() ?? "";
        if (TimecodeUpDown.TryParseTimecode(cells[2].Value?.ToString(), out decimal start)) clip.Start = (double)start;
        if (TimecodeUpDown.TryParseTimecode(cells[3].Value?.ToString(), out decimal end)) clip.End = (double)end;
        Clips[row] = SettingsStore.NormalizeClips([clip], media).Single();
        loading = true;
        try { cells[1].Value = Clips[row].Name; cells[2].Value = Time(Clips[row].Start); cells[3].Value = Time(Clips[row].End); }
        finally { loading = false; }
    }

    private void ApplyMarker(int row)
    {
        if (loading || row < 0 || row >= Markers.Count) return;
        var cells = markerGrid.Rows[row].Cells;
        if (TimecodeUpDown.TryParseTimecode(cells[0].Value?.ToString(), out decimal time)) Markers[row].Time = (double)time;
        Markers[row].Label = cells[1].Value?.ToString() ?? "";
        var normalized = SettingsStore.NormalizeMarkers(Markers, media);
        Markers.Clear(); Markers.AddRange(normalized);
        BeginInvoke(Reload); // 編集中のセルを書き換えないよう、編集の確定後に並べ直す
    }

    private IEnumerable<int> Selected(DataGridView grid) => grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).Order();

    private void AddCurrent()
    {
        Clips.Add(SettingsStore.NormalizeClips([new ClipRegion { Name = $"区間{Clips.Count + 1}", Start = rangeStart, End = rangeEnd, Crop = crop }], media).Single());
        Reload();
    }

    /// <summary>マーカーで区切った各区間（先頭のマーカー〜次のマーカー、最後は動画の末尾まで）を作る。</summary>
    private void FromMarkers()
    {
        if (Markers.Count == 0) { MessageBox.Show(this, "マーカーがありません。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        for (int i = 0; i < Markers.Count; i++)
        {
            double end = i + 1 < Markers.Count ? Markers[i + 1].Time : media.Duration;
            Clips.Add(SettingsStore.NormalizeClips([new ClipRegion { Name = Markers[i].Label, Start = Markers[i].Time, End = end }], media).Single());
        }
        Reload();
    }

    private void AddMarker()
    {
        double time = media.FrameTime(currentTime);
        if (Markers.Any(m => Math.Abs(m.Time - time) < 1e-9)) return;
        Markers.Add(new Marker { Time = time, Label = $"マーカー{Markers.Count + 1}" });
        var normalized = SettingsStore.NormalizeMarkers(Markers, media);
        Markers.Clear(); Markers.AddRange(normalized);
        Reload();
    }

    private void ApplySelected()
    {
        var rows = Selected(clipGrid).ToArray();
        if (rows.Length != 1) { MessageBox.Show(this, "主画面へ反映する区間を1つ選んでください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        Apply = Clips[rows[0]];
        DialogResult = DialogResult.OK;
        Close();
    }

    private void MoveClip(int direction)
    {
        var rows = Selected(clipGrid).ToArray();
        if (rows.Length != 1) return;
        int index = rows[0], target = index + direction;
        if (target < 0 || target >= Clips.Count) return;
        (Clips[index], Clips[target]) = (Clips[target], Clips[index]);
        Reload();
        clipGrid.ClearSelection(); clipGrid.Rows[target].Selected = true;
    }

    private void Delete<T>(DataGridView grid, List<T> items)
    {
        foreach (int row in Selected(grid).OrderDescending()) items.RemoveAt(row);
        Reload();
    }

    private void StartBatch()
    {
        if (!Clips.Any(c => c.Enabled)) { MessageBox.Show(this, "有効な区間がありません。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        using var dialog = new BrandedForm { Text = WindowTitle.Create("一括書き出し"), ClientSize = new Size(480, 210), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false };
        var kinds = new (CheckBox Box, ClipBatchKind Kind)[]
        {
            (new CheckBox { Text = "PNG連番", AutoSize = true }, ClipBatchKind.PngSequence), (new CheckBox { Text = "MP4", AutoSize = true, Checked = true }, ClipBatchKind.Mp4),
            (new CheckBox { Text = "GIF", AutoSize = true }, ClipBatchKind.Gif), (new CheckBox { Text = "手順書", AutoSize = true }, ClipBatchKind.Steps)
        };
        var row = new FlowLayoutPanel { Location = new Point(12, 12), Size = new Size(456, 32) };
        row.Controls.AddRange(kinds.Select(k => (Control)k.Box).ToArray());
        var folder = new TextBox { Location = new Point(12, 56), Width = 380, Text = defaultFolder, AccessibleName = "保存先フォルダー" };
        var browse = new Button { Text = "参照…", Location = new Point(398, 54), AutoSize = true };
        browse.Click += (_, _) => { using var picker = new FolderBrowserDialog { SelectedPath = folder.Text, UseDescriptionForTitle = true, Description = "一括書き出しの保存先" }; if (picker.ShowDialog(dialog) == DialogResult.OK) folder.Text = picker.SelectedPath; };
        var note = new Label { Location = new Point(12, 92), Size = new Size(456, 48), ForeColor = SystemColors.GrayText, Text = "出力名は「動画名_区間名」。既存のファイルは上書きせず、その形式だけを失敗として残りを続けます。手順書は区間内の操作から自動で作ります。" };
        var ok = new Button { Text = "開始", Location = new Point(276, 160), Size = new Size(90, 30), DialogResult = DialogResult.OK };
        var no = new Button { Text = "キャンセル", Location = new Point(376, 160), Size = new Size(92, 30), DialogResult = DialogResult.Cancel };
        dialog.Controls.AddRange([row, folder, browse, note, ok, no]); dialog.AcceptButton = ok; dialog.CancelButton = no;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var selected = kinds.Where(k => k.Box.Checked).Aggregate(ClipBatchKind.None, (all, k) => all | k.Kind);
        if (selected == ClipBatchKind.None || !Directory.Exists(folder.Text)) { MessageBox.Show(this, "形式を1つ以上選び、存在するフォルダーを指定してください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        Batch = new ClipBatchRequest(selected, folder.Text);
        DialogResult = DialogResult.OK;
        Close();
    }
}

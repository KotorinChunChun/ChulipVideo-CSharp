using System.ComponentModel;
using System.Globalization;
using ChulipVideo.Core;

namespace ChulipVideo.UI;

/// <summary>マスクの時間帯・方式・強さ・色を一覧で編集する。編集はコピーに対して行い、「適用」で確定する。</summary>
public sealed class MaskListForm : BrandedForm
{
    private readonly MediaInfo media;
    private readonly double currentTime;
    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true, AutoGenerateColumns = false, AccessibleName = "マスク一覧"
    };
    private static readonly (MaskMode Mode, string Label)[] Modes = [(MaskMode.Mosaic, "モザイク"), (MaskMode.Blur, "ぼかし"), (MaskMode.Fill, "塗りつぶし")];

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public List<MaskRegion> Masks { get; }

    public MaskListForm(IEnumerable<MaskRegion> masks, MediaInfo media, double currentTime, int selected)
    {
        this.media = media; this.currentTime = currentTime;
        Masks = masks.Select(m => m with { }).ToList();
        Text = WindowTitle.Create("マスク一覧");
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(640, 320);
        ClientSize = new Size(820, 380);
        ShowInTaskbar = false;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        grid.Columns.AddRange([
            new DataGridViewCheckBoxColumn { HeaderText = "有効", Width = 44 },
            new DataGridViewTextBoxColumn { HeaderText = "番号", Width = 48, ReadOnly = true },
            new DataGridViewTextBoxColumn { HeaderText = "開始", Width = 104 },
            new DataGridViewTextBoxColumn { HeaderText = "終了", Width = 104 },
            new DataGridViewComboBoxColumn { HeaderText = "方式", Width = 96, DataSource = Modes.Select(m => m.Label).ToArray() },
            new DataGridViewTextBoxColumn { HeaderText = "強さ(px)", Width = 70 },
            new DataGridViewTextBoxColumn { HeaderText = "色", Width = 80 },
            new DataGridViewTextBoxColumn { HeaderText = "メモ", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill },
            new DataGridViewTextBoxColumn { HeaderText = "出どころ", Width = 72, ReadOnly = true }
        ]);
        grid.CellValidating += ValidateCell;
        grid.CellEndEdit += (_, e) => ApplyRow(e.RowIndex);
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.CellValueChanged += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex is 0 or 4) ApplyRow(e.RowIndex); };
        root.Controls.Add(grid, 0, 0);
        root.Controls.Add(new Label
        {
            Text = "時刻は秒または時:分:秒で入力します。終了の時刻は含みません。OCR候補は無効の状態で追加されます。内容を確かめ、「有効」にしたものだけが映像にかかります。機密情報には、復元されにくい「塗りつぶし」を使ってください。「ぼかし」は細部を推測される場合があります。",
            AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 4, 0, 4)
        }, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Size = new Size(100, 30) };
        var accept = new Button { Text = "適用", DialogResult = DialogResult.OK, Size = new Size(100, 30) };
        buttons.Controls.AddRange([cancel, accept,
            Tool("削除", DeleteSelected), Tool("現在位置を終了に", () => SetTime(end: true)), Tool("現在位置を開始に", () => SetTime(end: false))]);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
        AcceptButton = accept; CancelButton = cancel;
        Reload(selected);
    }

    private Button Tool(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(0, 30), Margin = new Padding(3) };
        button.Click += (_, _) => action();
        return button;
    }

    private bool loading;

    private void Reload(int select)
    {
        loading = true;
        try { Fill(); } finally { loading = false; }
        if (select >= 0 && select < grid.Rows.Count) { grid.ClearSelection(); grid.Rows[select].Selected = true; grid.CurrentCell = grid.Rows[select].Cells[2]; }
    }

    private void Fill()
    {
        grid.Rows.Clear();
        for (int i = 0; i < Masks.Count; i++)
        {
            var m = Masks[i];
            grid.Rows.Add(m.Enabled, (i + 1).ToString(CultureInfo.InvariantCulture), Time(m.Start), Time(m.End),
                Modes.First(x => x.Mode == m.Mode).Label, m.Strength.ToString(CultureInfo.InvariantCulture), m.Color, m.Note, m.Source == "Suggested" ? "OCR候補" : "手動");
        }
    }

    private static string Time(double seconds) => TimecodeUpDown.FormatTimecode((decimal)seconds);

    private void ValidateCell(object? sender, DataGridViewCellValidatingEventArgs e)
    {
        string text = e.FormattedValue?.ToString() ?? "";
        string? error = e.ColumnIndex switch
        {
            2 or 3 when !TimecodeUpDown.TryParseTimecode(text, out _) => "時刻は秒数または時:分:秒で入力してください。",
            5 when !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value is < 2 or > 256 => "強さは2～256の整数で入力してください。",
            6 when !ColorText.IsValid(text) => "色は#RRGGBB形式で入力してください。",
            _ => null
        };
        grid.Rows[e.RowIndex].ErrorText = error ?? "";
        if (error is not null) e.Cancel = true;
    }

    private void ApplyRow(int row)
    {
        if (loading || row < 0 || row >= Masks.Count) return;
        var cells = grid.Rows[row].Cells;
        var mask = Masks[row];
        mask.Enabled = cells[0].Value is true;
        if (TimecodeUpDown.TryParseTimecode(cells[2].Value?.ToString(), out decimal start)) mask.Start = (double)start;
        if (TimecodeUpDown.TryParseTimecode(cells[3].Value?.ToString(), out decimal end)) mask.End = (double)end;
        mask.Mode = Modes.FirstOrDefault(m => m.Label == cells[4].Value?.ToString(), Modes[0]).Mode;
        if (int.TryParse(cells[5].Value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int strength)) mask.Strength = strength;
        if (ColorText.IsValid(cells[6].Value?.ToString())) mask.Color = cells[6].Value!.ToString()!.Trim();
        mask.Note = cells[7].Value?.ToString() ?? "";
        Normalize(row);
    }

    // 開始・終了の逆転や動画外の時刻は、表示も含めて補正する。
    private void Normalize(int row)
    {
        var normalized = SettingsStore.NormalizeMasks([Masks[row]], media).Single();
        Masks[row] = normalized;
        var cells = grid.Rows[row].Cells;
        cells[2].Value = Time(normalized.Start); cells[3].Value = Time(normalized.End);
    }

    private IEnumerable<int> SelectedRows() => grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).Order();

    private void SetTime(bool end)
    {
        foreach (int row in SelectedRows())
        {
            if (end) Masks[row].End = currentTime; else Masks[row].Start = currentTime;
            if (Masks[row].End <= Masks[row].Start) { if (end) Masks[row].Start = Math.Max(0, currentTime - 1 / media.Fps); else Masks[row].End = media.Duration; }
            Normalize(row);
        }
    }

    private void DeleteSelected()
    {
        var rows = SelectedRows().ToArray();
        foreach (int row in rows.OrderDescending()) Masks.RemoveAt(row);
        Reload(rows.Length > 0 ? Math.Min(rows[0], Masks.Count - 1) : -1);
    }
}

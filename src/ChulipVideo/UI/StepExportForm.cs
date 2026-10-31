using System.ComponentModel;
using ChulipVideo.Steps;

namespace ChulipVideo.UI;

/// <summary>手順書の題名・形式・保存先・画像範囲を選ぶ。</summary>
public sealed class StepExportForm : BrandedForm
{
    private readonly TextBox title = new() { Dock = DockStyle.Fill, AccessibleName = "手順書の題名" };
    private readonly TextBox folder = new() { Dock = DockStyle.Fill, AccessibleName = "保存先フォルダー" };
    private readonly CheckBox markdown = new() { Text = "Markdown (.md)", AutoSize = true, Checked = true };
    private readonly CheckBox html = new() { Text = "HTML (.html)", AutoSize = true, Checked = true };
    private readonly CheckBox word = new() { Text = "Word (.docx)", AutoSize = true, Checked = true };
    private readonly CheckBox pdf = new() { Text = "PDF (.pdf)", AutoSize = true };
    private readonly CheckBox focus = new() { Text = "画像を操作位置の周りに絞る（切り出し範囲の縦横1/2）", AutoSize = true };

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public string TitleText => title.Text.Trim();
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public string Folder => folder.Text.Trim();
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool FocusImage => focus.Checked;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public StepDocumentFormat Formats =>
        (markdown.Checked ? StepDocumentFormat.Markdown : 0) | (html.Checked ? StepDocumentFormat.Html : 0) |
        (word.Checked ? StepDocumentFormat.Word : 0) | (pdf.Checked && pdf.Enabled ? StepDocumentFormat.Pdf : 0);

    public StepExportForm(string defaultTitle, string defaultFolder, bool focusImage, bool pdfAvailable)
    {
        Text = WindowTitle.Create("手順書の書き出し");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(560, 300);
        title.Text = defaultTitle; folder.Text = defaultFolder; focus.Checked = focusImage;
        pdf.Enabled = pdfAvailable;
        if (!pdfAvailable) pdf.Text += "（Microsoft Edgeが見つかりません）";
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, Padding = new Padding(10) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 5; i++) table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.Controls.Add(Caption("題名"), 0, 0); table.Controls.Add(title, 1, 0);
        var folderRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = Padding.Empty };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var browse = new Button { Text = "参照…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "手順書フォルダーを作る場所", UseDescriptionForTitle = true, SelectedPath = Folder };
            if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath;
        };
        folderRow.Controls.Add(folder, 0, 0); folderRow.Controls.Add(browse, 1, 0);
        table.Controls.Add(Caption("保存先"), 0, 1); table.Controls.Add(folderRow, 1, 1);
        var formats = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        formats.Controls.AddRange([markdown, html, word, pdf]);
        table.Controls.Add(Caption("形式"), 0, 2); table.Controls.Add(formats, 1, 2);
        table.Controls.Add(Caption("画像"), 0, 3); table.Controls.Add(focus, 1, 3);
        var note = new Label { Text = "保存先に「動画名_手順書_日時」のフォルダーを新しく作ります。画像にはマスクと注釈がかかります。", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 8, 0, 0) };
        table.Controls.Add(note, 0, 4); table.SetColumnSpan(note, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(6) };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Size = new Size(100, 30) };
        var accept = new Button { Text = "書き出し", Size = new Size(100, 30) };
        accept.Click += (_, _) => Accept();
        buttons.Controls.AddRange([cancel, accept]);
        Controls.Add(table);
        Controls.Add(buttons);
        AcceptButton = accept; CancelButton = cancel;
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 6) };

    private void Accept()
    {
        if (Formats == StepDocumentFormat.None) { MessageBox.Show(this, "書き出す形式を1つ以上選んでください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (!Directory.Exists(Folder)) { MessageBox.Show(this, "保存先のフォルダーがありません。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); folder.Focus(); return; }
        DialogResult = DialogResult.OK;
        Close();
    }
}

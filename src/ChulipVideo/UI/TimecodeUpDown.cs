using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ChulipVideo.UI;

/// <summary>値を秒数で保持し、24時間を超える時刻も時分秒で編集する入力欄。</summary>
public sealed class TimecodeUpDown : NumericUpDown
{
    public TimecodeUpDown()
    {
        Minimum = 0;
        Maximum = 864000;
        DecimalPlaces = 3;
        Increment = 0.1m;
        TextAlign = HorizontalAlignment.Center;
        Width = 150;
        UpdateEditText();
    }

    [AllowNull]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public override string Text
    {
        get => base.Text;
        set
        {
            base.Text = value;
            if (UserEdit) CommitEdit();
        }
    }

    public static string FormatTimecode(decimal seconds)
    {
        long milliseconds = (long)decimal.Round(seconds * 1000, 0, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture,
            $"{milliseconds / 3600000:00}:{milliseconds / 60000 % 60:00}:{milliseconds / 1000 % 60:00}.{milliseconds % 1000:000}");
    }

    /// <summary>秒数、分:秒、時:分:秒を解析する。桁あふれや不正な分秒を拒否する。</summary>
    public static bool TryParseTimecode(string? text, out decimal seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split(':');
        const NumberStyles secondsStyle = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign;
        if (parts.Length == 1)
            return decimal.TryParse(parts[0], secondsStyle, CultureInfo.InvariantCulture, out seconds);
        if (parts.Length is not (2 or 3)) return false;
        if (!decimal.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal last) || last < 0 || last >= 60)
            return false;
        try
        {
            decimal result = last;
            for (int index = 0; index < parts.Length - 1; index++)
            {
                if (parts[index].Length == 0 || !parts[index].All(char.IsAsciiDigit) ||
                    !decimal.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out decimal component)) return false;
                if (parts.Length == 3 && index == 1 && component >= 60) return false;
                result += component * (parts.Length == 3 && index == 0 ? 3600 : 60);
            }
            seconds = result;
            return true;
        }
        catch (OverflowException) { return false; }
    }

    /// <summary>入力を確定し、無効な入力は直前の値に戻す。有効な範囲外入力は上下限へ制限する。</summary>
    public bool CommitEdit()
    {
        // 表示だけを丸めた秒数を、未編集の確定操作で切り捨てない。
        if (!UserEdit) { UpdateEditText(); return true; }
        string pending = Text;
        UserEdit = false;
        bool valid = TryParseTimecode(pending, out decimal seconds);
        if (valid) Value = Math.Clamp(seconds, Minimum, Maximum);
        UpdateEditText();
        return valid;
    }

    // Valueの参照だけでこのメソッドが呼ばれるため、未確定入力は触らない。
    // 確定はEnter、フォーカス離脱、上下ボタン、Text設定で明示的に行う。
    protected override void ValidateEditText() { }

    protected override void UpdateEditText()
    {
        if (UserEdit) { CommitEdit(); return; }
        ChangingText = true;
        try { Text = FormatTimecode(Value); }
        finally { ChangingText = false; }
    }

    protected override void OnValueChanged(EventArgs e)
    {
        // 外部から更新された値を、更新前の編集中テキストで上書きしない。
        UserEdit = false;
        base.OnValueChanged(e);
    }

    public override void UpButton()
    {
        if (UserEdit) CommitEdit();
        base.UpButton();
    }

    public override void DownButton()
    {
        if (UserEdit) CommitEdit();
        base.DownButton();
    }

    protected override void OnTextBoxKeyPress(object? source, KeyPressEventArgs e)
    {
        // NumericUpDownの数値専用フィルターを通さず、区切り文字と通常の編集キーを受け付ける。
        OnKeyPress(e);
        if (!char.IsControl(e.KeyChar) && !char.IsAsciiDigit(e.KeyChar) && e.KeyChar is not (':' or '.' or '-' or '+') &&
            (ModifierKeys & (Keys.Control | Keys.Alt)) == 0) e.Handled = true;
    }

    protected override void OnTextBoxKeyDown(object? source, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            CommitEdit();
            e.Handled = true;
            e.SuppressKeyPress = true;
            OnKeyDown(e);
            return;
        }
        base.OnTextBoxKeyDown(source, e);
    }

    protected override void OnTextBoxLostFocus(object? source, EventArgs e)
    {
        CommitEdit();
        base.OnTextBoxLostFocus(source, e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        CommitEdit();
        base.OnLostFocus(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Enter) { CommitEdit(); return true; }
        if (keyData == (Keys.Control | Keys.A)) { Select(0, Text.Length); return true; }
        // 親画面の移動・再生ショートカットへ渡さず、内部のTextBoxに編集を任せる。
        var key = keyData & Keys.KeyCode;
        if ((keyData & Keys.Alt) == 0 &&
            (key is Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.Up or Keys.Down or Keys.Back or Keys.Delete or Keys.Space ||
             (keyData & Keys.Control) != 0 && key is Keys.C or Keys.X or Keys.V or Keys.Z or Keys.Y)) return false;
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

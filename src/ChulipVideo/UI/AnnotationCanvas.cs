using System.ComponentModel;
using ChulipVideo.Core;
using ChulipVideo.Export;

namespace ChulipVideo.UI;

public enum AnnotationTool { Select, Box, Arrow, Text, Badge, Spotlight }

/// <summary>手順の画像に注釈を重ねて表示し、選択・移動・追加・削除する。画像は所有せず、呼び出し側が破棄する。</summary>
public sealed class AnnotationCanvas : Control
{
    private enum Drag { None, Move, Create }
    private Drag drag;
    private Point start;
    private Annotation? original, draft;
    private bool moved;
    private RectangleF target;
    private float scale = 1;

    public Bitmap? Image { get; private set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public List<Annotation>? Annotations { get; private set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Annotation? Selected { get; private set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public AnnotationTool Tool { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public int StepNumber { get; private set; }
    /// <summary>画像が表す元動画の範囲（元動画のピクセル）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public CropRect Area { get; private set; }
    /// <summary>文字の注釈を置くときに入力を求める。nullや空文字なら置かない。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Func<string?>? RequestText { get; set; }
    public event EventHandler? AnnotationsChanged;
    /// <summary>作成の道具を1回使い終えて、選択に戻ったとき。</summary>
    public event EventHandler? ToolCompleted;

    public AnnotationCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = Color.FromArgb(120, 120, 120);
    }

    public void SetStep(Bitmap? image, List<Annotation>? annotations, int number, CropRect area)
    {
        Image = image; Annotations = annotations; StepNumber = number; Area = area;
        if (Selected is not null && annotations?.Contains(Selected) != true) Selected = null;
        drag = Drag.None; draft = null;
        UpdateLayout();
        Invalidate();
    }

    public void DeleteSelected()
    {
        if (Selected is null || Annotations is null) return;
        Annotations.Remove(Selected);
        Selected = null;
        Invalidate();
        AnnotationsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateLayout()
    {
        if (Image is null || Width <= 8 || Height <= 8) { scale = 1; target = RectangleF.Empty; return; }
        scale = Math.Min((Width - 8f) / Image.Width, (Height - 8f) / Image.Height);
        target = new RectangleF((Width - Image.Width * scale) / 2, (Height - Image.Height * scale) / 2, Image.Width * scale, Image.Height * scale);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateLayout(); }

    private PointF Offset => new(-Area.X, -Area.Y);
    private Point ToSource(Point client) => new((int)Math.Round(Area.X + (client.X - target.X) / scale), (int)Math.Round(Area.Y + (client.Y - target.Y) / scale));

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
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        g.DrawImage(Image, target);
        g.SetClip(target);
        g.TranslateTransform(target.X, target.Y);
        var shown = (Annotations ?? []).Concat(draft is null ? [] : [draft]);
        AnnotationRenderer.Draw(g, shown, Offset, scale, StepNumber, Selected);
    }

    /// <summary>上に描いた（後の）注釈を優先して、クライアント座標の位置にある注釈を返す。</summary>
    public Annotation? HitTest(Point client)
    {
        if (Annotations is null) return null;
        var local = new PointF(client.X - target.X, client.Y - target.Y);
        for (int i = Annotations.Count - 1; i >= 0; i--)
        {
            var bounds = AnnotationRenderer.Bounds(Annotations[i], Offset, scale);
            bounds.Inflate(6, 6);
            if (bounds.Contains(local)) return Annotations[i];
        }
        return null;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (Image is null || Annotations is null || e.Button != MouseButtons.Left) return;
        var source = ToSource(e.Location);
        switch (Tool)
        {
            case AnnotationTool.Select:
                Selected = HitTest(e.Location);
                if (Selected is not null) { drag = Drag.Move; start = source; original = Selected with { }; moved = false; Capture = true; }
                Invalidate();
                break;
            case AnnotationTool.Badge:
                Add(new Annotation { Kind = AnnotationKind.Badge, X = source.X, Y = source.Y });
                break;
            case AnnotationTool.Text:
                if (RequestText?.Invoke() is { Length: > 0 } text) Add(new Annotation { Kind = AnnotationKind.Text, X = source.X, Y = source.Y, Text = text });
                else Complete();
                break;
            default:
                var kind = Tool switch { AnnotationTool.Arrow => AnnotationKind.Arrow, AnnotationTool.Spotlight => AnnotationKind.Spotlight, _ => AnnotationKind.Box };
                draft = new Annotation { Kind = kind, X = source.X, Y = source.Y, EndX = source.X, EndY = source.Y };
                start = source; drag = Drag.Create; Capture = true;
                break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (drag == Drag.None) return;
        var source = ToSource(e.Location);
        int dx = source.X - start.X, dy = source.Y - start.Y;
        if (drag == Drag.Move && Selected is not null && original is not null)
        {
            Selected.X = original.X + dx; Selected.Y = original.Y + dy;
            Selected.EndX = original.EndX + dx; Selected.EndY = original.EndY + dy;
            moved |= dx != 0 || dy != 0;
        }
        else if (drag == Drag.Create && draft is not null)
        {
            if (draft.Kind == AnnotationKind.Arrow) { draft.EndX = source.X; draft.EndY = source.Y; }
            else
            {
                draft.X = Math.Min(start.X, source.X); draft.Y = Math.Min(start.Y, source.Y);
                draft.Width = Math.Abs(dx); draft.Height = Math.Abs(dy);
            }
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var finished = drag;
        drag = Drag.None;
        var created = draft;
        draft = null;
        Capture = false;
        if (finished == Drag.Move && moved) AnnotationsChanged?.Invoke(this, EventArgs.Empty);
        if (finished != Drag.Create || created is null) { Invalidate(); return; }
        // 小さすぎる枠・短すぎる矢印は、クリックの誤操作として捨てる。
        bool large = created.Kind == AnnotationKind.Arrow
            ? Math.Abs(created.EndX - created.X) + Math.Abs(created.EndY - created.Y) >= 6
            : created.Width >= 4 && created.Height >= 4;
        if (large) Add(created); else Complete();
    }

    private void Add(Annotation annotation)
    {
        Annotations?.Add(annotation);
        Selected = annotation;
        AnnotationsChanged?.Invoke(this, EventArgs.Empty);
        Complete();
    }

    private void Complete()
    {
        Tool = AnnotationTool.Select;
        Invalidate();
        ToolCompleted?.Invoke(this, EventArgs.Empty);
    }
}

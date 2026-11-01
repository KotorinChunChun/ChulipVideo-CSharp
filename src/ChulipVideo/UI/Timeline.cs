using System.ComponentModel;
namespace ChulipVideo.UI;

public sealed class Timeline : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public double Duration {get;set;}
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public double Start {get;set;}
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public double End {get;set;}
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public double Position {get;set;}
    /// <summary>下端に帯で示すマスクの時間帯（開始・終了の秒）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public IReadOnlyList<(double Start, double End)> MaskRanges {get;set;} = [];
    /// <summary>カット・早送りされる無操作区間（開始・終了の秒）。線の上に斜線で示す。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public IReadOnlyList<(double Start, double End)> IdleRanges {get;set;} = [];
    public event Action<double>? Seek;
    public event Action<double>? StartChanged;
    public event Action<double>? EndChanged;
    private int drag;
    public Timeline() { DoubleBuffered=true; Height=40; Cursor=Cursors.Hand; AccessibleName="タイムライン"; }
    private float X(double t) => 14+(float)(t/Math.Max(Duration,0.001)*(Width-28));
    private double Time(int x) => Math.Clamp((x-14d)/Math.Max(1,Width-28)*Duration,0,Duration);
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g=e.Graphics;
        g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var gray=new Pen(Color.LightGray,5); using var selected=new Pen(Color.FromArgb(88,156,193),5);
        g.DrawLine(gray,14,20,Width-14,20); g.DrawLine(selected,X(Start),20,X(End),20);
        g.FillPolygon(Brushes.ForestGreen,[new PointF(X(Start)-6,4),new PointF(X(Start)+6,4),new PointF(X(Start),15)]);
        g.FillPolygon(Brushes.IndianRed,[new PointF(X(End)-6,36),new PointF(X(End)+6,36),new PointF(X(End),25)]);
        using(var mask=new SolidBrush(Color.FromArgb(0,160,190)))
            foreach(var (s,t) in MaskRanges) g.FillRectangle(mask,X(s),Height-3,Math.Max(2,X(t)-X(s)),3);
        using(var hatch=new System.Drawing.Drawing2D.HatchBrush(System.Drawing.Drawing2D.HatchStyle.WideUpwardDiagonal,Color.FromArgb(150,80,80,80),Color.Transparent))
            foreach(var (s,t) in IdleRanges) g.FillRectangle(hatch,X(s),15,Math.Max(2,X(t)-X(s)),10);
        g.FillEllipse(Brushes.White,X(Position)-6,14,12,12);
        g.DrawEllipse(Pens.Black,X(Position)-6,14,12,12);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); if(Duration<=0 || e.Button!=MouseButtons.Left)return;
        drag=Math.Abs(e.X-X(Start))<10 && e.Y<16?1:Math.Abs(e.X-X(End))<10 && e.Y>24?2:3;
        Capture=true; UpdateDrag(e.X);
    }
    private void UpdateDrag(int x)
    { var t=Time(x); if(drag==1)StartChanged?.Invoke(t); else if(drag==2)EndChanged?.Invoke(t); else if(drag==3)Seek?.Invoke(t); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if(Capture)UpdateDrag(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); Capture=false; drag=0; }
}

using System.Drawing.Drawing2D;
using ChulipVideo.Core;

namespace ChulipVideo.UI;

/// <summary>元動画のピクセル座標で切り出し枠を編集するプレビュー。</summary>
public sealed class VideoCanvas : Control
{
    private Bitmap? frame;
    private Size videoSize;
    private CropRect crop = new(0, 0, 1, 1);
    private bool allowOutside;
    private bool moveLocked, sizeLocked;
    private string cropColor = "#FF8C00", canvasColor = "#787878";
    private double zoom = 1;
    private PointF pan;
    private DragMode drag;
    private Point mouseStart;
    private PointF videoStart, panStart;
    private CropRect cropStart;
    private int edgeX, edgeY;
    private MouseButtons dragButton;
    private Point? hoverPosition;

    private enum DragMode { None, Create, Move, Resize, Pan }

    public VideoCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
            ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleName = "動画プレビューと切り出し枠";
        AccessibleDescription = "左ドラッグで切り出し枠を作成、枠内または右ドラッグで移動、ハンドルでサイズ変更。ホイールで拡大、中ドラッグで表示移動、中ダブルクリックで全体表示。";
    }

    /// <summary>設定した画像の所有権を受け取り、置換時と破棄時に解放する。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Bitmap? Frame
    {
        get => frame;
        set
        {
            if (ReferenceEquals(frame, value)) return;
            var previous = frame;
            frame = value;
            previous?.Dispose();
            Invalidate();
        }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Size VideoSize
    {
        get => videoSize;
        set
        {
            if (videoSize == value) return;
            videoSize = new Size(Math.Max(0, value.Width), Math.Max(0, value.Height));
            Crop = crop;
            ResetView();
        }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public CropRect Crop
    {
        get => crop;
        set
        {
            var next = HasVideo ? value.Clamp(videoSize.Width, videoSize.Height, allowOutside) : value;
            if (next == crop) return;
            crop = next;
            Invalidate();
            CropChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AllowOutside
    {
        get => allowOutside;
        set { if (allowOutside == value) return; allowOutside = value; Crop = crop; Invalidate(); }
    }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool MoveLocked { get => moveLocked; set { moveLocked = value; Invalidate(); } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool SizeLocked { get => sizeLocked; set { sizeLocked = value; Invalidate(); } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string CropColor { get => cropColor; set { cropColor = value; Invalidate(); } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string CanvasColor { get => canvasColor; set { canvasColor = value; Invalidate(); } }
    public double Zoom => zoom;
    public event EventHandler? CropChanged;
    public event EventHandler? CropCommitted;
    public event EventHandler? ViewChanged;

    private bool HasVideo => videoSize.Width > 0 && videoSize.Height > 0;
    private float HandleRadius => 4 * DeviceDpi / 96f;
    private double FrameScale => HasVideo ? Math.Max(0.0001, Math.Min(
        Math.Max(1, ClientSize.Width - 32) / (double)videoSize.Width,
        Math.Max(1, ClientSize.Height - 32) / (double)videoSize.Height) * zoom) : 1;
    private PointF Origin => new((float)((ClientSize.Width - videoSize.Width * FrameScale) / 2 + pan.X),
        (float)((ClientSize.Height - videoSize.Height * FrameScale) / 2 + pan.Y));

    private PointF ToVideo(Point client)
    {
        var origin = Origin;
        return new((float)((client.X - origin.X) / FrameScale), (float)((client.Y - origin.Y) / FrameScale));
    }

    private RectangleF ToClient(CropRect rect)
    {
        var origin = Origin;
        return new((float)(origin.X + rect.X * FrameScale), (float)(origin.Y + rect.Y * FrameScale),
            (float)(rect.Width * FrameScale), (float)(rect.Height * FrameScale));
    }

    public void ResetView()
    {
        zoom = 1;
        pan = PointF.Empty;
        Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(ParseColor(canvasColor, Color.FromArgb(120, 120, 120)));
        if (!HasVideo)
        {
            TextRenderer.DrawText(g, "動画を開くか、ここへドロップしてください", Font, ClientRectangle,
                Color.Gainsboro, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var imageRect = ToClient(new CropRect(0, 0, videoSize.Width, videoSize.Height));
        var cropRect = ToClient(crop);
        // 動画外の選択部分は、出力時と同じ黒で表示する。
        g.FillRectangle(Brushes.Black, cropRect);
        g.FillRectangle(Brushes.Black, imageRect);
        if (frame is not null)
        {
            g.InterpolationMode = FrameScale >= 2 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(frame, imageRect, new RectangleF(0, 0, frame.Width, frame.Height), GraphicsUnit.Pixel);
        }
        using (var region = new Region(imageRect))
        using (var shade = new SolidBrush(Color.FromArgb(105, Color.Black)))
        {
            region.Exclude(cropRect);
            g.FillRegion(shade, region);
        }
        using var framePen = new Pen(Color.FromArgb(130, Color.White));
        g.DrawRectangle(framePen, imageRect.X, imageRect.Y, imageRect.Width, imageRect.Height);
        var color = ParseColor(cropColor, Color.DarkOrange);
        bool active = Focused || drag is DragMode.Create or DragMode.Move or DragMode.Resize;
        // 独自の保存色は維持し、既定配色だけ原型と同じ赤で注目状態を示す。
        if ((active || IsCropHovered) && color.ToArgb() == Color.DarkOrange.ToArgb()) color = Color.Red;
        using var cropPen = new Pen(color, 2) { DashStyle = active ? DashStyle.Solid : DashStyle.Dash };
        g.DrawRectangle(cropPen, cropRect.X, cropRect.Y, cropRect.Width, cropRect.Height);
        if (!SizeLocked)
        {
            using var handlePen = new Pen(color);
            foreach (var (_, _, point) in Handles(cropRect))
            {
                var r = new RectangleF(point.X - HandleRadius, point.Y - HandleRadius, HandleRadius * 2, HandleRadius * 2);
                g.FillRectangle(Brushes.White, r);
                g.DrawRectangle(handlePen, r.X, r.Y, r.Width, r.Height);
            }
        }
        string caption = $"{crop.Width} × {crop.Height} px  ({crop.X}, {crop.Y})";
        var label = TextRenderer.MeasureText(caption, Font);
        int lx = Math.Clamp((int)cropRect.Left, 0, Math.Max(0, Width - label.Width));
        int ly = Math.Clamp((int)cropRect.Top - label.Height - 3, 0, Math.Max(0, Height - label.Height));
        TextRenderer.DrawText(g, caption, Font, new Point(lx, ly), Color.White, Color.FromArgb(32, 36, 43));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (!HasVideo || drag != DragMode.None) return;
        mouseStart = e.Location;
        videoStart = ToVideo(e.Location);
        cropStart = crop;
        panStart = pan;
        dragButton = e.Button;
        if (e.Button == MouseButtons.Middle) drag = DragMode.Pan;
        else if (e.Button == MouseButtons.Right && !MoveLocked) drag = DragMode.Move;
        else if (e.Button == MouseButtons.Left)
        {
            (edgeX, edgeY) = HitHandle(e.Location);
            if (!SizeLocked && (edgeX != 0 || edgeY != 0)) drag = DragMode.Resize;
            else if (ToClient(crop).Contains(e.Location) && !MoveLocked) drag = DragMode.Move;
            else if (!SizeLocked && !MoveLocked && !ToClient(crop).Contains(e.Location)) drag = DragMode.Create;
        }
        if (drag != DragMode.None) Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool wasHovered = IsCropHovered;
        hoverPosition = e.Location;
        if (wasHovered != IsCropHovered) Invalidate();
        if (!HasVideo) return;
        if (drag == DragMode.None) { UpdateCursor(e.Location); return; }
        if (drag == DragMode.Pan)
        {
            pan = new(panStart.X + e.X - mouseStart.X, panStart.Y + e.Y - mouseStart.Y);
            Invalidate();
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        var position = ToVideo(e.Location);
        bool shift = ModifierKeys.HasFlag(Keys.Shift), center = ModifierKeys.HasFlag(Keys.Control);
        int step = ModifierKeys.HasFlag(Keys.Alt) ? 10 : 1;
        int dx = Snap(position.X - videoStart.X, step), dy = Snap(position.Y - videoStart.Y, step);
        switch (drag)
        {
            case DragMode.Move:
                if (shift) { if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0; else dx = 0; }
                Crop = cropStart with { X = cropStart.X + dx, Y = cropStart.Y + dy };
                break;
            case DragMode.Create:
                if (shift)
                {
                    int side = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    dx = dx < 0 ? -side : side; dy = dy < 0 ? -side : side;
                }
                double x1 = Snap(videoStart.X, step), y1 = Snap(videoStart.Y, step);
                double x2 = x1 + dx, y2 = y1 + dy;
                if (!allowOutside)
                {
                    x1 = Math.Clamp(x1, 0, videoSize.Width); x2 = Math.Clamp(x2, 0, videoSize.Width);
                    y1 = Math.Clamp(y1, 0, videoSize.Height); y2 = Math.Clamp(y2, 0, videoSize.Height);
                }
                Crop = new((int)Math.Min(x1, x2), (int)Math.Min(y1, y2),
                    Math.Max(1, (int)Math.Abs(x2 - x1)), Math.Max(1, (int)Math.Abs(y2 - y1)));
                break;
            case DragMode.Resize:
                Crop = ResizeCrop(dx, dy, shift, center);
                break;
        }
    }

    private bool IsCropHovered
    {
        get
        {
            if (!HasVideo || hoverPosition is not Point point) return false;
            var bounds = ToClient(crop);
            bounds.Inflate(HandleRadius + 4, HandleRadius + 4);
            return bounds.Contains(point);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hoverPosition = null;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    private CropRect ResizeCrop(int dx, int dy, bool keepRatio, bool centered)
    {
        double w = Math.Max(1, cropStart.Width + dx * edgeX * (centered ? 2 : 1));
        double h = Math.Max(1, cropStart.Height + dy * edgeY * (centered ? 2 : 1));
        double ratio = cropStart.Width / (double)Math.Max(1, cropStart.Height);
        if (keepRatio)
        {
            if (edgeX == 0 || edgeY != 0 && Math.Abs(dy / (double)cropStart.Height) >= Math.Abs(dx / (double)cropStart.Width)) w = h * ratio;
            else h = w / ratio;
        }
        double cx = cropStart.X + cropStart.Width / 2d, cy = cropStart.Y + cropStart.Height / 2d;
        double maxW = 16384, maxH = 16384;
        if (!allowOutside)
        {
            maxW = centered || edgeX == 0 ? Math.Min(cx, videoSize.Width - cx) * 2 :
                edgeX < 0 ? cropStart.X + cropStart.Width : videoSize.Width - cropStart.X;
            maxH = centered || edgeY == 0 ? Math.Min(cy, videoSize.Height - cy) * 2 :
                edgeY < 0 ? cropStart.Y + cropStart.Height : videoSize.Height - cropStart.Y;
        }
        if (keepRatio)
        {
            double FrameScale = Math.Min(1, Math.Min(maxW / w, maxH / h));
            w *= FrameScale; h *= FrameScale;
        }
        else { w = Math.Min(w, maxW); h = Math.Min(h, maxH); }
        int width = Math.Max(1, (int)Math.Round(w)), height = Math.Max(1, (int)Math.Round(h));
        int x = (int)Math.Round(centered || edgeX == 0 ? cx - width / 2d : edgeX < 0 ? cropStart.X + cropStart.Width - width : cropStart.X);
        int y = (int)Math.Round(centered || edgeY == 0 ? cy - height / 2d : edgeY < 0 ? cropStart.Y + cropStart.Height - height : cropStart.Y);
        return new CropRect(x, y, width, height).Clamp(videoSize.Width, videoSize.Height, allowOutside);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == dragButton) CompleteDrag();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) CompleteDrag();
    }

    private void CompleteDrag()
    {
        var previous = drag;
        drag = DragMode.None;
        Capture = false;
        if (previous is not DragMode.None and not DragMode.Pan && crop != cropStart)
            CropCommitted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && drag != DragMode.None)
        {
            var previous = drag;
            drag = DragMode.None;
            Capture = false;
            if (previous == DragMode.Pan) { pan = panStart; Invalidate(); ViewChanged?.Invoke(this, EventArgs.Empty); }
            else Crop = cropStart;
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        base.OnKeyDown(e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        drag != DragMode.None && (keyData & Keys.KeyCode) == Keys.Escape || base.IsInputKey(keyData);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!HasVideo || drag != DragMode.None) return;
        var position = ToVideo(e.Location);
        zoom = Math.Clamp(zoom * Math.Pow(1.2, e.Delta / 120d), 0.1, 20);
        var origin = Origin;
        pan = new(pan.X + e.X - (float)(origin.X + position.X * FrameScale),
            pan.Y + e.Y - (float)(origin.Y + position.Y * FrameScale));
        Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Middle) { drag = DragMode.None; Capture = false; ResetView(); }
    }

    private (int X, int Y) HitHandle(Point location)
    {
        float radius = HandleRadius + 4;
        foreach (var (x, y, point) in Handles(ToClient(crop)))
            if (Math.Abs(location.X - point.X) <= radius && Math.Abs(location.Y - point.Y) <= radius) return (x, y);
        return (0, 0);
    }

    private static IEnumerable<(int X, int Y, PointF Point)> Handles(RectangleF r)
    {
        // 四隅を先に判定して、縮小表示の小さな枠でも角をつかめるようにする。
        yield return (-1, -1, new(r.Left, r.Top)); yield return (1, -1, new(r.Right, r.Top));
        yield return (-1, 1, new(r.Left, r.Bottom)); yield return (1, 1, new(r.Right, r.Bottom));
        yield return (0, -1, new(r.Left + r.Width / 2, r.Top)); yield return (0, 1, new(r.Left + r.Width / 2, r.Bottom));
        yield return (-1, 0, new(r.Left, r.Top + r.Height / 2)); yield return (1, 0, new(r.Right, r.Top + r.Height / 2));
    }

    private void UpdateCursor(Point point)
    {
        var (x, y) = SizeLocked ? (0, 0) : HitHandle(point);
        Cursor = (x, y) switch
        {
            (-1, -1) or (1, 1) => Cursors.SizeNWSE,
            (-1, 1) or (1, -1) => Cursors.SizeNESW,
            (0, -1) or (0, 1) => Cursors.SizeNS,
            (-1, 0) or (1, 0) => Cursors.SizeWE,
            _ => !MoveLocked && ToClient(crop).Contains(point) ? Cursors.SizeAll : Cursors.Cross
        };
    }

    private static int Snap(double value, int step) => (int)Math.Round(value / step) * step;
    private static Color ParseColor(string value, Color fallback) => ColorText.Parse(value, fallback);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { frame?.Dispose(); frame = null; }
        base.Dispose(disposing);
    }
}


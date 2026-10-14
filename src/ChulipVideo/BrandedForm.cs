namespace ChulipVideo;

public class BrandedForm : Form
{
    private readonly Icon productIcon;
    public BrandedForm()
    {
        using var stream = typeof(BrandedForm).Assembly.GetManifestResourceStream("ChulipVideo.assets.ChulipVideo.ico")!;
        productIcon = new Icon(stream);
        Icon = productIcon;
        Font = new Font("Yu Gothic UI", 9F);
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) productIcon.Dispose();
    }
}

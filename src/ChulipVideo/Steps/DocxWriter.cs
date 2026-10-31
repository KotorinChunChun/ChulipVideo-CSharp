using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace ChulipVideo.Steps;

/// <summary>
/// 外部パッケージを使わず、Wordで開ける最小構成のOOXML（.docx）を作る。
/// 部品は [Content_Types].xml、_rels/.rels、word/document.xml、word/_rels/document.xml.rels、word/media/*.png。
/// </summary>
public static class DocxWriter
{
    /// <summary>画像の表示幅16cm（EMU）。A4で左右2cmの余白に収まる。</summary>
    public const long ImageWidthEmu = 16L * 360000;
    private const string Font = "<w:rFonts w:ascii=\"Yu Gothic\" w:eastAsia=\"Yu Gothic\" w:hAnsi=\"Yu Gothic\"/>";

    public static void Write(string path, StepDocumentWriter.DocumentInfo info, IReadOnlyList<StepPage> pages, string imageRoot)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        Entry(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Default Extension=\"png\" ContentType=\"image/png\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "</Types>");
        Entry(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "</Relationships>");
        var relations = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        var body = new StringBuilder();
        body.Append(Paragraph(info.Title, 36, bold: true));
        body.Append(Paragraph($"元動画: {info.VideoFile}　作成日: {info.Created:yyyy-MM-dd}　手順数: {pages.Count}", 18, color: "666666"));
        foreach (var page in pages)
        {
            string id = "rIdImage" + page.Number.ToString(CultureInfo.InvariantCulture);
            string media = "step_" + page.Number.ToString("D3", CultureInfo.InvariantCulture) + ".png";
            zip.CreateEntryFromFile(Path.Combine(imageRoot, page.ImageFile.Replace('/', Path.DirectorySeparatorChar)), "word/media/" + media, CompressionLevel.NoCompression);
            relations.Append("<Relationship Id=\"").Append(id).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/").Append(media).Append("\"/>");
            body.Append(Paragraph("手順 " + page.Number.ToString(CultureInfo.InvariantCulture), 28, bold: true, keepNext: true));
            if (page.Description.Length > 0) body.Append(Paragraph(page.Description, 22, keepNext: true));
            body.Append(Image(id, page.Number, media, page.Width, page.Height));
        }
        Entry(zip, "word/_rels/document.xml.rels", relations.Append("</Relationships>").ToString());
        Entry(zip, "word/document.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"" +
            " xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"" +
            " xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\"" +
            " xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"" +
            " xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><w:body>" + body +
            "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1134\" w:header=\"567\" w:footer=\"567\" w:gutter=\"0\"/></w:sectPr>" +
            "</w:body></w:document>");
    }

    private static void Entry(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>sizeは半ポイント単位。改行は同じ段落内の改行にする。</summary>
    private static string Paragraph(string text, int size, bool bold = false, string? color = null, bool keepNext = false)
    {
        var p = new StringBuilder("<w:p>");
        if (keepNext) p.Append("<w:pPr><w:keepNext/></w:pPr>");
        string properties = "<w:rPr>" + Font + (bold ? "<w:b/>" : "") + (color is null ? "" : $"<w:color w:val=\"{color}\"/>") + $"<w:sz w:val=\"{size}\"/><w:szCs w:val=\"{size}\"/></w:rPr>";
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            p.Append("<w:r>").Append(properties);
            if (i > 0) p.Append("<w:br/>");
            p.Append("<w:t xml:space=\"preserve\">").Append(SecurityElement.Escape(lines[i])).Append("</w:t></w:r>");
        }
        return p.Append("</w:p>").ToString();
    }

    private static string Image(string relation, int number, string name, int width, int height)
    {
        long cx = ImageWidthEmu, cy = (long)Math.Round(ImageWidthEmu * (double)height / Math.Max(1, width));
        string n = number.ToString(CultureInfo.InvariantCulture), x = cx.ToString(CultureInfo.InvariantCulture), y = cy.ToString(CultureInfo.InvariantCulture);
        return "<w:p><w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">" +
            $"<wp:extent cx=\"{x}\" cy=\"{y}\"/><wp:docPr id=\"{n}\" name=\"手順 {n}\"/>" +
            "<wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect=\"1\"/></wp:cNvGraphicFramePr>" +
            "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic>" +
            $"<pic:nvPicPr><pic:cNvPr id=\"{n}\" name=\"{name}\"/><pic:cNvPicPr/></pic:nvPicPr>" +
            $"<pic:blipFill><a:blip r:embed=\"{relation}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
            $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{x}\" cy=\"{y}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>" +
            "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>";
    }
}

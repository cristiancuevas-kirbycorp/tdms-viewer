using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using TdmsViewer.Models;
using TdmsViewer.ViewModels;

namespace TdmsViewer.Services;

/// <summary>Builds a PDF of a report's graph pages with the report's header/footer bands.</summary>
public sealed class ReportPdfService
{
    // Composed page bitmap size (~150 DPI Letter landscape).
    private const int PageW = 1650;
    private const int PageH = 1275;

    /// <param name="renderPlot">Renders a page's plot to a PNG at the requested pixel size.</param>
    public void Build(
        ReportViewModel report,
        IReadOnlyList<PageViewModel> pages,
        string tdmsFileName,
        string outputPath,
        Func<PageViewModel, int, int, byte[]?> renderPlot)
    {
        var doc = new PdfDocument();
        var temps = new List<string>();
        try
        {
            for (var i = 0; i < pages.Count; i++)
            {
                var png = ComposePage(report, pages[i], i, pages.Count, tdmsFileName, renderPlot);
                var tmp = Path.Combine(Path.GetTempPath(), $"tvpdf_{Guid.NewGuid():N}.png");
                File.WriteAllBytes(tmp, png);
                temps.Add(tmp);

                var page = doc.AddPage();
                page.Size = PageSize.Letter;
                page.Orientation = PageOrientation.Landscape;
                using var gfx = XGraphics.FromPdfPage(page);
                var img = XImage.FromFile(tmp);
                gfx.DrawImage(img, 0, 0, page.Width.Point, page.Height.Point);
            }
            doc.Save(outputPath);
        }
        finally
        {
            foreach (var t in temps)
                try { File.Delete(t); } catch { /* temp cleanup is best-effort */ }
        }
    }

    private static byte[] ComposePage(
        ReportViewModel report, PageViewModel page, int index, int count, string tdmsFileName,
        Func<PageViewModel, int, int, byte[]?> renderPlot)
    {
        var margin = (int)(PageW * 0.045);
        var headerH = (int)(PageH * 0.06);
        var footerH = (int)(PageH * 0.05);
        var gap = (int)(PageH * 0.015);

        var plotX = margin;
        var plotY = margin + headerH + gap;
        var plotW = PageW - 2 * margin;
        var plotH = PageH - plotY - footerH - gap - margin;

        var plotPng = renderPlot(page, plotW, plotH);

        using var bmp = new Bitmap(PageW, PageH);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.White);

        using var font = new Font("Segoe UI", PageH * 0.013f);
        using var divider = new Pen(Color.FromArgb(215, 215, 215));

        var headerRect = new RectangleF(margin, margin, PageW - 2 * margin, headerH);
        DrawBand(g, report, headerRect, report.HeaderLeft, report.HeaderMiddle, report.HeaderRight, font, index, count, tdmsFileName);
        g.DrawLine(divider, margin, margin + headerH, PageW - margin, margin + headerH);

        if (plotPng is not null)
        {
            using var ms = new MemoryStream(plotPng);
            using var img = Image.FromStream(ms);
            g.DrawImage(img, plotX, plotY, plotW, plotH);
        }

        g.DrawLine(divider, margin, PageH - margin - footerH, PageW - margin, PageH - margin - footerH);
        var footerRect = new RectangleF(margin, PageH - margin - footerH, PageW - 2 * margin, footerH);
        DrawBand(g, report, footerRect, report.FooterLeft, report.FooterMiddle, report.FooterRight, font, index, count, tdmsFileName);

        using var outMs = new MemoryStream();
        bmp.Save(outMs, ImageFormat.Png);
        return outMs.ToArray();
    }

    private static void DrawBand(
        Graphics g, ReportViewModel report, RectangleF rect,
        ReportSlot left, ReportSlot mid, ReportSlot right, Font font, int index, int count, string tdmsFileName)
    {
        DrawSlot(g, report, rect, left, StringAlignment.Near, font, index, count, tdmsFileName);
        DrawSlot(g, report, rect, mid, StringAlignment.Center, font, index, count, tdmsFileName);
        DrawSlot(g, report, rect, right, StringAlignment.Far, font, index, count, tdmsFileName);
    }

    private static void DrawSlot(
        Graphics g, ReportViewModel report, RectangleF rect, ReportSlot slot, StringAlignment align,
        Font font, int index, int count, string tdmsFileName)
    {
        if (slot == ReportSlot.None) return;

        if (slot == ReportSlot.CustomImage)
        {
            if (string.IsNullOrWhiteSpace(report.CustomImagePath) || !File.Exists(report.CustomImagePath)) return;
            try
            {
                using var img = Image.FromFile(report.CustomImagePath);
                var h = rect.Height;
                var w = img.Width * (h / img.Height);
                var x = align switch
                {
                    StringAlignment.Near => rect.Left,
                    StringAlignment.Center => rect.Left + (rect.Width - w) / 2,
                    _ => rect.Right - w,
                };
                g.DrawImage(img, x, rect.Top, w, h);
            }
            catch (Exception ex) { App.Log(ex); }
            return;
        }

        var text = slot switch
        {
            ReportSlot.ReportName => report.Title,
            ReportSlot.Date => DateTime.Now.ToShortDateString(),
            ReportSlot.DateTime => DateTime.Now.ToString("g"),
            ReportSlot.PageNumber => $"Page {index + 1} of {count}",
            ReportSlot.FileName => tdmsFileName,
            ReportSlot.CustomText => report.CustomText,
            _ => string.Empty,
        };
        if (string.IsNullOrEmpty(text)) return;

        using var sf = new StringFormat { Alignment = align, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        using var brush = new SolidBrush(Color.FromArgb(45, 45, 45));
        g.DrawString(text, font, brush, rect, sf);
    }
}

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
        Func<PageViewModel, int, int, byte[]?> renderPlot,
        Func<PageViewModel, PdfCursorReadout?> cursorFor)
    {
        var doc = new PdfDocument();
        var temps = new List<string>();
        try
        {
            for (var i = 0; i < pages.Count; i++)
            {
                var png = ComposePage(report, pages[i], i, pages.Count, tdmsFileName, renderPlot, cursorFor);
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
        Func<PageViewModel, int, int, byte[]?> renderPlot,
        Func<PageViewModel, PdfCursorReadout?> cursorFor)
    {
        var margin = (int)(PageW * 0.01);
        var headerH = (int)(PageH * 0.035);
        var footerH = (int)(PageH * 0.03);
        var gap = (int)(PageH * 0.01);

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

        using var font = new Font("Segoe UI", PageH * 0.007f);
        using var divider = new Pen(Color.FromArgb(215, 215, 215));

        var headerRect = new RectangleF(margin, margin, PageW - 2 * margin, headerH);
        DrawBand(g, report, headerRect, report.HeaderLeft, report.HeaderMiddle, report.HeaderRight, font, index, count, tdmsFileName, page.Name);
        g.DrawLine(divider, margin, margin + headerH, PageW - margin, margin + headerH);

        if (plotPng is not null)
        {
            using var ms = new MemoryStream(plotPng);
            using var img = Image.FromStream(ms);
            g.DrawImage(img, plotX, plotY, plotW, plotH);
        }

        // Cursor readout overlay (when cursors are on for this page).
        if (cursorFor(page) is { } readout)
            DrawCursorReadout(g, readout, plotX, plotY, plotW, plotH);

        g.DrawLine(divider, margin, PageH - margin - footerH, PageW - margin, PageH - margin - footerH);
        var footerRect = new RectangleF(margin, PageH - margin - footerH, PageW - 2 * margin, footerH);
        DrawBand(g, report, footerRect, report.FooterLeft, report.FooterMiddle, report.FooterRight, font, index, count, tdmsFileName, page.Name);

        using var outMs = new MemoryStream();
        bmp.Save(outMs, ImageFormat.Png);
        return outMs.ToArray();
    }

    private static void DrawBand(
        Graphics g, ReportViewModel report, RectangleF rect,
        ReportSlot left, ReportSlot mid, ReportSlot right, Font font, int index, int count, string tdmsFileName, string pageName)
    {
        DrawSlot(g, report, rect, left, StringAlignment.Near, font, index, count, tdmsFileName, pageName);
        DrawSlot(g, report, rect, mid, StringAlignment.Center, font, index, count, tdmsFileName, pageName);
        DrawSlot(g, report, rect, right, StringAlignment.Far, font, index, count, tdmsFileName, pageName);
    }

    private static void DrawSlot(
        Graphics g, ReportViewModel report, RectangleF rect, ReportSlot slot, StringAlignment align,
        Font font, int index, int count, string tdmsFileName, string pageName)
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
            ReportSlot.PageName => pageName,
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

    // Draws the cursor readout table at its saved fractional position within the plot rectangle.
    private static void DrawCursorReadout(Graphics g, PdfCursorReadout r, float plotX, float plotY, float plotW, float plotH)
    {
        using var font = new Font("Segoe UI", PageH * 0.0075f);
        using var headerFont = new Font("Segoe UI", PageH * 0.0075f, FontStyle.Bold);

        var pad = PageW * 0.005f;
        var swatchW = PageW * 0.012f;
        var nameW = PageW * 0.15f;
        var colW = PageW * 0.065f;
        var rowH = font.GetHeight(g) + 3;

        using var sfRight = new StringFormat { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap };
        using var sfName = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

        var colsWidth = swatchW + nameW + r.Columns.Count * colW;
        var headerWidth = g.MeasureString(r.Header, headerFont).Width;
        var tableW = Math.Max(colsWidth, headerWidth) + pad * 2;
        var tableH = rowH * (r.Rows.Count + 2) + pad * 2;

        var x0 = plotX + (float)r.X * plotW;
        var y0 = plotY + (float)r.Y * plotH;
        x0 = Math.Clamp(x0, plotX, plotX + plotW - tableW);
        y0 = Math.Clamp(y0, plotY, plotY + plotH - tableH);

        using var bg = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
        using var border = new Pen(Color.FromArgb(200, 200, 200));
        g.FillRectangle(bg, x0, y0, tableW, tableH);
        g.DrawRectangle(border, x0, y0, tableW, tableH);

        using var text = new SolidBrush(Color.FromArgb(45, 45, 45));
        using var muted = new SolidBrush(Color.FromArgb(120, 120, 120));

        var nameX = x0 + pad + swatchW;
        var valuesX = nameX + nameW;
        var cy = y0 + pad;

        g.DrawString(r.Header, headerFont, text, x0 + pad, cy);
        cy += rowH;

        g.DrawString("Plot", font, muted, nameX, cy);
        for (var i = 0; i < r.Columns.Count; i++)
            g.DrawString(r.Columns[i], font, muted, new RectangleF(valuesX + i * colW, cy, colW, rowH), sfRight);
        cy += rowH;

        foreach (var row in r.Rows)
        {
            using (var sw = new SolidBrush(ParseColor(row.ColorHex)))
                g.FillRectangle(sw, x0 + pad, cy + rowH * 0.28f, swatchW * 0.6f, rowH * 0.45f);
            g.DrawString(row.Name, font, text, new RectangleF(nameX, cy, nameW, rowH), sfName);
            for (var i = 0; i < row.Values.Count; i++)
                g.DrawString(row.Values[i], font, text, new RectangleF(valuesX + i * colW, cy, colW, rowH), sfRight);
            cy += rowH;
        }
    }

    private static Color ParseColor(string hex)
    {
        try
        {
            var s = hex.TrimStart('#');
            return Color.FromArgb(
                Convert.ToInt32(s.Substring(0, 2), 16),
                Convert.ToInt32(s.Substring(2, 2), 16),
                Convert.ToInt32(s.Substring(4, 2), 16));
        }
        catch
        {
            return Color.Gray;
        }
    }
}

/// <summary>Cursor readout table for the printed PDF (built by the UI, drawn by the service).</summary>
public sealed record PdfCursorReadout(string Header, IReadOnlyList<string> Columns, IReadOnlyList<PdfCursorRow> Rows, double X, double Y);

public sealed record PdfCursorRow(string Name, string ColorHex, IReadOnlyList<string> Values);

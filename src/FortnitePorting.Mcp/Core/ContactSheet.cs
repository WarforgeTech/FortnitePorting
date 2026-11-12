using SkiaSharp;

namespace FortnitePorting.Mcp.Core;

public sealed record ContactSheetCell(int Index, byte[]? Png, string? Label);

/// <summary>
/// Composites icon thumbnails into one labelled grid PNG. This is what makes visual browsing
/// possible for a model: one image instead of sixty tool calls.
/// </summary>
public static class ContactSheet
{
    public const int MaxCells = 60;

    private static readonly SKColor Background = new(0x14, 0x15, 0x1A);
    private static readonly SKColor CellBackground = new(0x22, 0x24, 0x2C);
    private static readonly SKColor CellBorder = new(0x3C, 0x40, 0x4C);
    private static readonly SKColor LabelColor = SKColors.White;
    private static readonly SKColor LabelOutline = new(0x00, 0x00, 0x00, 0xE0);

    public static byte[] Render(IReadOnlyList<ContactSheetCell> cells, int cellSize = 128, int columns = 8, bool labels = true)
    {
        cellSize = Math.Clamp(cellSize, 48, 512);
        columns = Math.Clamp(columns, 1, 12);

        var count = Math.Max(1, cells.Count);
        var rows = (int) Math.Ceiling(count / (double) columns);

        var padding = Math.Max(4, cellSize / 16);
        var labelHeight = labels ? Math.Max(14, cellSize / 8) : 0;
        var cellWidth = cellSize;
        var cellHeight = cellSize + labelHeight;

        var width = columns * cellWidth + (columns + 1) * padding;
        var height = rows * cellHeight + (rows + 1) * padding;

        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(Background);

        using var cellPaint = new SKPaint { Color = CellBackground, IsAntialias = true };
        using var borderPaint = new SKPaint { Color = CellBorder, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        using var typeface = ResolveTypeface();

        var indexSize = Math.Max(11f, cellSize / 8f);
        var nameSize = Math.Max(9f, cellSize / 11f);

        using var indexFill = TextPaint(typeface, indexSize, LabelColor, false);
        using var indexStroke = TextPaint(typeface, indexSize, LabelOutline, true);
        using var nameFill = TextPaint(typeface, nameSize, new SKColor(0xD8, 0xDC, 0xE6), false);
        using var nameStroke = TextPaint(typeface, nameSize, LabelOutline, true);

        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            var column = i % columns;
            var row = i / columns;

            var x = padding + column * (cellWidth + padding);
            var y = padding + row * (cellHeight + padding);

            var imageRect = new SKRect(x, y, x + cellWidth, y + cellSize);
            canvas.DrawRect(imageRect, cellPaint);

            DrawIcon(canvas, cell.Png, imageRect, cellSize);
            canvas.DrawRect(imageRect, borderPaint);

            // Index badge, top-left, outlined so it stays readable over any artwork.
            var badge = cell.Index.ToString();
            var badgeX = x + 4;
            var badgeY = y + indexSize + 2;
            canvas.DrawText(badge, badgeX, badgeY, indexStroke);
            canvas.DrawText(badge, badgeX, badgeY, indexFill);

            if (!labels || string.IsNullOrWhiteSpace(cell.Label)) continue;

            var text = Truncate(cell.Label!, nameFill, cellWidth - 6);
            var textX = x + 3;
            var textY = y + cellSize + nameSize + 2;
            canvas.DrawText(text, textX, textY, nameStroke);
            canvas.DrawText(text, textX, textY, nameFill);
        }

        canvas.Flush();
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void DrawIcon(SKCanvas canvas, byte[]? png, SKRect rect, int cellSize)
    {
        if (png is null || png.Length == 0)
        {
            png = IconResolver.GeneratePlaceholder(cellSize);
        }

        try
        {
            using var image = SKBitmap.Decode(png);
            if (image is null) return;

            var scale = Math.Min(rect.Width / image.Width, rect.Height / image.Height);
            var drawWidth = image.Width * scale;
            var drawHeight = image.Height * scale;
            var destination = new SKRect(
                rect.MidX - drawWidth / 2f,
                rect.MidY - drawHeight / 2f,
                rect.MidX + drawWidth / 2f,
                rect.MidY + drawHeight / 2f);

            using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
            canvas.DrawBitmap(image, destination, paint);
        }
        catch
        {
            // A single unreadable thumbnail must not fail the whole sheet.
        }
    }

    private static SKPaint TextPaint(SKTypeface? typeface, float size, SKColor color, bool stroke) => new()
    {
        Typeface = typeface,
        TextSize = size,
        Color = color,
        IsAntialias = true,
        Style = stroke ? SKPaintStyle.Stroke : SKPaintStyle.Fill,
        StrokeWidth = stroke ? Math.Max(1.5f, size / 6f) : 0,
        SubpixelText = true
    };

    private static SKTypeface ResolveTypeface()
    {
        foreach (var family in new[] { "Segoe UI Semibold", "Segoe UI", "Arial", "DejaVu Sans" })
        {
            var typeface = SKTypeface.FromFamilyName(family, SKFontStyle.Bold);
            if (typeface is not null) return typeface;
        }

        return SKTypeface.Default;
    }

    private static string Truncate(string text, SKPaint paint, float maxWidth)
    {
        text = text.Replace('\n', ' ').Trim();
        if (paint.MeasureText(text) <= maxWidth) return text;

        for (var length = text.Length - 1; length > 1; length--)
        {
            var candidate = text[..length] + "…";
            if (paint.MeasureText(candidate) <= maxWidth) return candidate;
        }

        return "…";
    }
}

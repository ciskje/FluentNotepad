using Markdig;
using Windows.UI;

namespace FluentNotepad;

/// <summary>
/// Single Markdig pipeline factory + VS palette shared by the preview CSS
/// (MarkdownHtml, incl. print) and the edit-mode highlight (MarkdownHighlight).
/// </summary>
public static class MarkdownShared
{
    public static MarkdownPipeline CreatePipeline()
    {
        var builder = new MarkdownPipelineBuilder().UseAdvancedExtensions();
        // Single-dollar math swallows "$ ... | ... $" across table cells;
        // it never renders (no MathJax loaded), keep dollars literal.
        builder.Extensions.RemoveAll(e => (e.GetType().FullName ?? "").Contains("Mathematic"));
        return builder.Build();
    }

    public readonly record struct Rgb(int R, int G, int B)
    {
        public readonly string Hex => $"#{R:X2}{G:X2}{B:X2}";
        public readonly Color Color => Color.FromArgb(255, (byte)R, (byte)G, (byte)B);
    }

    // Same VS tones as the Mac palette: blue headings/links, red/gold bold,
    // purple italic, orange code, panel surfaces for backgrounds/borders.
    public readonly struct Palette
    {
        public readonly Rgb Heading, Strong, Emphasis, Code, CodeBg, Link, Quote, Marker, Dim;
        public readonly Rgb Body, BodyText, Panel, PanelBorder, CellBorder, Zebra;
        // JSON tree + edit-mode highlight (VS JSON tones, both themes).
        public readonly Rgb JsonKey, JsonString, JsonNumber, JsonBool, JsonNull, JsonComment;
        public Palette(bool dark)
        {
            if (dark)
            {
                Heading = new(0x56, 0x9C, 0xD6); Strong = new(0xDC, 0xDC, 0xAA); Emphasis = new(0xC5, 0x86, 0xC0);
                Code = new(0xCE, 0x91, 0x78); CodeBg = new(0x2D, 0x2D, 0x30); Link = new(0x4F, 0xC1, 0xFF);
                Quote = new(0x9C, 0xDC, 0xFE); Marker = new(0x56, 0x9C, 0xD6); Dim = new(0x80, 0x80, 0x80);
                Body = new(0x1E, 0x1E, 0x1E); BodyText = new(0xD4, 0xD4, 0xD4); Panel = new(0x25, 0x25, 0x26);
                PanelBorder = new(0x3F, 0x3F, 0x46); CellBorder = new(0x3F, 0x3F, 0x46); Zebra = new(0x23, 0x23, 0x26);
                JsonKey = new(0x9C, 0xDC, 0xFE); JsonString = new(0xCE, 0x91, 0x78); JsonNumber = new(0xB5, 0xCE, 0xA8);
                JsonBool = new(0x56, 0x9C, 0xD6); JsonNull = new(0x80, 0x80, 0x80); JsonComment = new(0x6A, 0x99, 0x50);
            }
            else
            {
                Heading = new(0x0F, 0x6C, 0xBD); Strong = new(0xA3, 0x15, 0x15); Emphasis = new(0x7A, 0x3E, 0x9D);
                Code = new(0xA3, 0x15, 0x15); CodeBg = new(0xF3, 0xF3, 0xF3); Link = new(0x0F, 0x6C, 0xBD);
                Quote = new(0x07, 0x5A, 0xA0); Marker = new(0x0F, 0x6C, 0xBD); Dim = new(0x80, 0x80, 0x80);
                Body = new(0xFF, 0xFF, 0xFF); BodyText = new(0x1E, 0x1E, 0x1E); Panel = new(0xF6, 0xF6, 0xF6);
                PanelBorder = new(0xE5, 0xE5, 0xE5); CellBorder = new(0xD4, 0xD4, 0xD4); Zebra = new(0xFA, 0xFA, 0xFA);
                JsonKey = new(0x00, 0x10, 0x80); JsonString = new(0xA3, 0x15, 0x15); JsonNumber = new(0x09, 0x86, 0x58);
                JsonBool = new(0x0F, 0x6C, 0xBD); JsonNull = new(0x80, 0x80, 0x80); JsonComment = new(0x00, 0x80, 0x00);
            }
        }
    }
}

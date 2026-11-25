using System.Text;
using System.Text.RegularExpressions;

namespace Resurgence.Ui;

/// <summary>The body of a news post (docs/server.md#news): a small subset of Markdown, read into a tree that <c>NewsBody.razor</c> draws as elements. Paragraphs (a blank line between them), <c>**bold**</c>, <c>*italic*</c> or <c>_italic_</c>,
/// <c>[text](https://address)</c> and lists (<c>- item</c>, <c>* item</c>, <c>1. item</c>); a backslash makes the next of <c>\ * _ [ ] ( )</c> plain. There is no HTML: a tag in the text is the tag's own characters, and a link is drawn only for an http or https address.</summary>
public static class NewsMarkdown
{
    public abstract record Inline;
    public sealed record Plain(string Text) : Inline;
    public sealed record Strong(IReadOnlyList<Inline> Content) : Inline;
    public sealed record Emphasis(IReadOnlyList<Inline> Content) : Inline;
    public sealed record Link(IReadOnlyList<Inline> Content, string Url) : Inline;

    public abstract record Block;
    public sealed record Paragraph(IReadOnlyList<Inline> Content) : Block;
    public sealed record ListBlock(bool Ordered, IReadOnlyList<IReadOnlyList<Inline>> Items) : Block;

    public static IReadOnlyList<Block> Parse(string markdown)
    {
        List<Block> blocks = [];
        List<string> paragraph = [];
        List<string>? items = null;
        var ordered = false;
        void Flush()
        {
            if (paragraph.Count > 0) blocks.Add(new Paragraph(Inlines(string.Join(' ', paragraph))));
            if (items is not null) blocks.Add(new ListBlock(ordered, [.. items.Select(Inlines)]));
            items = null;
            paragraph = [];
        }
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) Flush();
            else if (Item(line) is var (isOrdered, text))
            {
                if (items is null || ordered != isOrdered)
                {
                    Flush();
                    items = [];
                    ordered = isOrdered;
                }
                items.Add(text);
            }
            else
            {
                if (items is not null) Flush();
                paragraph.Add(line);
            }
        }
        Flush();
        return blocks;
    }

    // "- x", "* x" or "1. x"
    static (bool Ordered, string Text)? Item(string line)
    {
        if (line.Length > 2 && line[0] is '-' or '*' && line[1] == ' ') return (false, line[2..].TrimStart());
        var digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits])) digits++;
        return digits is >= 1 and <= 3 && line.Length > digits + 1 && line[digits] is '.' or ')' && line[digits + 1] == ' ' ? (true, line[(digits + 2)..].TrimStart()) : null;
    }

    static bool Word(string s, int i) => i >= 0 && i < s.Length && char.IsLetterOrDigit(s[i]);

    static List<Inline> Inlines(string s)
    {
        List<Inline> result = [];
        StringBuilder text = new();
        void Add(Inline inline)
        {
            if (text.Length > 0) result.Add(new Plain(text.ToString()));
            text.Clear();
            result.Add(inline);
        }
        for (var i = 0; i < s.Length;)
        {
            var c = s[i];
            int close;
            if (c == '\\' && i + 1 < s.Length && @"\*_[]()".Contains(s[i + 1]))
            {
                text.Append(s[i + 1]);
                i += 2;
            }
            else if (c == '*' && s.AsSpan(i).StartsWith("**", StringComparison.Ordinal) && (close = s.IndexOf("**", i + 2, StringComparison.Ordinal)) > i + 2)
            {
                Add(new Strong(Inlines(s[(i + 2)..close])));
                i = close + 2;
            }
            else if (c is '*' or '_' && !Word(s, c == '_' ? i - 1 : -1) && i + 1 < s.Length && s[i + 1] != ' '
                     && (close = s.IndexOf(c, i + 1)) > i + 1 && s[close - 1] != ' ' && (c == '*' || !Word(s, close + 1)))
            {
                Add(new Emphasis(Inlines(s[(i + 1)..close])));
                i = close + 1;
            }
            else if (c == '[' && s.IndexOf("](", i, StringComparison.Ordinal) is var mid and > 0 && s.IndexOf(')', mid + 2) is var end and > 0
                     && s[(mid + 2)..end].Trim() is var url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                Add(new Link(Inlines(s[(i + 1)..mid]), url));
                i = end + 1;
            }
            else
            {
                text.Append(c);
                i++;
            }
        }
        if (text.Length > 0) result.Add(new Plain(text.ToString()));
        return result;
    }

    static string Flat(IEnumerable<Inline> inlines) => string.Concat(inlines.Select(i => i switch
    {
        Plain p => p.Text,
        Strong s => Flat(s.Content),
        Emphasis e => Flat(e.Content),
        Link l => Flat(l.Content),
        _ => "",
    }));

    /// <summary>The carousel's short text: the first sentence of the body's first paragraph or list item as plain text, at most <paramref name="max"/> characters (cut at a space, with an ellipsis).</summary>
    public static string Lead(string markdown, int max = 140)
    {
        var first = Parse(markdown) is [var block, ..] ? block switch { Paragraph p => Flat(p.Content), ListBlock { Items: [var item, ..] } => Flat(item), _ => "" } : "";
        if (Regex.Match(first, @"[.!?](\s|$)") is { Success: true } sentence) first = first[..(sentence.Index + 1)];
        if (first.Length <= max) return first;
        var cut = first.LastIndexOf(' ', max - 1);
        return first[..(cut > 0 ? cut : max - 1)].TrimEnd(',', ';', ':', ' ') + "…";
    }
}

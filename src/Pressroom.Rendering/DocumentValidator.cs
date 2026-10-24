using AngleSharp.Html.Parser;

namespace Pressroom.Rendering;

/// <summary>Checks the actual HTML tree, including malformed markup repaired by the HTML parser.</summary>
public static class DocumentValidator
{
    public static bool HasBaseElement(string html)
    {
        using var document = new HtmlParser(new HtmlParserOptions { IsScripting = true }).ParseDocument(html);
        return document.QuerySelector("base") is not null;
    }
}

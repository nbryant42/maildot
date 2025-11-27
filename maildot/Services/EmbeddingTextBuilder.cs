using System;
using System.Text;
using HtmlAgilityPack;
using maildot.Models;

namespace maildot.Services;

public static class EmbeddingTextBuilder
{
    public static string BuildCombinedText(ImapMessage message, MessageBody body)
    {
        var subject = string.IsNullOrWhiteSpace(message.Subject) ? "(no subject)" : message.Subject;
        var content = !string.IsNullOrWhiteSpace(body.PlainText)
            ? body.PlainText!
            : HtmlToPlainText(body.SanitizedHtml ?? body.HtmlText ?? string.Empty);

        var combined = $"{subject}\n{content}".Trim();
        return TextCleaner.CleanNonNull(combined);
    }

    public static string HtmlToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var text = doc.DocumentNode.InnerText ?? string.Empty;
        var flattened = text.Replace("\r", " ").Replace("\n", " ");
        return TextCleaner.CleanNonNull(flattened);
    }
}

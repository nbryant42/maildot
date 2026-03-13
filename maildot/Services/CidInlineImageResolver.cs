using System;
using System.Collections.Generic;
using System.Linq;
using HtmlAgilityPack;

namespace maildot.Services;

public sealed record CidInlineImageRenderResult(string Html, IReadOnlySet<int> ConsumedAttachmentIds);

public static class CidInlineImageResolver
{
    public static bool ContainsCidReferences(string? html) =>
        !string.IsNullOrWhiteSpace(html) && html.Contains("cid:", StringComparison.OrdinalIgnoreCase);

    public static HashSet<string> ExtractCidReferences(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in doc.DocumentNode.SelectNodes("//img[@src]") ?? Enumerable.Empty<HtmlNode>())
        {
            var src = image.GetAttributeValue("src", string.Empty);
            var normalized = NormalizeContentId(src);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    public static string? NormalizeContentId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[4..].Trim();
        }

        if (normalized.Length >= 2 && normalized[0] == '<' && normalized[^1] == '>')
        {
            normalized = normalized[1..^1].Trim();
        }

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public static CidInlineImageRenderResult Resolve(string html, IReadOnlyList<ImapSyncService.AttachmentContent> attachments)
    {
        if (string.IsNullOrWhiteSpace(html) || attachments.Count == 0 || !ContainsCidReferences(html))
        {
            return new CidInlineImageRenderResult(html, new HashSet<int>());
        }

        var matches = attachments
            .Where(a => !string.IsNullOrWhiteSpace(a.ContentId) &&
                        !string.IsNullOrWhiteSpace(a.ContentType) &&
                        !string.IsNullOrWhiteSpace(a.Base64Data) &&
                        a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            .GroupBy(a => NormalizeContentId(a.ContentId), StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .ToDictionary(g => g.Key!, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var consumed = new HashSet<int>();

        foreach (var image in doc.DocumentNode.SelectNodes("//img[@src]") ?? Enumerable.Empty<HtmlNode>())
        {
            var src = image.GetAttributeValue("src", string.Empty);
            var normalized = NormalizeContentId(src);
            if (string.IsNullOrWhiteSpace(normalized) || !matches.TryGetValue(normalized, out var attachment))
            {
                continue;
            }

            image.SetAttributeValue("src", $"data:{attachment.ContentType};base64,{attachment.Base64Data}");
            consumed.Add(attachment.AttachmentId);
        }

        return new CidInlineImageRenderResult(doc.DocumentNode.InnerHtml, consumed);
    }
}

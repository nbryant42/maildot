using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using maildot.Data;
using maildot.Models;

namespace maildot.Services;

public sealed record SanitizedHtmlResult(string Html, IReadOnlyList<BlockedResource> BlockedResources);

public sealed record BlockedResource(string Url, BlockedResourceReason Reason);

public enum BlockedResourceReason
{
    DisallowedTag,
    DisallowedAttribute,
    ExternalContentBlocked,
    InvalidSchemeOrHost,
    PrivateNetwork
}

public static class HtmlSanitizer
{
    public const int CurrentPolicyVersion = 4;

    private static readonly HashSet<string> DisallowedElements =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "script", "style", "iframe", "frame", "frameset", "object", "embed", "link",
            "meta", "form", "input", "button", "video", "audio", "source", "canvas",
            "svg", "math", "foreignobject", "use", "base"
        };

    private static readonly HashSet<string> ExternalContentElements =
        new(StringComparer.OrdinalIgnoreCase) { "img", "video", "audio", "iframe", "frame", "source", "link" };

    private static readonly HashSet<string> GlobalAttributes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "title", "class", "lang", "dir"
        };

    private static readonly Dictionary<string, HashSet<string>> AllowedAttributesByElement =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "href", "title", "target", "rel" },
            ["img"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src", "alt", "width", "height" },
            ["table"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "width", "height", "align", "bgcolor", "border", "cellpadding", "cellspacing" },
            ["thead"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "align", "valign" },
            ["tbody"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "align", "valign" },
            ["tfoot"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "align", "valign" },
            ["tr"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "align", "valign", "bgcolor" },
            ["td"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "align", "valign", "width", "height", "bgcolor", "border", "colspan", "rowspan" },
            ["th"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "align", "valign", "width", "height", "bgcolor", "border", "colspan", "rowspan" },
            ["colgroup"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "width", "span" },
            ["col"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "width", "span" },
            ["p"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "align" },
            ["div"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "align" },
            ["span"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["blockquote"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "align" },
            ["hr"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "width", "align" }
        };

    private static readonly HashSet<string> UrlAttributes =
        new(StringComparer.OrdinalIgnoreCase) { "href", "src", "background", "action" };

    private static readonly HashSet<string> AllowedCssProperties =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "color",
            "background-color",
            "font",
            "font-family",
            "font-size",
            "font-style",
            "font-weight",
            "line-height",
            "text-align",
            "text-decoration",
            "letter-spacing",
            "white-space",
            "word-break",
            "word-wrap",
            "display",
            "width",
            "height",
            "max-width",
            "min-width",
            "max-height",
            "min-height",
            "margin",
            "margin-left",
            "margin-right",
            "margin-top",
            "margin-bottom",
            "padding",
            "padding-left",
            "padding-right",
            "padding-top",
            "padding-bottom",
            "border",
            "border-top",
            "border-right",
            "border-bottom",
            "border-left",
            "border-color",
            "border-style",
            "border-width",
            "border-collapse",
            "border-spacing",
            "vertical-align",
            "background-repeat",
            "background-position",
            "background-size"
        };

    public static SanitizedHtmlResult Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new SanitizedHtmlResult(string.Empty, Array.Empty<BlockedResource>());
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var blockedResources = new List<BlockedResource>();
        CleanNode(doc.DocumentNode, blockedResources);

        var sanitized = doc.DocumentNode.InnerHtml;
        return new SanitizedHtmlResult(sanitized, blockedResources);
    }

    public static string? SanitizeNullable(string? html) =>
        string.IsNullOrWhiteSpace(html)
            ? null
            : TextCleaner.CleanNullable(Sanitize(html).Html);

    public static bool NeedsResanitization(int sanitizedHtmlVersion, string? htmlText) =>
        !string.IsNullOrWhiteSpace(htmlText) && sanitizedHtmlVersion < CurrentPolicyVersion;

    public static async Task<string> BuildFallbackHtmlAsync(MailDbContext db, MessageBody body, CancellationToken cancellationToken)
    {
        if (NeedsResanitization(body.SanitizedHtmlVersion, body.HtmlText))
        {
            body.SanitizedHtml = SanitizeNullable(body.HtmlText);
            body.SanitizedHtmlVersion = CurrentPolicyVersion;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to persist refreshed sanitized HTML for message {body.MessageId}: {ex}");
            }
        }

        if (!string.IsNullOrWhiteSpace(body.SanitizedHtml))
        {
            return body.SanitizedHtml;
        }

        if (!string.IsNullOrWhiteSpace(body.HtmlText))
        {
            return Sanitize(body.HtmlText).Html;
        }

        if (!string.IsNullOrWhiteSpace(body.PlainText))
        {
            return $"<html><body><pre>{System.Net.WebUtility.HtmlEncode(body.PlainText)}</pre></body></html>";
        }

        return "<html><body></body></html>";
    }

    private static void CleanNode(HtmlNode node, ICollection<BlockedResource> blocked)
    {
        foreach (var child in node.ChildNodes.ToList())
        {
            if (child.NodeType == HtmlNodeType.Element)
            {
                if (!IsAllowedElement(child.Name))
                {
                    blocked.Add(new BlockedResource(child.Name, BlockedResourceReason.DisallowedTag));
                    child.Remove();
                    continue;
                }

                SanitizeAttributes(child, blocked);
            }

            CleanNode(child, blocked);
        }
    }

    private static void SanitizeAttributes(HtmlNode node, ICollection<BlockedResource> blocked)
    {
        var toRemove = new List<HtmlAttribute>();
        foreach (var attribute in node.Attributes.ToList())
        {
            if (attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            {
                toRemove.Add(attribute);
                blocked.Add(new BlockedResource(attribute.Name, BlockedResourceReason.DisallowedAttribute));
                continue;
            }

            if (string.Equals(attribute.Name, "style", StringComparison.OrdinalIgnoreCase))
            {
                var sanitizedStyle = SanitizeStyleAttribute(attribute.Value);
                if (string.IsNullOrWhiteSpace(sanitizedStyle))
                {
                    toRemove.Add(attribute);
                    blocked.Add(new BlockedResource(attribute.Name, BlockedResourceReason.DisallowedAttribute));
                }
                else
                {
                    attribute.Value = sanitizedStyle;
                }

                continue;
            }

            if (!IsAllowedAttribute(node.Name, attribute.Name))
            {
                toRemove.Add(attribute);
                continue;
            }

            if (!UrlAttributes.Contains(attribute.Name))
            {
                continue;
            }

            var evaluation = EvaluateUrl(attribute.Value, node.Name, attribute.Name);
            if (evaluation != UrlEvaluation.Allow)
            {
                toRemove.Add(attribute);
                var reason = evaluation switch
                {
                    UrlEvaluation.PrivateNetwork => BlockedResourceReason.PrivateNetwork,
                    UrlEvaluation.Invalid => BlockedResourceReason.InvalidSchemeOrHost,
                    _ => BlockedResourceReason.ExternalContentBlocked
                };
                blocked.Add(new BlockedResource(attribute.Value, reason));
            }
        }

        foreach (var attr in toRemove)
        {
            node.Attributes.Remove(attr);
        }
    }

    private enum UrlEvaluation
    {
        Allow,
        BlockExternal,
        Invalid,
        PrivateNetwork
    }

    private static bool IsAllowedElement(string elementName) =>
        !DisallowedElements.Contains(elementName);

    private static bool IsAllowedAttribute(string elementName, string attributeName)
    {
        if (GlobalAttributes.Contains(attributeName))
        {
            return true;
        }

        return AllowedAttributesByElement.TryGetValue(elementName, out var allowed) &&
               allowed.Contains(attributeName);
    }

    private static string? SanitizeStyleAttribute(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var sanitizedDeclarations = new List<string>();
        foreach (var rawDeclaration in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colonIndex = rawDeclaration.IndexOf(':');
            if (colonIndex <= 0 || colonIndex == rawDeclaration.Length - 1)
            {
                continue;
            }

            var property = rawDeclaration[..colonIndex].Trim();
            var rawPropertyValue = rawDeclaration[(colonIndex + 1)..].Trim();
            if (!IsAllowedCssProperty(property) || !IsSafeCssValue(rawPropertyValue))
            {
                continue;
            }

            var normalizedValue = NormalizeCssValue(rawPropertyValue);
            if (string.IsNullOrWhiteSpace(normalizedValue))
            {
                continue;
            }

            sanitizedDeclarations.Add($"{property.ToLowerInvariant()}:{normalizedValue}");
        }

        return sanitizedDeclarations.Count == 0
            ? null
            : string.Join("; ", sanitizedDeclarations);
    }

    private static bool IsAllowedCssProperty(string property)
    {
        if (string.IsNullOrWhiteSpace(property) || property.StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        return AllowedCssProperties.Contains(property);
    }

    private static bool IsSafeCssValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Any(char.IsControl))
        {
            return false;
        }

        if (ContainsCssToken(value, "url(") ||
            ContainsCssToken(value, "image-set(") ||
            ContainsCssToken(value, "image(") ||
            ContainsCssToken(value, "@import") ||
            ContainsCssToken(value, "expression(") ||
            ContainsCssToken(value, "behavior(") ||
            ContainsCssToken(value, "-moz-binding") ||
            ContainsCssToken(value, "javascript:") ||
            ContainsCssToken(value, "data:"))
        {
            return false;
        }

        return value.All(IsAllowedCssCharacter);
    }

    private static bool ContainsCssToken(string value, string token) =>
        value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedCssCharacter(char c) =>
        char.IsLetterOrDigit(c) ||
        char.IsWhiteSpace(c) ||
        c is '#' or '%' or '.' or ',' or '(' or ')' or '-' or '_' or '/' or '"' or '\'' or '!' or ':' or '+';

    private static string NormalizeCssValue(string value) =>
        string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static UrlEvaluation EvaluateUrl(string value, string elementName, string attributeName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return UrlEvaluation.BlockExternal;
        }

        // Allow anchor references (#foo)
        if (value.StartsWith("#", StringComparison.Ordinal))
        {
            return UrlEvaluation.Allow;
        }

        // Allow data URIs only for img[src] to prevent phishing via data:text/html on anchors
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(elementName, "img", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(attributeName, "src", StringComparison.OrdinalIgnoreCase)
                ? UrlEvaluation.Allow
                : UrlEvaluation.Invalid;
        }

        // Allow cid: only for img[src]; the content is resolved from locally stored attachments later.
        if (value.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(elementName, "img", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(attributeName, "src", StringComparison.OrdinalIgnoreCase)
                ? UrlEvaluation.Allow
                : UrlEvaluation.Invalid;
        }

        if (!Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var uri))
        {
            return UrlEvaluation.Invalid;
        }

        if (!uri.IsAbsoluteUri)
        {
            return UrlEvaluation.BlockExternal;
        }

        if (string.Equals(uri.Scheme, "javascript", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
        {
            return UrlEvaluation.Invalid;
        }

        if (string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            if (IsPrivateHost(uri.Host))
            {
                return UrlEvaluation.PrivateNetwork;
            }

            if (ExternalContentElements.Contains(elementName))
            {
                return UrlEvaluation.BlockExternal;
            }

            return UrlEvaluation.Allow;
        }

        return UrlEvaluation.Invalid;
    }

    private static bool IsPrivateHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip))
            {
                return true;
            }

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = ip.GetAddressBytes();
                return (bytes[0] == 10) ||
                       (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                       (bytes[0] == 192 && bytes[1] == 168) ||
                       (bytes[0] == 127) ||
                       (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);
            }

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var bytes = ip.GetAddressBytes();
                // fc00::/7 unique local, fe80::/10 link-local
                return (bytes[0] & 0xFE) == 0xFC ||
                       (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80);
            }
        }

        return false;
    }
}

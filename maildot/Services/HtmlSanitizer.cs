using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

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
    private static readonly HashSet<string> DangerousElements =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "script", "style", "iframe", "frame", "frameset", "object", "embed", "link",
            "meta", "form", "input", "button", "video", "audio", "source", "canvas"
        };

    private static readonly HashSet<string> ExternalContentElements =
        new(StringComparer.OrdinalIgnoreCase) { "img", "video", "audio", "iframe", "frame", "source", "link" };

    private static readonly Dictionary<string, HashSet<string>> AllowedAttributes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "href", "title", "target", "rel" },
            ["img"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src", "alt", "title" },
            ["p"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "title" },
            ["div"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "title" },
            ["span"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "title" },
            ["*"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "title" }
        };

    private static readonly HashSet<string> UrlAttributes =
        new(StringComparer.OrdinalIgnoreCase) { "href", "src", "background", "action" };

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

    private static void CleanNode(HtmlNode node, ICollection<BlockedResource> blocked)
    {
        foreach (var child in node.ChildNodes.ToList())
        {
            if (child.NodeType == HtmlNodeType.Element)
            {
                if (DangerousElements.Contains(child.Name))
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
        var allowed = AllowedAttributes.TryGetValue(node.Name, out var attrs)
            ? attrs
            : AllowedAttributes.GetValueOrDefault("*");

        var toRemove = new List<HtmlAttribute>();
        foreach (var attribute in node.Attributes.ToList())
        {
            if (attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(attribute.Name, "style", StringComparison.OrdinalIgnoreCase))
            {
                toRemove.Add(attribute);
                blocked.Add(new BlockedResource(attribute.Name, BlockedResourceReason.DisallowedAttribute));
                continue;
            }

            if (allowed != null && !allowed.Contains(attribute.Name))
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

        // Allow data URIs for inline content
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return UrlEvaluation.Allow;
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

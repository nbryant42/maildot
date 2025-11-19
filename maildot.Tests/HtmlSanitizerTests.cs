using maildot.Services;

namespace maildot.Tests;

public class HtmlSanitizerTests
{
    [Fact]
    public void StripsScriptTags()
    {
        var html = "<div>Hello<script>alert('x')</script></div>";
        var result = HtmlSanitizer.Sanitize(html);
        Assert.DoesNotContain("script", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BlocksExternalImages()
    {
        var html = "<img src=\"http://example.com/image.png\">";
        var result = HtmlSanitizer.Sanitize(html);
        Assert.DoesNotContain("src", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.BlockedResources, r => r.Url.Contains("example.com"));
    }

    [Fact]
    public void AllowsDataImages()
    {
        var html = "<img src=\"data:image/png;base64,AAAA\" />";
        var result = HtmlSanitizer.Sanitize(html);
        Assert.Contains("data:image/png", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.BlockedResources);
    }

    [Fact]
    public void RemovesJavascriptHref()
    {
        var html = "<a href=\"javascript:alert(1)\">click</a>";
        var result = HtmlSanitizer.Sanitize(html);
        Assert.DoesNotContain("javascript", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BlocksPrivateNetworkUrls()
    {
        var html = "<img src=\"http://127.0.0.1/pixel.png\" />";
        var result = HtmlSanitizer.Sanitize(html);
        Assert.Contains(result.BlockedResources, r => r.Reason == BlockedResourceReason.PrivateNetwork);
        Assert.DoesNotContain("127.0.0.1", result.Html, StringComparison.OrdinalIgnoreCase);
    }
}

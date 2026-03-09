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

    [Fact]
    public void StripsStylesheetLinkTags()
    {
        var html = "<div>Hello</div><link rel=\"stylesheet\" href=\"https://example.com/a.css\">";
        var result = HtmlSanitizer.Sanitize(html);

        Assert.DoesNotContain("<link", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.BlockedResources, r => r.Reason == BlockedResourceReason.DisallowedTag && r.Url == "link");
    }

    [Fact]
    public void PreservesSafeInlineStyles()
    {
        var html = "<div style=\"color: red; font-size: 14px; margin-top: 8px\">Hello</div>";
        var result = HtmlSanitizer.Sanitize(html);

        Assert.Contains("style=\"color:red; font-size:14px; margin-top:8px\"", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreservesCommonPresentationalAttributes()
    {
        var html = "<table width=\"600\" cellpadding=\"12\" cellspacing=\"0\"><tr><td align=\"center\" valign=\"top\" bgcolor=\"#ffffff\">Hi</td></tr></table>";
        var result = HtmlSanitizer.Sanitize(html);

        Assert.Contains("width=\"600\"", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cellpadding=\"12\"", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cellspacing=\"0\"", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("align=\"center\"", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("valign=\"top\"", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bgcolor=\"#ffffff\"", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemovesUnsafeCssUrlValues()
    {
        var html = "<div style=\"color: #222; background-image: url(https://example.com/hero.png)\">Hello</div>";
        var result = HtmlSanitizer.Sanitize(html);

        Assert.Contains("style=\"color:#222\"", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("background-image", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemovesUnsafeCssExpressions()
    {
        var html = "<div style=\"width: expression(alert(1)); color: blue\">Hello</div>";
        var result = HtmlSanitizer.Sanitize(html);

        Assert.Contains("style=\"color:blue\"", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expression", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemovesBackgroundImageSetValues()
    {
        var html = "<div style=\"color:#222; background:image-set('https://example.com/tracker.png' 1x)\">Hello</div>";
        var result = HtmlSanitizer.Sanitize(html);

        Assert.Contains("style=\"color:#222\"", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("image-set", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.com", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemovesStyleAttributeWhenAllDeclarationsAreUnsafe()
    {
        var html = "<div style=\"background:url(https://example.com/tracker.png)\">Hello</div>";
        var result = HtmlSanitizer.Sanitize(html);

        Assert.DoesNotContain("style=", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.BlockedResources, r => r.Reason == BlockedResourceReason.DisallowedAttribute && r.Url == "style");
    }

    [Fact]
    public void PreservesImageDimensions()
    {
        var html = "<img src=\"data:image/png;base64,AAAA\" width=\"640\" height=\"480\" alt=\"hero\">";
        var result = HtmlSanitizer.Sanitize(html);

        Assert.Contains("width=\"640\"", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("height=\"480\"", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alt=\"hero\"", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreservesSimpleNewsletterLayout()
    {
        var html = """
            <table width="600" cellpadding="0" cellspacing="0" style="background-color:#f4f4f4; border-collapse: collapse">
              <tr>
                <td style="padding:24px; text-align:center; color:#123456; font-family:Arial, sans-serif">
                  Weekly Update
                </td>
              </tr>
            </table>
            """;
        var result = HtmlSanitizer.Sanitize(html);

        Assert.Contains("background-color:#f4f4f4", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("border-collapse:collapse", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("padding:24px", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("text-align:center", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("font-family:Arial, sans-serif", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BlocksDataUriOnAnchors()
    {
        var html = "<a href=\"data:text/html,<script>alert(1)</script>\">click</a>";
        var result = HtmlSanitizer.Sanitize(html);

        Assert.DoesNotContain("data:", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.BlockedResources, r => r.Reason == BlockedResourceReason.InvalidSchemeOrHost);
    }

    [Fact]
    public void StripsSvgElements()
    {
        var html = "<div>Hello</div><svg onload=\"alert(1)\"><circle r=\"50\"/></svg>";
        var result = HtmlSanitizer.Sanitize(html);

        Assert.DoesNotContain("<svg", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.BlockedResources, r => r.Reason == BlockedResourceReason.DisallowedTag && r.Url == "svg");
    }

    [Fact]
    public void StripsMathElements()
    {
        var html = "<div>Hello</div><math><mrow><mi>x</mi></mrow></math>";
        var result = HtmlSanitizer.Sanitize(html);

        Assert.DoesNotContain("<math", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.BlockedResources, r => r.Reason == BlockedResourceReason.DisallowedTag && r.Url == "math");
    }

    [Fact]
    public void NeedsResanitization_WhenStoredVersionIsBehind()
    {
        Assert.True(HtmlSanitizer.NeedsResanitization(1, "<div>Hello</div>"));
    }

    [Fact]
    public void NeedsResanitization_WhenVersionIsCurrentOrHtmlMissing_ReturnsFalse()
    {
        Assert.False(HtmlSanitizer.NeedsResanitization(HtmlSanitizer.CurrentPolicyVersion, "<div>Hello</div>"));
        Assert.False(HtmlSanitizer.NeedsResanitization(1, null));
    }
}

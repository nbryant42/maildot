using maildot.Models;
using maildot.Services;

namespace maildot.Tests;

public class EmbeddingTextBuilderTests
{
    [Fact]
    public void BuildCombinedText_PrefersPlainTextAndIncludesSubject()
    {
        var msg = new ImapMessage { Subject = "Subject line" };
        var body = new MessageBody
        {
            PlainText = "Plain content",
            HtmlText = "<p>Html content</p>"
        };

        var text = EmbeddingTextBuilder.BuildCombinedText(msg, body);

        Assert.Contains("Subject line", text);
        Assert.Contains("Plain content", text);
        Assert.DoesNotContain("<p>", text);
    }

    [Fact]
    public void BuildCombinedText_FallsBackToHtmlText()
    {
        var msg = new ImapMessage { Subject = string.Empty };
        var body = new MessageBody
        {
            PlainText = null,
            HtmlText = "<div>Click <b>here</b></div>"
        };

        var text = EmbeddingTextBuilder.BuildCombinedText(msg, body);

        Assert.Contains("(no subject)", text);
        Assert.Contains("Click here", text);
        Assert.DoesNotContain("<b>", text);
    }
}

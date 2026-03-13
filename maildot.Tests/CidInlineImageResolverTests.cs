using maildot.Services;

namespace maildot.Tests;

public class CidInlineImageResolverTests
{
    [Fact]
    public void NormalizeContentId_StripsCidPrefixAndAngleBrackets()
    {
        var normalized = CidInlineImageResolver.NormalizeContentId(" cid:<Logo123@example.com> ");

        Assert.Equal("Logo123@example.com", normalized);
    }

    [Fact]
    public void Resolve_RewritesCidImages_CaseInsensitively_AndTracksConsumedAttachments()
    {
        var html = "<html><body><img src=\"cid:logo123@example.com\"></body></html>";
        var attachments = new List<ImapSyncService.AttachmentContent>
        {
            new(
                AttachmentId: 7,
                FileName: "logo.png",
                ContentType: "image/png",
                ContentId: "<Logo123@Example.com>",
                Disposition: "inline",
                Base64Data: "AAAA",
                SizeBytes: 4)
        };

        var result = CidInlineImageResolver.Resolve(html, attachments);

        Assert.Contains("src=\"data:image/png;base64,AAAA\"", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(7, result.ConsumedAttachmentIds);
    }

    [Fact]
    public void Resolve_LeavesUnmatchedCidImagesUnchanged()
    {
        var html = "<html><body><img src=\"cid:missing\"></body></html>";
        var attachments = new List<ImapSyncService.AttachmentContent>
        {
            new(
                AttachmentId: 8,
                FileName: "photo.png",
                ContentType: "image/png",
                ContentId: "<other>",
                Disposition: "inline",
                Base64Data: "BBBB",
                SizeBytes: 4)
        };

        var result = CidInlineImageResolver.Resolve(html, attachments);

        Assert.Contains("src=\"cid:missing\"", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.ConsumedAttachmentIds);
    }
}

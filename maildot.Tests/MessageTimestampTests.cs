using maildot.Services;
using MailKit;
using MimeKit;

namespace maildot.Tests;

public class MessageTimestampTests
{
    private static readonly DateTimeOffset HistoricalDate = new(2019, 10, 13, 1, 27, 2, TimeSpan.Zero);

    private static MimeMessage Message(params (string Name, string Value)[] headers)
    {
        var message = new MimeMessage();
        message.Headers.Clear();
        foreach (var (name, value) in headers) message.Headers.Add(name, value);
        return message;
    }

    [Fact]
    public void ReimportOfSentMailWithoutReceivedHeaderUses2019Date()
    {
        using var message = Message(("Date", "Sat, 12 Oct 2019 21:27:02 -0400"));
        Assert.Equal(HistoricalDate, MessageTimestamp.Resolve(message, null));
    }

    [Fact]
    public void UsesFetchedInternalDateWhenReceivedHeaderIsMissing()
    {
        using var message = Message(("Date", "Sat, 12 Oct 2019 21:26:00 -0400"));
        Assert.Equal(HistoricalDate, MessageTimestamp.Resolve(message, HistoricalDate));
    }

    [Fact]
    public void SkipsMalformedReceivedHeaderAndUsesNextValidOne()
    {
        using var message = Message(("Received", "unparseable"),
            ("Received", "from mail.example.test; Sat, 12 Oct 2019 21:27:02 -0400"));
        Assert.Equal(HistoricalDate, MessageTimestamp.Resolve(message, HistoricalDate.AddDays(1)));
    }

    [Fact]
    public void MissingDatesPreserveExistingTimestampOrUseStableUnknown()
    {
        using var message = Message(("Date", "unparseable"));
        Assert.Equal(HistoricalDate, MessageTimestamp.Resolve(message, null, HistoricalDate));
        Assert.Equal(MessageTimestamp.Unknown, MessageTimestamp.Resolve(message, null));
    }

    [Fact]
    public void HeaderOnlySyncUsesEnvelopeDateWhenInternalDateIsMissing()
    {
        var summary = new MessageSummary(0) { Envelope = new Envelope { Date = HistoricalDate } };
        Assert.Equal(HistoricalDate, MessageTimestamp.Resolve(summary));
        summary.Envelope = null;
        Assert.Equal(HistoricalDate, MessageTimestamp.Resolve(summary, HistoricalDate));
        Assert.Equal(MessageTimestamp.Unknown, MessageTimestamp.Resolve(summary));
    }
}

using System;
using System.Globalization;
using MailKit;
using MimeKit;
using MimeKit.Utils;

namespace maildot.Services;

public static class MessageTimestamp
{
    // Unknown dates must be stable, never the time an archive happens to be imported.
    public static readonly DateTimeOffset Unknown = DateTimeOffset.UnixEpoch;

    public static DateTimeOffset Resolve(MimeMessage message, DateTimeOffset? internalDate, DateTimeOffset? existing = null)
    {
        foreach (var header in message.Headers)
        {
            if (!header.Field.Equals("Received", StringComparison.OrdinalIgnoreCase)) continue;
            var value = header.Value;
            var separator = value.LastIndexOf(';');
            var date = separator >= 0 ? value[(separator + 1)..].Trim() : value.Trim();
            if (TryParse(date, out var received)) return received;
        }

        // Parse the actual Date header: a new MimeMessage can have a default Date that
        // wasn't supplied by the sender and must not manufacture an import timestamp.
        DateTimeOffset? sent = null;
        foreach (var header in message.Headers)
        {
            if (header.Field.Equals("Date", StringComparison.OrdinalIgnoreCase) && TryParse(header.Value, out var parsed))
            {
                sent = parsed;
                break;
            }
        }
        return ResolveFallback(internalDate, sent, existing);
    }

    public static DateTimeOffset Resolve(IMessageSummary summary, DateTimeOffset? existing = null) =>
        ResolveFallback(summary.InternalDate, summary.Envelope?.Date, existing);

    private static DateTimeOffset ResolveFallback(DateTimeOffset? internalDate, DateTimeOffset? sent, DateTimeOffset? existing) =>
        (internalDate ?? sent ?? existing ?? Unknown).ToUniversalTime();

    private static bool TryParse(string value, out DateTimeOffset date)
    {
        if (DateUtils.TryParse(value, out date) || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out date))
        {
            date = date.ToUniversalTime();
            return true;
        }
        return false;
    }
}

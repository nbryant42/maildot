using System;
using System.Text;

namespace maildot.Services;

public static class TextCleaner
{
    public static string? CleanNullable(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var withoutNulls = value.Replace("\0", string.Empty);
        return StripInvalidSurrogates(withoutNulls);
    }

    public static string CleanNonNull(string? value)
    {
        var cleaned = CleanNullable(value);
        return string.IsNullOrEmpty(cleaned) ? string.Empty : cleaned!;
    }

    private static string StripInvalidSurrogates(string value)
    {
        var span = value.AsSpan();
        var sb = new StringBuilder(span.Length);

        for (var i = 0; i < span.Length; i++)
        {
            var ch = span[i];
            if (!char.IsSurrogate(ch))
            {
                sb.Append(ch);
                continue;
            }

            if (char.IsHighSurrogate(ch) && i + 1 < span.Length && char.IsLowSurrogate(span[i + 1]))
            {
                sb.Append(ch);
                sb.Append(span[i + 1]);
                i++;
                continue;
            }

            // skip lone surrogate
        }

        return sb.ToString();
    }
}

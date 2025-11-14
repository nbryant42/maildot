using System;
using System.Collections.Generic;
using System.Linq;

namespace maildot.Services;

public static class SenderInitialsHelper
{
    private static readonly char[] NameSeparators = { ' ', '\t', '\r', '\n' };

    public static string From(string? displayName, string? address)
    {
        if (TryFromName(displayName, out var initials))
        {
            return initials;
        }

        if (!string.IsNullOrWhiteSpace(address))
        {
            var localPart = address.Split('@').FirstOrDefault() ?? string.Empty;
            var derived = ExtractFromLocalPart(localPart);
            if (!string.IsNullOrEmpty(derived))
            {
                return derived.ToUpperInvariant();
            }
        }

        return "?";
    }

    private static bool TryFromName(string? name, out string initials)
    {
        initials = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var tokens = name
            .Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(TrimNonAlphaNumeric)
            .Where(token => token.Length > 0)
            .ToList();

        if (tokens.Count == 0)
        {
            return false;
        }

        var letters = new List<char>();
        foreach (var token in tokens)
        {
            var firstChar = token.FirstOrDefault(char.IsLetterOrDigit);
            if (firstChar != default)
            {
                letters.Add(char.ToUpperInvariant(firstChar));
            }

            if (letters.Count == 2)
            {
                break;
            }
        }

        if (letters.Count == 0)
        {
            return false;
        }

        initials = letters.Count == 1
            ? letters[0].ToString()
            : new string(new[] { letters[0], letters[1] });

        return true;
    }

    private static string TrimNonAlphaNumeric(string value)
    {
        var start = 0;
        var end = value.Length - 1;

        while (start <= end && !char.IsLetterOrDigit(value[start]))
        {
            start++;
        }

        while (end >= start && !char.IsLetterOrDigit(value[end]))
        {
            end--;
        }

        return start <= end ? value[start..(end + 1)] : string.Empty;
    }

    private static string ExtractFromLocalPart(string localPart)
    {
        if (string.IsNullOrWhiteSpace(localPart))
        {
            return string.Empty;
        }

        var letters = new List<char>();
        var separators = new[] { '.', '-', '_', '+' };
        var segments = localPart.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var filtered = new string(segment.Where(char.IsLetterOrDigit).ToArray());
            if (filtered.Length == 0)
            {
                continue;
            }

            letters.Add(filtered[0]);
            if (letters.Count >= 2)
            {
                break;
            }
        }

        if (letters.Count == 0)
        {
            return string.Empty;
        }

        if (letters.Count == 1)
        {
            var remaining = localPart.SkipWhile(ch => !char.IsLetterOrDigit(ch))
                .Skip(letters.Count)
                .FirstOrDefault(char.IsLetterOrDigit);
            if (remaining != default)
            {
                letters.Add(remaining);
            }
        }

        if (letters.Count == 1)
        {
            return letters[0].ToString();
        }

        return new string(new[] { letters[0], letters[1] });
    }
}

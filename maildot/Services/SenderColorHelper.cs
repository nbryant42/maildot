using System;
using System.Collections.Generic;
using System.Linq;

namespace maildot.Services;

public static class SenderColorHelper
{
    public readonly record struct RgbColor(byte R, byte G, byte B);

    public static RgbColor GetColor(string? displayName, string? address)
    {
        var key = !string.IsNullOrWhiteSpace(displayName)
            ? displayName.Trim()
            : address?.Trim() ?? string.Empty;

        var normalized = key.ToUpperInvariant();
        var hue = ComputeHue(normalized);

        // Pastel palette
        const double saturation = 0.45;
        const double value = 0.90;

        return FromHsv(hue, saturation, value);
    }

    private static double ComputeHue(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return 210; // default calm blue
        }

        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in key)
            {
                hash ^= ch;
                hash *= 16777619;
            }

            return (hash % 360);
        }
    }

    private static RgbColor FromHsv(double hue, double saturation, double value)
    {
        hue = hue % 360;
        var c = value * saturation;
        var x = c * (1 - Math.Abs((hue / 60) % 2 - 1));
        var m = value - c;

        double rPrime, gPrime, bPrime;

        if (hue < 60)
        {
            rPrime = c; gPrime = x; bPrime = 0;
        }
        else if (hue < 120)
        {
            rPrime = x; gPrime = c; bPrime = 0;
        }
        else if (hue < 180)
        {
            rPrime = 0; gPrime = c; bPrime = x;
        }
        else if (hue < 240)
        {
            rPrime = 0; gPrime = x; bPrime = c;
        }
        else if (hue < 300)
        {
            rPrime = x; gPrime = 0; bPrime = c;
        }
        else
        {
            rPrime = c; gPrime = 0; bPrime = x;
        }

        byte r = (byte)Math.Round((rPrime + m) * 255);
        byte g = (byte)Math.Round((gPrime + m) * 255);
        byte b = (byte)Math.Round((bPrime + m) * 255);

        return new RgbColor(r, g, b);
    }
}

using HtmlAgilityPack;
using maildot.Data;
using maildot.Models;
using maildot.Services;
using Microsoft.EntityFrameworkCore;
using Tokenizers.DotNet;

namespace TokenStats;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var tokenizerPath = args.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(tokenizerPath))
        {
            const string hubName = "onnx-community/Qwen3-Embedding-0.6B-ONNX";
            const string tokFile = "tokenizer.json";
            var settingsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "maildot", "hf", hubName);

            Console.WriteLine("No tokenizer path provided; downloading from Hugging Face if needed...");
            tokenizerPath = await HuggingFace.GetFileFromHub(hubName, tokFile, settingsDir);
        }

        if (!File.Exists(tokenizerPath))
        {
            Console.WriteLine($"Tokenizer file not found: {tokenizerPath}");
            return;
        }

        var tokenizer = new Tokenizer(vocabPath: tokenizerPath);
        var texts = await LoadMessageTextsAsync();
        if (texts.Count == 0)
        {
            Console.WriteLine("No messages found in the database.");
            return;
        }

        var lengths = texts.Select(t => CountTokens(tokenizer, t)).ToList();
        ReportStats(lengths);
    }

    private static async Task<List<string>> LoadMessageTextsAsync()
    {
        var settings = PostgresSettingsStore.Load();
        var pwResponse = await CredentialManager.RequestPostgresPasswordAsync(settings);
        if (pwResponse.Result != CredentialAccessResult.Success || string.IsNullOrWhiteSpace(pwResponse.Password))
        {
            Console.WriteLine("PostgreSQL password not found. Please set it in the vault first.");
            return new List<string>();
        }

        using var db = MailDbContextFactory.CreateDbContext(settings, pwResponse.Password);

        var messages = await db.MessageBodies
            .Include(b => b.Message)
            .AsNoTracking()
            .Select(b => new
            {
                b.Message.Subject,
                b.PlainText,
                b.HtmlText,
                b.SanitizedHtml
            })
            .ToListAsync();

        var list = new List<string>(messages.Count);
        foreach (var m in messages)
        {
            var body = !string.IsNullOrWhiteSpace(m.PlainText)
                ? m.PlainText
                : StripHtml(m.SanitizedHtml ?? m.HtmlText ?? string.Empty);

            var text = $"{m.Subject ?? string.Empty}\n{body}".Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                list.Add(text);
            }
        }

        return list;
    }

    private static int CountTokens(Tokenizer tokenizer, string text)
    {
        return tokenizer.Encode(text).Length;
    }

    private static void ReportStats(List<int> lengths)
    {
        lengths.Sort();
        double min = lengths.First();
        double max = lengths.Last();
        double mean = lengths.Average();
        double median = lengths[lengths.Count / 2];
        if (lengths.Count % 2 == 0)
        {
            median = (lengths[lengths.Count / 2 - 1] + lengths[lengths.Count / 2]) / 2.0;
        }

        double sumSq = lengths.Sum(l => Math.Pow(l - mean, 2));
        double std = Math.Sqrt(sumSq / lengths.Count);

        Console.WriteLine($"Messages analyzed: {lengths.Count}");
        Console.WriteLine($"Min:    {min}");
        Console.WriteLine($"Mean:   {mean:F2}");
        Console.WriteLine($"Median: {median:F2}");
        Console.WriteLine($"Max:    {max}");
        Console.WriteLine($"StdDev: {std:F2}");
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc.DocumentNode.InnerText;
    }
}

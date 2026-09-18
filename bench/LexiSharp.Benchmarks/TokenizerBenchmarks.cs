using BenchmarkDotNet.Attributes;
using LexiSharp.Linguistics;

namespace LexiSharp.Benchmarks;

[MemoryDiagnoser]
public class TokenizerBenchmarks
{
    private string _asciiText = null!;
    private string _accentedText = null!;
    private string _mixedUnicode = null!;

    [GlobalSetup]
    public void Setup()
    {
        _asciiText = RepeatWords(
            "alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima mike november oscar papa quebec romeo sierra tango uniform victor whiskey xray yankee zulu",
            80);

        _accentedText = RepeatWords(
            "café déjà vu façade résumé été hôtel élève être français naïf toit île début coût août œuf noël forêt benoît maître",
            80);

        _mixedUnicode = RepeatWords(
            "hello 🚀 world привет мир αλφα βητα 中文测试 blah",
            80);
    }

    private static string RepeatWords(string words, int times)
    {
        var builder = new System.Text.StringBuilder(words.Length * times + times);
        for (int i = 0; i < times; i++)
            builder.Append(words).Append(' ');
        return builder.ToString();
    }

    [Benchmark(Baseline = true)]
    public int TokenizeDefaultAscii() => Tokenizer.Default.Tokenize(_asciiText).Count;

    [Benchmark]
    public int TokenizeAccented() => Tokenizer.Default.Tokenize(_accentedText).Count;

    [Benchmark]
    public int TokenizeMixedUnicode() => Tokenizer.Default.Tokenize(_mixedUnicode).Count;

    [Benchmark]
    public int NormalizeAsciiOnly()
    {
        int count = 0;
        count += Tokenizer.Normalize("alpha").Length;
        count += Tokenizer.Normalize("bravo").Length;
        count += Tokenizer.Normalize("charlie").Length;
        count += Tokenizer.Normalize("delta").Length;
        return count;
    }

    [Benchmark]
    public int NormalizeAccented()
    {
        int count = 0;
        count += Tokenizer.Normalize("café").Length;
        count += Tokenizer.Normalize("déjà").Length;
        count += Tokenizer.Normalize("résumé").Length;
        count += Tokenizer.Normalize("façade").Length;
        return count;
    }
}
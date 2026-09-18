using System.Text;
using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

public class TokenizerParityTests
{
    private static readonly string[] Samples =
    {
        "plain ascii text with punctuation, and some 1234567890 numbers!",
        "C'est déjà vu - l'été à Paris…",
        "Straße Ärger über Öl und so.",
        "hello 🚀 world 🌍 and 🐍",
        "привет мир как дела хорошо",
        "αλφα βητα γαμμα δελτα",
        "v1.2.3 beta2 rc-7",
        "foo—bar «quoted» “double” …more",
        "a b ii c",
        "  leading and trailing  ",
        "中文测试与word混合mixed文本",
        "word\u0301with\u0301combining\u0301marks",
        "no-break\u00A0space\u00A0here",
        "emoji\u200Dzwj\u200Dconnects words",
        "😀😃😄 multiples",
        "Cats, dogs. Fish; birds! Pigs?",
        "one\ttwo\nthree\r\nfour",
        "double  space  after   words",
        "UPPER lower MiXeD cAsE",
        "étude éléphant été été",
        "foo_bar-baz.qux/quux",
    };

    public static TheoryData<TokenizerOptions?> Options => new()
    {
        null,
        new TokenizerOptions { KeepSingleCharTerms = true },
        new TokenizerOptions { NGramMax = 2 },
        new TokenizerOptions { RemoveStopWords = true },
    };

    [Theory]
    [MemberData(nameof(Options))]
    public void Tokenize_MatchesScalarReference(TokenizerOptions? options)
    {
        var sut = new Tokenizer(options);

        foreach (var sample in Samples)
        {
            Assert.Equal(
                ReferenceTokenize(sample, options),
                sut.Tokenize(sample));
        }
    }

    private static IReadOnlyList<string> ReferenceTokenize(string text, TokenizerOptions? options)
    {
        options ??= TokenizerOptions.Default;

        var words = new List<string>();
        var word = new StringBuilder();

        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
                word.Append(rune);
            else if (word.Length > 0)
            {
                if (word.Length >= 2 || options.KeepSingleCharTerms)
                    words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0 && (word.Length >= 2 || options.KeepSingleCharTerms))
            words.Add(word.ToString());
        else
            word.Clear();

        var terms = new List<string>(words.Count);
        foreach (var raw in words)
        {
            var term = Tokenizer.Normalize(raw);
            if (options.RemoveStopWords && options.GetStopWords().Contains(term))
                continue;
            if (options.Stemmer is not null)
                term = options.Stemmer.Stem(term);
            terms.Add(term);
        }

        if (options.NGramMax <= 1 || terms.Count < 2)
            return terms;

        var ngrams = new List<string>(terms.Count);
        for (int n = options.NGramMin; n <= options.NGramMax; n++)
        {
            for (int i = 0; i + n <= terms.Count; i++)
                ngrams.Add(string.Join(' ', terms.Skip(i).Take(n)));
        }

        return ngrams;
    }
}
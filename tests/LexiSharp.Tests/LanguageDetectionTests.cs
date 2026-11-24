using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

public class LanguageDetectionTests
{
    [Fact]
    public void DetectsFrenchSentence()
    {
        Assert.Equal("fr", LanguageAwareTokenizer.DetectFrenchEnglish(
            "Le développement de la bibliothèque est dans le dépôt avec les autres projets"));
    }

    [Fact]
    public void DetectsEnglishSentence()
    {
        Assert.Equal("en", LanguageAwareTokenizer.DetectFrenchEnglish(
            "The development of the library is in the repository with the other projects"));
    }

    [Fact]
    public void ShortText_FallsBackToMarkers()
    {
        // Too short for n-gram profiles; accent marker decides.
        Assert.Equal("fr", LanguageAwareTokenizer.DetectFrenchEnglish("déploiement"));
    }

    [Fact]
    public void EmptyText_DefaultsToEnglish()
    {
        Assert.Equal("en", LanguageAwareTokenizer.DetectFrenchEnglish(""));
    }
}

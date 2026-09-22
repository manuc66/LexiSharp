using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;

namespace LexiSharp.Tests.Conformance;

public class RankedTextSearchEngineConformanceTests : SearchEngineConformanceTests
{
    protected override EngineCapabilities Capabilities => new(Phrases: true, Expansions: true);

    protected override ITextSearchEngine CreateEngine() =>
        new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
}

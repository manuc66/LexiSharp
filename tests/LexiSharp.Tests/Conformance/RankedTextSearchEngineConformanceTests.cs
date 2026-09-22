using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;

namespace LexiSharp.Tests.Conformance;

public class RankedTextSearchEngineConformanceTests : SearchEngineConformanceTests
{

    protected override ITextSearchEngine CreateEngine() =>
        new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
}

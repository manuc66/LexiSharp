using System.Globalization;
using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Eval;

/// <summary>A term one analysis indexes and the other does not.</summary>
/// <param name="DocumentFrequency">
/// The term's df on the side that has it; the other side's is 0 by construction.
/// </param>
internal sealed record AnalysisDiffTerm(string Term, int DocumentFrequency);

/// <summary>One pair whose reach verdict the analysis decides.</summary>
/// <param name="Run">The verdict under the run's own analysis.</param>
/// <param name="Other">The verdict under the analysis named on the command line.</param>
internal sealed record ReachChange(string QueryId, string DocumentId, DocumentReach Run, DocumentReach Other);

/// <summary>How many judged pairs took one (run, other) verdict pair.</summary>
internal sealed record ReachTransition(DocumentReach Run, DocumentReach Other, int Pairs);

/// <summary>What the two vocabularies and the two indexes' document lengths say.</summary>
/// <param name="Available">
/// Whether both indexes can enumerate their vocabulary. The verdict comparison needs neither this
/// nor the lists below, so a store that cannot is reported as unmeasured rather than as a diff with
/// nothing in it.
/// </param>
/// <param name="RunOnly">The run's rarest-to-commonest top terms the other analysis drops.</param>
/// <param name="OtherOnly">The other analysis's top terms the run's analysis never produced.</param>
/// <param name="EmptiedByOtherCount">How many documents the other analysis reduces to zero terms.</param>
/// <param name="EmptiedByRunCount">How many documents only the run's analysis keeps words for.</param>
internal sealed record VocabularyDiff(
    bool Available,
    int RunOnlyCount,
    int OtherOnlyCount,
    IReadOnlyList<AnalysisDiffTerm> RunOnly,
    IReadOnlyList<AnalysisDiffTerm> OtherOnly,
    IReadOnlyList<string> EmptiedByOther,
    int EmptiedByOtherCount,
    IReadOnlyList<string> EmptiedByRun,
    int EmptiedByRunCount);

/// <summary>The verdicts over every judged (query, document) pair, and how many pairs moved.</summary>
/// <param name="Changes">The pairs that moved, capped — <paramref name="ChangedPairs"/> is the whole set.</param>
internal sealed record VerdictDiff(
    int JudgedPairs,
    int ChangedPairs,
    IReadOnlyList<ReachTransition> Transitions,
    IReadOnlyList<ReachChange> Changes);

/// <summary>Everything one <c>--analysis-diff</c> invocation produced for one dataset.</summary>
internal sealed record AnalysisDiffReport(
    string RunDescription,
    string OtherDescription,
    IndexFingerprint RunIndex,
    IndexFingerprint OtherIndex,
    VocabularyDiff Vocabulary,
    VerdictDiff Verdicts);

/// <summary>
/// What a second analysis would change about this corpus: which terms it indexes and which judged
/// (query, document) pairs it would reach differently — the retrieval-level counterpart of the
/// fingerprint, which only counts terms.
/// </summary>
/// <remarks>
/// <para>
/// The fingerprint answers *whether* two indexes differ and by how much; a published figure
/// differing from a run's is a question about which pairs that difference reaches. This compares
/// the run's analysis against a named one through <see cref="DocumentDiagnosis.Measure"/> and
/// <see cref="DocumentDiagnosis.Reach"/> — the same code the per-document diagnosis reads, so a
/// verdict here and a verdict there are one definition rather than two.
/// </para>
/// <para>
/// Verdicts, not ranks: a pair that becomes reachable under the other analysis is a pair some
/// technique <em>can</em> score, not one that is retrieved, and nothing here searches. The pairs it
/// lists are what <c>--diagnose-doc</c> then diagnoses.
/// </para>
/// </remarks>
internal static class AnalysisDiff
{
    /// <summary>Terms listed per side, by document frequency — the ones a reader can place.</summary>
    internal const int TopTerms = 10;

    /// <summary>Pairs listed when a verdict moved. The count above the list is the whole set.</summary>
    internal const int MaxChanges = 20;

    /// <summary>Documents listed per emptied direction.</summary>
    internal const int MaxEmptied = 10;

    public static AnalysisDiffReport Compare(
        BeirCorpus corpus,
        IReadOnlyList<EvaluatedQuery> queries,
        ITextIndex runIndex,
        ITokenizer runTokenizer,
        TokenizerOptions runOptions,
        string runDescription,
        ITextIndex otherIndex,
        ITokenizer otherTokenizer,
        TokenizerOptions otherOptions,
        string otherDescription)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(runIndex);
        ArgumentNullException.ThrowIfNull(runTokenizer);
        ArgumentNullException.ThrowIfNull(runOptions);
        ArgumentNullException.ThrowIfNull(otherIndex);
        ArgumentNullException.ThrowIfNull(otherTokenizer);
        ArgumentNullException.ThrowIfNull(otherOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(runDescription);
        ArgumentException.ThrowIfNullOrWhiteSpace(otherDescription);

        var documents = new Dictionary<string, BeirDocument>(StringComparer.Ordinal);

        foreach (BeirDocument document in corpus.Documents)
            documents.TryAdd(document.Id, document);

        return new AnalysisDiffReport(
            runDescription,
            otherDescription,
            IndexFingerprint.Of(runIndex),
            IndexFingerprint.Of(otherIndex),
            CompareVocabulary(corpus, runIndex, otherIndex),
            CompareVerdicts(
                queries,
                documents,
                runIndex, runTokenizer, DocumentDiagnosis.SurfaceTokenizer(runOptions),
                otherIndex, otherTokenizer, DocumentDiagnosis.SurfaceTokenizer(otherOptions)));
    }

    /// <summary>Prints the report: the two indexes, the vocabulary, then the verdicts.</summary>
    public static void Write(TextWriter writer, AnalysisDiffReport report)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(report);

        writer.WriteLine();
        writer.WriteLine("Analysis diff — two tokenizations of the same corpus:");
        writer.WriteLine($"  run   : {report.RunDescription}");
        writer.WriteLine($"  other : {report.OtherDescription}");

        long termDelta = report.OtherIndex.TotalTerms - report.RunIndex.TotalTerms;

        writer.WriteLine(
            "  index            run        other      delta");
        writer.WriteLine(
            $"  documents        {I(report.RunIndex.Documents),-10} {I(report.OtherIndex.Documents),-10} "
            + I(report.OtherIndex.Documents - report.RunIndex.Documents));
        writer.WriteLine(
            $"  non-empty        {I(report.RunIndex.NonEmptyDocuments),-10} {I(report.OtherIndex.NonEmptyDocuments),-10} "
            + I(report.OtherIndex.NonEmptyDocuments - report.RunIndex.NonEmptyDocuments));
        writer.WriteLine(
            $"  total terms      {I(report.RunIndex.TotalTerms),-10} {I(report.OtherIndex.TotalTerms),-10} "
            + $"{termDelta:+0;-0;0} ({(report.RunIndex.TotalTerms == 0 ? 0 : (double)termDelta / report.RunIndex.TotalTerms):P2})");

        WriteVocabulary(writer, report.Vocabulary);
        WriteVerdicts(writer, report.Verdicts);
    }

    private static void WriteVocabulary(TextWriter writer, VocabularyDiff vocabulary)
    {
        writer.WriteLine();

        if (!vocabulary.Available)
        {
            writer.WriteLine(
                "  vocabulary      : not measured — one of the two indexes cannot enumerate its terms.");
            return;
        }

        writer.WriteLine(
            $"  vocabulary       {I(vocabulary.RunOnlyCount)} terms the run's analysis has alone, "
            + $"{I(vocabulary.OtherOnlyCount)} the other has alone");

        if (vocabulary.RunOnly.Count > 0)
        {
            writer.WriteLine(
                $"    the run's analysis only, by document frequency (top {I(vocabulary.RunOnly.Count)} of {I(vocabulary.RunOnlyCount)}):");

            foreach (AnalysisDiffTerm term in vocabulary.RunOnly)
                writer.WriteLine($"      {term.Term,-24} df {I(term.DocumentFrequency)}");
        }

        if (vocabulary.OtherOnly.Count > 0)
        {
            writer.WriteLine(
                $"    the other analysis only, by document frequency (top {I(vocabulary.OtherOnly.Count)} of {I(vocabulary.OtherOnlyCount)}):");

            foreach (AnalysisDiffTerm term in vocabulary.OtherOnly)
                writer.WriteLine($"      {term.Term,-24} df {I(term.DocumentFrequency)}");
        }

        writer.WriteLine(
            $"  emptied          {I(vocabulary.EmptiedByOtherCount)} document(s) reduce to zero terms under "
            + $"the other analysis{ListOf(vocabulary.EmptiedByOther)}; "
            + $"{I(vocabulary.EmptiedByRunCount)} under the run's analysis{ListOf(vocabulary.EmptiedByRun)}");
    }

    private static void WriteVerdicts(TextWriter writer, VerdictDiff verdicts)
    {
        writer.WriteLine();

        if (verdicts.JudgedPairs == 0)
        {
            writer.WriteLine("  reach verdicts   : no judged (query, document) pair to compare.");
            return;
        }

        writer.WriteLine($"  reach verdicts over {I(verdicts.JudgedPairs)} judged (query, document) pairs:");

        foreach (ReachTransition transition in verdicts.Transitions)
        {
            writer.WriteLine(
                $"    {transition.Run} -> {transition.Other}".PadRight(58)
                + I(transition.Pairs));
        }

        writer.WriteLine(
            $"  changed          {I(verdicts.ChangedPairs)} of {I(verdicts.JudgedPairs)} pairs "
            + $"({(double)verdicts.ChangedPairs / verdicts.JudgedPairs:P2}) moved verdict under the other analysis");

        foreach (ReachChange change in verdicts.Changes)
            writer.WriteLine($"    {change.QueryId} / {change.DocumentId}: {change.Run} -> {change.Other}");

        if (verdicts.ChangedPairs > verdicts.Changes.Count)
            writer.WriteLine($"    (+{I(verdicts.ChangedPairs - verdicts.Changes.Count)} more — the count above is the whole set)");

        writer.WriteLine(
            "  verdicts are reachability, not ranks: a pair that became reachable is one some technique can "
            + "score, not one that was retrieved. Run --diagnose-doc on the ids above for the ranks.");
    }

    private static VocabularyDiff CompareVocabulary(BeirCorpus corpus, ITextIndex runIndex, ITextIndex otherIndex)
    {
        if (runIndex is not IVocabularyIndex runVocabulary || otherIndex is not IVocabularyIndex otherVocabulary)
            return new VocabularyDiff(false, 0, 0, [], [], [], 0, [], 0);

        var runOnly = new List<AnalysisDiffTerm>();

        foreach (string term in runVocabulary.Vocabulary)
        {
            if (otherIndex.DocumentFrequency(term) == 0)
                runOnly.Add(new AnalysisDiffTerm(term, runIndex.DocumentFrequency(term)));
        }

        var otherOnly = new List<AnalysisDiffTerm>();

        foreach (string term in otherVocabulary.Vocabulary)
        {
            if (runIndex.DocumentFrequency(term) == 0)
                otherOnly.Add(new AnalysisDiffTerm(term, otherIndex.DocumentFrequency(term)));
        }

        var emptiedByOther = new List<string>();
        var emptiedByRun = new List<string>();

        foreach (BeirDocument document in corpus.Documents)
        {
            int runLength = runIndex.Contains(document.Id) ? runIndex.DocumentLength(document.Id) : 0;
            int otherLength = otherIndex.Contains(document.Id) ? otherIndex.DocumentLength(document.Id) : 0;

            if (runLength > 0 && otherLength == 0)
                emptiedByOther.Add(document.Id);
            else if (otherLength > 0 && runLength == 0)
                emptiedByRun.Add(document.Id);
        }

        return new VocabularyDiff(
            true,
            runOnly.Count,
            otherOnly.Count,
            runOnly
                .OrderByDescending(term => term.DocumentFrequency)
                .ThenBy(term => term.Term, StringComparer.Ordinal)
                .Take(TopTerms)
                .ToList(),
            otherOnly
                .OrderByDescending(term => term.DocumentFrequency)
                .ThenBy(term => term.Term, StringComparer.Ordinal)
                .Take(TopTerms)
                .ToList(),
            emptiedByOther.Take(MaxEmptied).ToList(),
            emptiedByOther.Count,
            emptiedByRun.Take(MaxEmptied).ToList(),
            emptiedByRun.Count);
    }

    private static VerdictDiff CompareVerdicts(
        IReadOnlyList<EvaluatedQuery> queries,
        IReadOnlyDictionary<string, BeirDocument> documents,
        ITextIndex runIndex,
        ITokenizer runTokenizer,
        ITokenizer runSurface,
        ITextIndex otherIndex,
        ITokenizer otherTokenizer,
        ITokenizer otherSurface)
    {
        int judged = 0;
        int changed = 0;
        var transitions = new Dictionary<(DocumentReach Run, DocumentReach Other), int>();
        var changes = new List<ReachChange>();

        foreach (EvaluatedQuery evaluated in queries)
        {
            foreach ((string documentId, double grade) in evaluated.Graded)
            {
                // A grade of zero is a judged non-relevant document: it is in the file, and it is
                // not something any technique is expected to surface.
                if (grade <= 0)
                    continue;

                // A qrel naming a document the corpus does not hold has nothing to be reachable
                // against; the per-query analysis counts it, and nothing here can measure it.
                if (!documents.TryGetValue(documentId, out BeirDocument? document))
                    continue;

                judged++;

                bool excluded = evaluated.Excluded?.Contains(documentId) ?? false;

                LexicalReach runLexical = DocumentDiagnosis.Measure(
                    runIndex, runTokenizer, runSurface, evaluated.Query.Text, document);
                LexicalReach otherLexical = DocumentDiagnosis.Measure(
                    otherIndex, otherTokenizer, otherSurface, evaluated.Query.Text, document);

                DocumentReach run = DocumentDiagnosis.Reach(
                    excluded, runLexical, LengthOf(runIndex, documentId));
                DocumentReach other = DocumentDiagnosis.Reach(
                    excluded, otherLexical, LengthOf(otherIndex, documentId));

                transitions.TryGetValue((run, other), out int counted);
                transitions[(run, other)] = counted + 1;

                if (run == other)
                    continue;

                changed++;

                if (changes.Count < MaxChanges)
                    changes.Add(new ReachChange(evaluated.Query.Id, documentId, run, other));
            }
        }

        var ordered = transitions
            .Select(pair => new ReachTransition(pair.Key.Run, pair.Key.Other, pair.Value))
            .OrderByDescending(transition => transition.Pairs)
            .ThenBy(transition => transition.Run)
            .ThenBy(transition => transition.Other)
            .ToList();

        return new VerdictDiff(judged, changed, ordered, changes);
    }

    private static int LengthOf(ITextIndex index, string documentId) =>
        index.Contains(documentId) ? index.DocumentLength(documentId) : 0;

    /// <summary>A capped inline list: a corpus emptied by the thousand must not print a wall.</summary>
    private static string ListOf(IReadOnlyList<string> items)
    {
        if (items.Count == 0)
            return string.Empty;

        return " [" + string.Join(", ", items) + "]";
    }

    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string I(long value) => value.ToString(CultureInfo.InvariantCulture);
}

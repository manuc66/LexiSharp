using System.Diagnostics;
using System.Globalization;
using LexiSharp.Demo;
using LexiSharp.Linguistics;

var builder = WebApplication.CreateBuilder(args);

// --corpus selects what gets indexed; the built-in corpus is the default so a fresh clone runs with
// no options and no download. --segmentation selects how separators inside a word are treated.
// Both are read here rather than through the configuration builder, because an unknown value has to
// fail before anything is indexed, with a message that says which values exist.
string corpusKey = DemoCorpusCatalog.BuiltIn;
string segmentationName = DemoAnalysis.DefaultName;
string? dataDirectory = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--corpus" when i + 1 < args.Length:
            corpusKey = args[++i];
            break;
        case "--segmentation" when i + 1 < args.Length:
            segmentationName = args[++i];
            break;
        case "--data-dir" when i + 1 < args.Length:
            dataDirectory = args[++i];
            break;
        case "--help" or "-h":
            Console.WriteLine(Usage());
            return 0;
    }
}

DemoCorpusSet corpus;
WordSegmentation segmentation;

try
{
    corpus = DemoCorpusCatalog.Load(corpusKey, dataDirectory, builder.Environment.ContentRootPath);
    segmentation = DemoAnalysis.Resolve(segmentationName);
}
catch (DemoOptionException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(Usage());
    return 1;
}

// Built here rather than left to the container: the five lanes are built in the constructor, and a
// singleton resolved on first use would put the whole indexing cost inside whichever request
// happened to arrive first — a 26-document corpus hides that, a BEIR one does not.
var stopwatch = Stopwatch.StartNew();
DemoSearchService service;

try
{
    service = new DemoSearchService(corpus, segmentation);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to index {corpus.Label}: {ex.Message}");
    return 1;
}

stopwatch.Stop();

Console.WriteLine(
    $"Indexed {corpus.Documents.Count} documents of {corpus.Label} "
    + $"[{DemoAnalysis.Name(segmentation)}] in "
    + $"{stopwatch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s.");

builder.Services.AddSingleton(corpus);
builder.Services.AddSingleton(service);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/compare", (DemoSearchService service, string? q, int? limit) =>
    string.IsNullOrWhiteSpace(q)
        ? Results.BadRequest(new { error = "The 'q' query parameter is required." })
        : Results.Ok(service.Compare(q, Math.Clamp(limit ?? 6, 1, 25))));

app.MapGet("/api/explain", (DemoSearchService service, string? q, string? lane, string? id) =>
{
    if (string.IsNullOrWhiteSpace(q) || string.IsNullOrWhiteSpace(lane) || string.IsNullOrWhiteSpace(id))
        return Results.BadRequest(new { error = "The 'q', 'lane' and 'id' query parameters are required." });

    var explanation = service.Explain(q, lane, id);
    return explanation is null ? Results.NotFound() : Results.Ok(explanation);
});

app.MapGet("/api/meta", (DemoSearchService service, DemoCorpusSet active) => Results.Ok(new
{
    corpus = new
    {
        active.Key,
        active.Label,
        documents = active.Documents.Count,
    },
    // The analysis is reported because it decides which tokens exist: under flat, "1,000" is the
    // term "000". A page that shows scores without saying how they were produced invites the reader
    // to attribute a difference to the ranking strategy that the tokenizer caused.
    analysis = new
    {
        name = DemoAnalysis.Name(segmentation),
        segmentation = DemoAnalysis.Describe(segmentation),
    },
    // Probed per request rather than captured at startup: which corpora are on disk is a property of
    // the machine, and the page loads this once anyway.
    availableCorpora = DemoCorpusCatalog.Describe(dataDirectory, app.Environment.ContentRootPath),
    documentCount = service.DocumentCount,
    queries = active.SampleQueries,
}));

app.Run();

// app.Run returns when the host shuts down; the value-returning entry point needs it.
return 0;

static string Usage() =>
    $"""
     Usage: dotnet run --project samples/LexiSharp.Demo [--corpus <name>] [--segmentation <name>] [--data-dir <path>]

       --corpus <name>         {DemoCorpusCatalog.BuiltIn} (default), nfcorpus, scifact or arguana.
                               A BEIR corpus is read from the evaluation harness's data directory;
                               the demo does not download it.
       --segmentation <name>   {DemoAnalysis.DefaultName} (default) or flat. {DemoAnalysis.Describe(WordSegmentation.UnicodeWordBoundaries)}.
       --data-dir <path>       Where to look for corpus files instead of searching for them.

     The demo serves on http://localhost:5000 unless ASPNETCORE_URLS says otherwise.
     """;
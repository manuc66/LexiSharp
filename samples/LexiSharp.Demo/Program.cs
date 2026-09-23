using LexiSharp.Demo;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<DemoSearchService>();

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

app.MapGet("/api/meta", (DemoSearchService service) => Results.Ok(new
{
    documentCount = service.DocumentCount,
    queries = DemoCorpus.SampleQueries,
}));

app.Run();

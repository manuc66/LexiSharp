using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LexiSharp.Eval;

namespace LexiSharp.Tests;

/// <summary>
/// A producer's vector directory, written by a test: the manifest and the two TSV files
/// <see cref="SparseVectorSet.Load"/> reads, for a named corpus.
/// </summary>
/// <remarks>
/// Shared by the tests that exercise the loader and by the PostgreSQL sparse lane's live test, which
/// needs a directory on disk like every other measurement of that lane. The files are the format the
/// Python encoder writes, not a convenience shape, so a change to the loader's expectations fails
/// here.
/// </remarks>
internal sealed class SparseVectorDirectory : IDisposable
{
    public SparseVectorDirectory(string dataset = "test")
    {
        Dataset = dataset;
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "lexisharp-sparse-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>The corpus name the files are named after, e.g. <c>test</c>.</summary>
    public string Dataset { get; }

    public string Path { get; }

    public void Write(
        IReadOnlyList<(string Text, string Terms)> documents,
        IReadOnlyList<(string Text, string Terms)> queries,
        bool writeManifest = true)
    {
        File.WriteAllText(
            System.IO.Path.Combine(Path, $"{Dataset}.docs.tsv"), Lines(documents), new UTF8Encoding(false));
        File.WriteAllText(
            System.IO.Path.Combine(Path, $"{Dataset}.queries.tsv"), Lines(queries), new UTF8Encoding(false));

        if (!writeManifest)
            return;

        File.WriteAllText(
            System.IO.Path.Combine(Path, $"{Dataset}.meta.json"),
            """
            {
              "model": "SPLADE-v3",
              "base_model": "naver/splade-v3",
              "license": "CC BY-NC-SA 4.0",
              "model_file": "splade-v3.onnx",
              "model_sha256": "9e472022f478fec8c4afaa397e68f9c90bb5ff98f0f5c0c6bb195cf2b388ab30",
              "quantised": false,
              "truncate": 128,
              "top_k": 64,
              "document_text": "title + \\n + text when the record has a title, else text",
              "tokenizer": "test",
              "runtime": "test",
              "seconds": 1.5,
              "documents": { "texts": 1, "terms_min": 1, "terms_mean": 2.0, "terms_max": 2, "seconds": 1.0 },
              "queries": { "texts": 1, "terms_min": 1, "terms_mean": 1.0, "terms_max": 1, "seconds": 0.5 }
            }
            """,
            new UTF8Encoding(false));
    }

    private static string Lines(IReadOnlyList<(string Text, string Terms)> texts) =>
        string.Concat(texts.Select(text => $"{SparseVectorSet.HashText(text.Text)}\t{text.Terms}\n"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is not a test failure.
        }
    }
}

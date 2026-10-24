# LexiSharp.Cli

A .NET tool for [LexiSharp](https://www.nuget.org/packages/LexiSharp/): search a corpus from the
terminal, keep it on disk, and measure a retrieval configuration.

```bash
dotnet tool install -g LexiSharp.Cli
```

## Search a directory

```bash
lexisharp search --query "rank fusion"          # the current directory
lexisharp search ./docs --query "rank fusion"   # one you name
```

The directory is scanned and indexed in memory, then searched; nothing is written. Text files and
Markdown are read, and `--analyzer default|english` picks the tokenizer.

When the directory holds a store, the store is searched instead of the files: the directory
itself if it is a store, else its `.lexisharp` subdirectory. The store records the analyzer it was
built with, so `--analyzer` is refused against one rather than silently reading a different index.

## Keep the corpus on disk

```bash
lexisharp segment build  ./docs                    # into ./.lexisharp
lexisharp segment build  ./docs --store ./index    # or wherever you like
lexisharp segment search ./index --query "bm25"
lexisharp segment info   ./index                   # counts, without searching
```

`segment add`, `segment remove --id <doc-id>` and `segment commit` change an existing store. The
segments outlive the process, so a restart does not re-tokenize the corpus.

## Measure a configuration

`benchmark` runs one or more configurations over a corpus with labelled queries and prints nDCG,
MAP, MRR and recall; `diff` compares two of them query by query, and `baseline` / `verify` record
and replay a run so that a change in the ranking is a reviewable diff.

```bash
lexisharp benchmark ./corpus --queries queries.json --qrels qrels.tsv --configs bm25,hybrid
lexisharp diff ./corpus --queries queries.json --qrels qrels.tsv --baseline bm25 --candidate hybrid
```

`--queries` takes a JSON object `{"id": "text"}` or a TSV `id<TAB>text`; `--qrels` takes a TSV of
`qid<TAB>docid[<TAB>grade]`. `lexisharp benchmark --help` lists every option and every configuration
name.

## Source and documentation

- [The library](https://github.com/manuc66/LexiSharp)
- [Documentation](https://manuc66.github.io/LexiSharp/)
- [Evaluation harness and metrics](https://github.com/manuc66/LexiSharp/blob/main/docs/evaluation.md)

MIT.

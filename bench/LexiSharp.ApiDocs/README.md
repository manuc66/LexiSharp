# api-docs

Build tooling. Generates `docs/api.md` from the compiled assemblies, and fails the build when the
committed page and the assemblies disagree.

Internal, not shipped, and not a benchmark. It reads the build output and writes one Markdown
file; it never touches the source tree.

## Why a generated page

The library's pitch is that everything is an interface you can plug into, and the site had no
page listing them. Writing one by hand puts 32 contracts and 175 types in a table nobody
maintains, and the first type added without updating it is a wrong page that stays wrong.

So the page is a *function* of the build: read the assemblies, read the XML documentation they
ship, render. The contributor adds a type; CI says the page moved; they run `write`. That is the
whole maintenance story, and it is why the page can be trusted to describe the surface.

## What it reads

Per public type: its name, whether it is an interface, record, class, struct, enum or delegate,
the base type and interfaces it declares, the first paragraph of its own XML `<summary>`, and the
guide page that covers its namespace.

Nothing is loaded and nothing is resolved — contracts are read as the metadata names them — so a
package whose own dependencies are not on disk reads as cleanly as the core one. The four
packages are written out in the source rather than globbed over `bin/`, because a glob silently
drops a package the current build did not produce, and a reference page missing a package is
worse than a build that stops.

## The output is a pure function of the build

No timestamp, no machine path, no build hash, no locale-dependent formatting, `\n` line endings
whatever the platform, UTF-8 without a BOM. Two runs on two machines produce the same bytes, which
is what lets `check` gate a build on a byte comparison.

## What is a judgement, and where it lives

Two things in the file are not derived from the assembly, and both are in the source rather than
the page:

- **The guide map** (`ApiSurface.GuideFor`) — which page covers which namespace. It is a lookup,
  not a derivation, and a namespace with no entry is rendered with a `—` rather than guessed at.
- **The prose** — the introduction and the note on what the columns mean.

Everything else in the file is read from the build. In particular the *What it is* column is the
type's own summary, trimmed: the page cannot make a claim the source does not make.

## Usage

```bash
dotnet build LexiSharp.slnx -c Release                                  # the input
dotnet run --project bench/LexiSharp.ApiDocs -c Release -- write        # regenerate docs/api.md
dotnet run --project bench/LexiSharp.ApiDocs -c Release -- check        # exit 1 if out of date
dotnet run --project bench/LexiSharp.ApiDocs -c Release -- print        # to stdout, tree untouched
```

`check` is what CI runs, and it reports the first differing line: a log that only says "out of
date" sends the reader to a diff on another machine, and a local run of this is where that diff
should already be on screen.

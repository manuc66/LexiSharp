# cod-shape

Build tooling. Answers one question: **did the generated code change between two builds?**

Internal, not shipped, and not a benchmark. It measures nothing — it reads what the compiler
already emitted.

## What it reads

For every method in an assembly: the length of its IL body, and its decoded local signature. Keyed
by declaring type, name and decoded signature. Nothing else, and in particular **no metadata
token**.

## Why token-free, and not the obvious implementation

Two implementations look right and are not, and both fail as *confident false findings* rather
than as errors:

- **Keying on the raw signature blob.** A signature encodes metadata tokens, and adding one type
  to an assembly renumbers the type table, so every stored signature in the assembly changes with
  it. On one real build here, adding a single record and two enums made this report **1 182
  changed methods**. Signatures are decoded to type names first, so a build that only gained a
  type reports nothing.
- **Keying on the method name alone.** An assembly holds several hundred methods called `.ctor`
  or `Add`. A bare name keeps one entry per name, and the survivors differ by luck of declaration
  order — noise that reads as change, and a method that quietly stops being measured.

It is also **blind to operand values** on purpose: a call retargeted to another method of the
same signature, without moving an instruction boundary, is not reported. So run the source diff
alongside this, not instead of it.

## Usage

```sh
# Write a manifest: sorted, tab-separated, one line per method.
dotnet run --project bench/LexiSharp.CodeShape -c Release -- emit \
  src/LexiSharp/bin/Release/net10.0/LexiSharp.dll LexiSharp.shape.txt

# Compare two assemblies directly. Exit 1 if any method present in both changed cost.
dotnet run --project bench/LexiSharp.CodeShape -c Release -- compare old.dll new.dll

# Summarise one assembly, optionally one type.
dotnet run --project bench/LexiSharp.CodeShape -c Release -- report <assembly> [TypeName]
```

`compare` distinguishes two kinds of difference, and only one of them is a codegen change:

| reported as | meaning | exit code |
|---|---|---|
| `~` present in both, different size or locals | **cost moved** | 1 |
| `-` / `+` on one side only | the source changed | does not fail |

That split is the point. Adding a method is an ordinary source change; treating it as a
performance event would make every edit look like a regression.

## Comparing two CI runs

The `Code shape manifests` step in `ci.yml` publishes one manifest per shipped assembly as a
build artifact, 30-day retention. To ask whether codegen moved between two runs: download the
`code-shape` artifact from each and `diff` them, or feed both to `compare` if you still have the
assemblies.

**Only compare runs built with the same toolchain.** The manifest measures what the compiler
emitted, so a different SDK patch can move it with the source untouched. That is not
hypothetical: the manifests published by the first CI run of this tool gave 135,181 IL bytes for
`LexiSharp.dll`, where a local build of the same commit gave 135,373 — identical method counts,
different code sizes. The repository pins no SDK version (no `global.json`, and the workflows ask
for `10.0.x`, which floats), so the runner and a local machine are not guaranteed to agree. A
`global.json` would be the fix; until then, read a size difference across two runs as a question,
not as a finding.

It is advisory and gates nothing, because every ordinary source edit changes a manifest. A gate
would have to compare against a baseline, and a baseline is only worth having for the assemblies
whose cost actually matters and whose source changes rarely.

## Tests

`tests/LexiSharp.Tests/CodeShapeTests.cs`, with fixtures emitted at test time by
`TestAssemblyBuilder` rather than committed as binaries, so the difference between the two sides of
each test is visible in the source.

The fixtures exist to make the traps reproducible, which shapes their design: a method takes a
parameter whose type is declared in the same assembly, and that type is declared *after* the types
`extraTypes` adds. Without both, the signature encodes no token and a broken implementation
passes — which it did, until the tests were checked against a deliberately broken one.

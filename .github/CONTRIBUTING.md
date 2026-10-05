# Contributing

Two documents govern a change here, and they answer different questions. `AGENTS.md` is the honesty
charter and the engineering rules — read it first and treat it as binding. This file is the writing
rules for `CHANGELOG.md` and the mechanics of a pull request.

Where this file and `AGENTS.md` appear to disagree, `AGENTS.md` wins and the disagreement is a bug in
this file. Two of its rules below exist because of that, and they are marked **[charter]**.

## What a changelog entry is for

The reader is a user or an integrator of the library, not a colleague on this project. An entry
answers two questions and stops:

1. **What changed in this release?**
2. **What does it do to my code or my project?**

Everything else belongs somewhere else, and "somewhere else" is usually a page under `docs/`.

### Include

- **User-visible impact.** What the feature or fix does, from outside the assembly.
- **Defaults.** Whether an option is on or off out of the box — `default: false`, stated, not implied.
- **Migration.** What to edit when an API changed: a renamed type, a moved member, a new argument.
- **A headline figure, with its source.** One number is welcome when the impact is large, and it must
  point at the thing that replays it. `CHANGELOG.md`'s own header says it: a number written only in
  this file is an assertion. The pinned figures live in `bench/LexiSharp.Eval/reference/pinned.json`
  and are checked by

  ```bash
  dotnet run --project bench/LexiSharp.Eval -c Release -- --verify-reference
  ```

### Exclude

- **Investigation narratives and post-mortems.** "We first thought…", "the earlier entry said X but…".
  A correction is not a change; the present tense is what a page carries. The *finding* behind a
  correction — the mechanism, the rule, the corrected belief — belongs in `docs/`, as the finding alone.
- **The machine.** Local infrastructure limits ("could not run on this machine", "this host's
  run-to-run error was ±20 %") are not user-facing. What *was* verified is: name the suite, the
  variable that enables it, and what a green run without that variable means. A backend covered only
  by an integration job is covered only by that job, and saying so is not machine trivia.
- **[charter] Uncertainty about a number stays.** "Not measured", "an upper bound, not a result",
  "fitted in-sample", "the best of 125 points" are properties of the figure, written for the reader,
  and deleting them makes the figure a lie. What goes is the *host-specific* number, not the
  consequence: keep "the benchmark could not resolve it, so no timing claim is made", drop "±5 % to
  ±25 % on this host".
- **[charter] Withdrawals of published figures stay, in one entry.** If a release asserted a figure
  and the figure was wrong, the changelog owns the provenance: name the figure, say why it fell, and
  point at `docs/` for the measurement. A reader carrying the number has to be able to match it to the
  entry that withdrew it. This applies to figures a *tagged release* carried. A claim that landed
  after the tag and no release ever asserted needs the correction in `docs/`, not a withdrawal here —
  and `git tag --contains` is how you tell which case you are in.

## Format

Keep a Changelog, and Semantic Versioning with the 0.x caveat the header states: nothing is frozen
before 1.0, and a minor bump may break the public API.

- One section per category per release: `Added`, `Changed`, `Deprecated`, `Removed`, `Fixed`,
  `Security`. **A category appears once.** Two `### Added` blocks under one version split the release's
  additions into two lists with no ordering principle and put a wall in front of anyone scanning for
  what is new.
- Date an unreleased section when you release it (`## [0.9.0] — 2026-10-05`). Do not add a date to a
  section that is already published; the absence is the record of when it shipped.
- Entries are paragraphs, not one-liners, and they lead with the type or option name in bold so a
  reader scanning the list can find theirs.

## Before you open the pull request

Commit by kind: a defect fix, a new option, a test, a measurement harness are four commits. One commit
per working session produces a review nobody can read, and it arrives entangled with the change that
would have been dropped without it.

- A test lands **with** the code it tests, in the same commit. A commit that adds a source file and
  leaves its test behind reads as untested to the next person.
- Stage by path and read `git status` for all three states — modified, untracked, deleted. Tracked and
  untracked files are not separable in this repository, so `git add -u` produces a tree that does not
  compile, and `git add -A` stages whatever else is in the tree.
- Assume someone else is in the working tree.
- Run `dotnet run --project bench/LexiSharp.ApiDocs -c Release -- check` if you touched a public type's
  documentation. `docs/api.md` is generated from the assemblies and CI fails when it drifts.

Builds on this host need `MSBUILDDISABLENODEREUSE=1 MSBuildEnableWorkloadResolver=false`, and the
Postgres-backed suites run only with `POSTGRES_TEST_CONNECTION` set. Both are properties of the
environment, not of the change, so neither belongs in the changelog.
### What

<!-- One or two sentences: the change from the outside. Type or option name in bold. -->

### Why

<!-- The problem, not the investigation. No "we first thought", no post-mortem. -->

### Checklist — change

- [ ] Commits are split by kind (fix / option / test / harness), not one per session.
- [ ] The test for any new or changed behaviour is **in the same commit** as the code.
- [ ] `dotnet test` passes; suites needing `POSTGRES_TEST_CONNECTION` either ran with it or are marked
      in the PR body as covered by CI only.
- [ ] `dotnet run --project bench/LexiSharp.ApiDocs -c Release -- check` passes if a public type's
      documentation changed.
- [ ] `bench/LexiSharp.Eval --verify-reference` passes if a pinned figure moved.

### Checklist — changelog

- [ ] The entry says **what changed** and **what it does to a caller's code**.
- [ ] Defaults are stated (`default: false`), and migration steps are given for an API change.
- [ ] No investigation narrative, no post-mortem, no history of an earlier entry.
- [ ] No local machine or infrastructure detail. If verification was partial, the entry names the
      suite and the variable that enables it.
- [ ] Any figure carries its source — the `pinned.json` row or the doc section — so it can be replayed.
      A number that appears only in the changelog is an assertion.
- [ ] Uncertainty **about a figure** is kept ("not measured", "upper bound", "fitted in-sample",
      "best of 125"). Uncertainty **about the host** is dropped.
- [ ] A withdrawn figure is named in one entry with the reason, under the release that published it.
- [ ] One section per category per release; no category appears twice under one version.
- [ ] Unreleased sections carry a date only when released; published ones are left as they shipped.

### Checklist — honesty

- [ ] Every performance or quality claim in the diff was measured, in this session, against a baseline
      taken in the same session.
- [ ] No number is asserted that no code path in this repository can produce.
- [ ] A measurement whose baseline is a past build names which build the other column is.
- [ ] An "upper bound" is labelled as one.
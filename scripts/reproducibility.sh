#!/usr/bin/env bash
# Unattended reproducibility sweep. Answers one question with evidence: which of this
# repository's outputs are reproducible, and which are not, and on what.
#
# The distinction the sweep is built around, because getting it wrong is how a number
# gets believed: some outputs are *supposed* to be bit-identical, some are supposed to
# move. The golden master's score fingerprints are the first kind - the summation order
# is fixed by the sorted posting lists, so the same corpus must fold to the same token
# on any machine. Timings are the second kind: they are not claimed to travel, and a
# sweep that reported their variance as a failure would be reporting the truth as a
# defect. So each stage declares which it is, and the exit code counts only the first.
#
# Usage:  scripts/reproducibility.sh [--quick|--full] [--repeats N]
#           --quick  skip the stages that cost hours (default)
#           --full   run them too: every corpus, the benchmark suite
#           --repeats N   determinism repetitions (default 5)
#
# Writes every log and machine-readable result under artifacts/reproducibility/ and
# prints a summary table. Exits 1 if any stage that must be stable was not.

set -uo pipefail

# MSB4014: this machine's workload manifests were removed by package management, and the
# resolver fails hard on them. Every dotnet invocation in this repository needs this, and
# forgetting it is a confusing error rather than an obvious one.
export MSBuildEnableWorkloadResolver=false
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

MODE=quick
REPEATS=5
while [ $# -gt 0 ]; do
  case "$1" in
    --quick)   MODE=quick ;;
    --full)    MODE=full ;;
    --repeats) REPEATS="$2"; shift ;;
    -h|--help) sed -n '2,22p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done

STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
OUT="artifacts/reproducibility/$STAMP"
mkdir -p "$OUT"
RESULTS="$OUT/results.tsv"
GOLDEN="bench/reference-corpus/golden/rankings.txt"
CONFIGS="bm25,bm25-semantic,bm25f,bm25+,bm25l,bm25-proximity-full"
COMMON=(--queries bench/reference-corpus/queries.json --qrels bench/reference-corpus/qrels.tsv)

# Pinned rather than left to the default. The harness defaults --jobs to ProcessorCount-1, which
# means a sweep run on a workstation and the same sweep run on a CI runner reduce the per-query scores
# over a different number of partitions. The reduction is deterministic, so the *numbers* do not move,
# but neither the runtime nor the wall-clock line in the log is then comparable between the two, and a
# sweep whose own timings drift with the host is a sweep nobody can read. Cap 8 as well: past that the
# harness is contending with itself, and the sweep is not measuring the library.
JOBS="${LEXISHARP_EVAL_JOBS:-8}"

# The scores a reference run recorded, tab-separated as "query id, document id, score" — the third
# column at the precision the run file carries. The ArguAna stage compares against it pair by pair,
# which is the only comparison that can tell two implementations apart: a metric sees an order, and an
# order can agree while every score behind it differs.
#
# Under artifacts/, which .gitignore already covers, and deliberately not in the repository. These
# bytes come from running the reference's own searcher over its own index; they are evidence, not
# source, and a reader who wants to re-derive them should be able to. Committing them would make the
# 15,466-of-15,466 claim verifiable on a machine that never ran anything, which is not the same claim.
# Point LEXISHARP_REFERENCE_SCORES elsewhere to compare against a different run.
#
# The qrels travel the same way, for the same reason, and are needed by the independent evaluator the
# ArguAna stage runs. Without either file the stage still measures the metric and says plainly that
# the bit-level half did not run.
REFERENCE_SCORES="${LEXISHARP_REFERENCE_SCORES:-artifacts/reference/arguana-reference-scores.tsv}"
ARGUANA_QRELS_DEFAULT="artifacts/reference/arguana-qrels.trec"

# stage <name> <must-be-stable: yes|no> <description>; body reads stdin.
declare -a ROWS=()
FAILED=0

stage() {
  local name="$1" must="$2" desc="$3"
  shift 3
  local log="$OUT/$name.log"
  local start; start=$(date +%s)
  printf '\n=== %s ===\n%s\n' "$name" "$desc"

  # A subshell, so a stage that crashes or calls exit takes only itself down. A sweep meant to
  # run unattended must not lose the remaining stages to one segfault or one out-of-memory kill.
  if ( "$@" ) > "$log" 2>&1; then status=pass; else status=fail; fi
  local elapsed=$(( $(date +%s) - start ))

  printf '  %s  (%ss)  %s\n' "$status" "$elapsed" "$desc"
  printf '%s\t%s\t%s\t%s\n' "$name" "$must" "$status" "$elapsed" >> "$RESULTS"

  if [ "$status" = fail ] && [ "$must" = yes ]; then
    FAILED=$((FAILED + 1))
    echo "  --- last 15 lines of $log ---"
    tail -15 "$log" | sed 's/^/  | /'
  fi
  return 0
}

# Stages that need a body: define with closures.
build()            { dotnet build LexiSharp.slnx -c Release 2>&1 | grep -E ": error |: warning |Build succeeded|erreur" | tail -40; }
# --no-incremental, not a delete of some scratch directory: the point is to force a full
# recompilation, and an incremental build that reuses yesterday's obj proves nothing about
# whether this tree compiles from nothing. The first version of this removed a path that
# does not exist, so the stage passed in 3 seconds on a warm obj while claiming to be a
# clean build.
build_clean()      { dotnet build LexiSharp.slnx -c Release --no-incremental 2>&1 | tail -20; }
unit_tests()       { dotnet test LexiSharp.slnx -c Release --no-build --logger "console;verbosity=minimal" 2>&1 | tail -6; }
golden_verify()    { dotnet run --project bench/LexiSharp.Cli -c Release --no-build -- verify bench/reference-corpus/corpus "${COMMON[@]}" --configs "$CONFIGS" --top-k 5 --against "$GOLDEN" 2>&1; }
# The whole log, not a tail. A truncated log kept the verdict and dropped the evidence: the
# first version showed "OK" with four of the eleven checks visible, which reads as proof and
# is not. A stage that cannot show its work has not shown its work.
pins_verify()      { dotnet run --project bench/LexiSharp.Eval -c Release --no-build -- --data bench/LexiSharp.Eval/data --verify-reference --jobs "$JOBS" 2>&1; }

# Re-record to a scratch file and byte-compare against the committed one. This is the
# cross-machine check: the file in git was recorded on whatever machine wrote it, and this
# run re-derives every score from scratch. cmp is byte equality, not a tolerance.
golden_rederive()  {
  local scratch="$OUT/reranked.txt"
  dotnet run --project bench/LexiSharp.Cli -c Release --no-build -- baseline bench/reference-corpus/corpus \
    "${COMMON[@]}" --configs "$CONFIGS" --top-k 5 --out "$scratch" > /dev/null 2>&1
  if cmp -s "$GOLDEN" "$scratch"; then
    echo "byte-identical to the committed baseline ($(grep -c '^\S' "$GOLDEN") lines)"
  else
    echo "DIFFERS from the committed baseline:"
    diff "$GOLDEN" "$scratch" | head -20
    return 1
  fi
}

determinism() {
  local i
  for i in $(seq 1 "$REPEATS"); do
    if ! dotnet run --project bench/LexiSharp.Cli -c Release --no-build -- baseline bench/reference-corpus/corpus \
        "${COMMON[@]}" --configs "$CONFIGS" --top-k 5 --out "$OUT/rep-$i.txt" > /dev/null 2>&1; then
      echo "repetition $i failed to record"; return 1
    fi
  done
  local ref="$OUT/rep-1.txt" i
  for i in $(seq 2 "$REPEATS"); do
    if ! cmp -s "$ref" "$OUT/rep-$i.txt"; then
      echo "repetition $i differs from repetition 1:"; diff "$ref" "$OUT/rep-$i.txt" | head -10; return 1
    fi
  done
  echo "$REPEATS repetitions, all byte-identical"
}

# One environment knob at a time, because the point is to attribute a difference to a
# single cause. These are the knobs that plausibly touch instruction selection, and
# therefore the only ones worth spending a run on. must=stable: a difference here is
# either a finding or a false alarm, and either way it is not something to ignore.
env_matrix() {
  local label="$1"; shift
  local before="$OUT/env-before.txt" after="$OUT/env-$label.txt"
  dotnet run --project bench/LexiSharp.Cli -c Release --no-build -- baseline bench/reference-corpus/corpus \
    "${COMMON[@]}" --configs "$CONFIGS" --top-k 5 --out "$before" > /dev/null 2>&1
  if env "$@" dotnet run --project bench/LexiSharp.Cli -c Release --no-build -- baseline bench/reference-corpus/corpus \
      "${COMMON[@]}" --configs "$CONFIGS" --top-k 5 --out "$after" > /dev/null 2>&1; then
    if cmp -s "$before" "$after"; then
      echo "$label: byte-identical to the default run"
    else
      echo "$label: DIFFERS from the default run"
      diff "$before" "$after" | head -10
      return 1
    fi
  else
    echo "$label: the run did not complete under these settings"; return 1
  fi
}

code_shape() {
  # Two independent reads of the same assembly must produce the same manifest. This is a check
  # on the *tool* rather than on the code: the CI job publishes a manifest every run, and if the
  # manifest were not stable the artifact would be unreadable as evidence. The IL byte count is
  # printed because it is the number a manifest from another host is compared against, and a
  # difference there is a question about the compiler - see bench/LexiSharp.CodeShape/README.md.
  local a="$OUT/shape-1.txt" b="$OUT/shape-2.txt"
  local dll="src/LexiSharp/bin/Release/net10.0/LexiSharp.dll"
  dotnet run --project bench/LexiSharp.CodeShape -c Release --no-build -- emit "$dll" "$a" > /dev/null 2>&1
  dotnet run --project bench/LexiSharp.CodeShape -c Release --no-build -- emit "$dll" "$b" > /dev/null 2>&1
  if cmp -s "$a" "$b"; then
    echo "manifest is stable across two reads of the same assembly"
  else
    echo "the same assembly produced two different manifests:"; diff "$a" "$b" | head -10; return 1
  fi
  echo "IL bytes, for comparison against a manifest from another host:"
  dotnet run --project bench/LexiSharp.CodeShape -c Release --no-build -- report "$dll" 2>&1 | tail -6
  return 0
}

eval_corpus() {
  local dataset="$1"; shift
  dotnet run --project bench/LexiSharp.Eval -c Release --no-build -- --data bench/LexiSharp.Eval/data \
    --dataset "$dataset" --no-tuned --jobs "$JOBS" "$@" 2>&1 | grep -E "^\s*(BM25|BM25F|BM25\+|BM25L|TF-IDF)" | head -20
}

# The parity row that used to be a gap: ArguAna at the reference's own k1 and b under the reference
# analysis, which now reproduces 0.3970. Gated, because the gap it replaced was 0.033 of nDCG and a
# silent regression back into it would otherwise pass every other stage here. Three of the settings it
# needs are opt-in, so they are named here rather than hidden in a preset: --analyzer uax29 selects
# the reference segmentation, --reference-index-statistics checks the index against the reference's own
# term count before any score is computed, and --query-term-frequency counts a repeated query term once
# per occurrence. Query syntax needs no flag: this harness leaves SearchOptions.ParseQuerySyntax at
# false by default, which is what a corpus of prose needs, and --query-syntax turns it on.
#
# The run is written out so the per-query comparison is possible with trec_eval and not only in
# aggregate. --index-cache is not used here: it is for iterating on a corpus, and a sweep that trusted a
# cache would be checking that the cache was written correctly rather than that the index is.
arguana_parity() {
  local run="$OUT/arguana-parity-run.txt"
  local scores="$OUT/arguana-parity-scores.txt"
  # The three reproduction flags are the point of this stage and are not decorative. The reference
  # scores its terms in single precision against a reciprocal table, keeps the query-side frequency in
  # the weight instead of scoring a term once per occurrence, leaves the k1+1 out of the numerator, and
  # rounds what it returns to four decimals before walking down each run of near-equal scores. Without
  # all four the ranking is right and the scores are not: measured against a run that reference's own
  # searcher produced, 6.18% of 14,168 paired scores match on the raw bits with only the idf count
  # fixed, and 100.00% with all of it. Each is off by default; they are here because a run file is
  # compared score by score.
  dotnet run --project bench/LexiSharp.Eval -c Release --no-build -- --data bench/LexiSharp.Eval/data \
    --dataset arguana --analyzer uax29 --exclude-query-doc --reference-bm25 0.9,0.4 \
    --ndcg-gain linear --query-term-frequency --omit-saturation-constant \
    --single-precision-bm25 --reference-score-rounding --no-tuned \
    --jobs "$JOBS" --reference-index-statistics --top-k 1000 --run "$run" 2>&1 \
    | grep -E "nDCG@10|recall@|Index fingerprint|terms |conventions" | head -12
  echo "run written to $run"

  # And the raw scores, on the depth the reference's own run file carries, so the bit-level claim can be
  # checked rather than taken on trust. It is one line of awk: a mismatch on the score is a mismatch.
  dotnet run --project bench/LexiSharp.Eval -c Release --no-build -- --data bench/LexiSharp.Eval/data \
    --dataset arguana --analyzer uax29 --exclude-query-doc --reference-bm25 0.9,0.4 \
    --ndcg-gain linear --query-term-frequency --omit-saturation-constant \
    --single-precision-bm25 --reference-score-rounding --no-tuned \
    --jobs "$JOBS" --reference-index-statistics --top-k 11 --run "$scores" >/dev/null 2>&1
  if [ -s "$REFERENCE_SCORES" ]; then
    awk 'NR==FNR { s[$1" "$2]=$3; next }
         { k=$1" "$3; if (k in s && s[k]!=$5) { d++; if (d<=3) print "  differe: " k " nous " s[k] " reference " $5 } }
         END { printf "  %d scores sur %d different sur le bit\n", d+0, FNR }' \
      "$REFERENCE_SCORES" "$scores"
  else
    echo "  pas de scores de reference a $REFERENCE_SCORES : la metrique ci-dessus est mesuree, le bit non"
  fi

  # The index fingerprint is the cheap half of this stage and it is checked above; the expensive half is
  # the score. Both matter: a score reproduced over a differently-analysed index is not a reproduction.

  # And the metric scored by something that is not this repository. The harness computing its own
  # nDCG and agreeing with itself is a weaker claim than an independent evaluator reading the run file
  # this stage just wrote, so trec_eval is asked to read it when it is on the machine. Measured with
  # trec_eval 9.0.8: ndcg_cut_10 0.3970 and recall_100 0.9324, against 0.3970 and 0.9324 published.
  local evaluator="${TREC_EVAL:-trec_eval}"
  local qrels="${ARGUANA_QRELS:-$ARGUANA_QRELS_DEFAULT}"
  if command -v "$evaluator" >/dev/null 2>&1 && [ -s "$qrels" ]; then
    echo "  $evaluator on the run file above:"
    "$evaluator" -m ndcg_cut.10 -m recall.100 "$qrels" "$run" | sed 's/^/    /'
  else
    echo "  $evaluator indisponible ou qrels absents : l'egalite au bit ci-dessus reste verifiee,"
    echo "  le nDCG@10 affiche au-dessus est mesure par ce harnais et n'est pas confirme a l'exterieur."
  fi
}

# The ArguAna exclusion comparison, kept as a stage because it is the measurement that found a
# defect: the query's own document is the closest thing to the query, and the option that removes it
# is what the published figure was produced with. Needs-stable, because both rows are pinned and both
# reproduce. Expensive: one index build and 1,406 long queries per row.
arguana_exclusion() {
  local out="$OUT/arguana-exclusion.tsv"
  : > "$out"
  local p k1 b
  for p in 0.9,0.4 1.5,0.75; do
    k1="${p%%,*}"; b="${p#*,}"
    local extra=()
    [ "$p" = "0.9,0.4" ] && extra=(--exclude-query-doc)
    local row
    # The label is printed with a space after the comma, so the pattern has to carry one. Matching the
    # parameters exactly rather than a prefix of them is what makes this stage able to fail: written as
    # "k1=$p" it never matched, the column came out empty, and the range check below failed on that
    # rather than on a number. The stage was marked stable and had never run green.
    row=$(dotnet run --project bench/LexiSharp.Eval -c Release --no-build -- \
      --data bench/LexiSharp.Eval/data --dataset arguana --no-tuned --analyzer english \
      --jobs "$JOBS" --reference-bm25 "$p" --query-term-frequency --ndcg-gain linear "${extra[@]}" 2>&1 \
      | grep -F "BM25 (k1=$k1, b=$b)" | head -1)
    printf '%s\t%s\t%s\n' "$p" "$([ ${#extra[@]} -gt 0 ] && echo exclude || echo plain)" "$row" >> "$out"
  done
  cat "$out"
  # An empty metric column is a failure of this stage, not a value: it is what a pattern that matches
  # nothing looks like, and it passed the range check below as "outside the range" rather than as
  # "absent". Checked before the range so the two are not confused.
  if awk -F'\t' 'NF < 3 || $3 == "" { found = 1 } END { exit !found }' "$out"; then
    echo "a row produced no metric: the grep above matched nothing"
    return 1
  fi
  grep -qE "0\.3[0-9][0-9]|0\.4[0-9][0-9]" "$out" || { echo "no ArguAna row reached the expected range"; return 1; }
}

benchmarks() {
  dotnet run --project bench/LexiSharp.Benchmarks -c Release --no-build -- --filter '*' --job short 2>&1 | tail -30
}

# ---------------------------------------------------------------- run

printf 'reproducibility sweep, %s, mode=%s repeats=%s\n' "$STAMP" "$MODE" "$REPEATS"
printf 'logs: %s\n' "$OUT"

{
  echo "=== host ==="
  echo "uname:      $(uname -srm)"
  echo "cpu:        $(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2- | xargs)"
  echo "cores:      $(nproc) logical"
  echo "memory:     $(awk '/MemTotal/ {printf "%.1f GiB", $2/1048576}' /proc/meminfo)"
  echo "os:         $(. /etc/os-release 2>/dev/null && echo "$PRETTY_NAME")"
  echo "sdk:        $(dotnet --version 2>/dev/null)"
  echo "runtime:    $(dotnet --list-runtimes 2>/dev/null | awk '/Microsoft.NETCore.App/ {print $2; exit}')"
  echo "rid:        $(dotnet --info 2>/dev/null | awk '/^ *RID:/ {print $2; exit}')"
  echo "arch:       $(dotnet --info 2>/dev/null | awk '/^ *Architecture:/ {print $2; exit}')"
  echo "workloads:  MSBuildEnableWorkloadResolver=$MSBuildEnableWorkloadResolver"
} 2>&1 | tee "$OUT/host.txt"

# Fixed, and said to be fixed: these must be byte-identical on any machine.
stage build-clean        yes "clean Release build, 0 errors and 0 warnings"          build_clean
stage unit-tests         yes "full test suite"                                        unit_tests
stage golden-verify      yes "golden master: 132 rankings, scores bit-for-bit"        golden_verify
stage golden-rederive    yes "re-recorded baseline is byte-identical to the committed" golden_rederive
stage determinism        yes "$REPEATS recordings on this host, byte-identical"        determinism

# The environment matrix is the cross-machine question asked locally: each of these can
# change instruction selection, and therefore the only thing standing between the claim
# and a reassociated sum.
for spec in \
  "no-tiering:DOTNET_TieredCompilation=0" \
  "no-pgo:DOTNET_TieredPGO=0" \
  "no-hwintrinsic:DOTNET_EnableHWIntrinsic=0" \
  "no-avx2:COMPlus_EnableAVX2=0" \
  "no-avx512:COMPlus_EnableAVX512F=0" \
  "no-sse42:COMPlus_EnableSSE42=0" \
  "no-r2r:DOTNET_ReadyToRun=0" \
  "server-gc:DOTNET_gcServer=1" \
  "one-thread:DOTNET_PROCESSOR_COUNT=1" \
  "conserve-gc:DOTNET_gCConserveMemory=9" ; do
  label="${spec%%:*}"; knob="${spec#*:}"
  stage "env-$label" yes "$knob leaves the score bits unchanged" env_matrix "$label" "$knob"
done

# Reported, not gated: a size difference is a question about the compiler, and the tool's
# own README says not to fail on it.
stage code-shape         no  "IL shape under ReadyToRun on and off (reported, not gated)" code_shape

# Quality, at the tolerance the repository actually claims.
stage pins-verify        yes "8 pinned configurations within +/-0.002"                  pins_verify

if [ "$MODE" = full ]; then
  stage eval-nfcorpus    yes "nFCorpus reproduces"                                      eval_corpus nfcorpus --analyzer english
  stage eval-scifact     yes "SciFact reproduces"                                       eval_corpus scifact --analyzer english
  stage eval-arguana     yes "ArguAna reproduces"                                       eval_corpus arguana --analyzer english
  stage arguana-parity   yes "ArguAna at the reference's own parameters reaches 0.3970" arguana_parity
  stage arguana-excl     yes "ArguAna with and without the query's own document"        arguana_exclusion
  stage benchmarks       no  "BenchmarkDotNet suite (timings: reported, never gated)"  benchmarks
else
  printf '\n=== skipped in --quick ===\neval corpora, the ArguAna parity and exclusion comparisons, benchmark suite.\nRe-run with --full.\n'
fi

# ---------------------------------------------------------------- summary

printf '\n================================ SUMMARY ================================\n'
printf '%-22s %-9s %-7s %s\n' STAGE MUST-BE-STABLE RESULT SECONDS
while IFS=$'\t' read -r name must status elapsed; do
  printf '%-22s %-9s %-7s %s\n' "$name" "$must" "$status" "$elapsed"
done < "$RESULTS"

printf '\n'
if [ "$FAILED" -eq 0 ]; then
  echo "every stage that must be stable was stable."
  echo "Timings and IL sizes are reported above and are not claims; see docs/benchmarks.md."
else
  echo "$FAILED stage(s) that must be stable were not. Logs in $OUT"
fi
printf 'results: %s\n' "$RESULTS"
printf '======================================================================\n'
exit $(( FAILED > 0 ? 1 : 0 ))

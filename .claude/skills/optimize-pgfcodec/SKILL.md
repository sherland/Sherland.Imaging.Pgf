---
name: optimize-pgfcodec
description: >-
  Iteratively profile and optimize the managed `PictTag.PgfCodec` implementation for end-to-end
  performance. Use when improving PGF decode, progressive decode, encode, workspace, SIMD, or
  related C# paths: identify the highest-impact managed bottleneck, implement one focused change,
  validate it against the native oracle, commit it, and repeat until no remaining end-to-end
  improvement exceeds 2 percent.
---

# Optimize PgfCodec

Improve the managed codec in an autonomous, measured loop. An active PRD is optional: when the user
provides one, keep its progress log and sub-items current; when there is no PRD, record equivalent
evidence in the repository's PGF benchmark archive and commit history without inventing a plan.
Existing managed-vs-C++ comparison
benchmarks and native-oracle correctness tests are mandatory gates, not disposable scaffolding. The
managed-only performance project is the optimization workbench; it does not replace or weaken the
comparison harness. Do not stop after one successful change or at an arbitrary stage boundary: keep
looping until the exit rule below is satisfied. If profiling reveals additional work, add a scoped
sub-item to the active PRD when one exists, or record it as the next benchmark-archive work item
when no PRD was supplied, and continue it in the same session.

## Workflow

Repeat the following loop for each focused optimization opportunity:

1. Inspect the codec, managed-only performance project, comparison benchmarks, any supplied PRD,
   docs, tests, and git state. Establish or refresh a Release baseline for representative end-to-end
   workloads; preserve unrelated user changes. If no PRD was supplied, use the existing
   `docs/benchmarks/pgfcodec/` archive (or an appropriately named run directory beneath it) for
   immutable measurements and progress notes.
2. Run the managed-only performance project with its normal timing and allocation diagnostics
   (at minimum `MemoryDiagnoser`). Do **not** blindly stack every diagnoser into one timing run:
   profilers and hardware counters perturb timings and often require separate jobs.
3. Run a separate diagnostic pass with the useful available analyzers (for example
   DiagnosticsHub `CPUUsageDiagnoser`, ETW/EventPipe, disassembly, or hardware counters). Capture
   a `.diagsession`/ETL for the relevant scenario and let healthy runs finish.
4. Feed the captured ETL to `bivex/DiagSessionAnalyzer` (or the equivalent installed analyzer),
   filtering to the benchmark worker PID. The reusable wrapper and pinned setup instructions are in
   [`references/diag-session-analyzer.md`](references/diag-session-analyzer.md) and
   [`scripts/`](scripts/); use them instead of retyping fragile command lines. Use the analyzer's
   function/call-tree output to identify hot methods and, where symbols permit, source lines.
   Distinguish codec work from runtime/JIT/startup and benchmark-harness work (such as checksum
   code); never optimize a name merely because it ranks highly in an unfiltered trace.
5. Decide whether the highest-impact candidate can plausibly improve the complete operation by
   more than 2%. Prefer one focused algorithmic, allocation, memory-layout, JIT/vectorization, or
   hot-loop change backed by the profile. Preserve byte-exact behavior, scalar fallbacks,
   Browser/WASM compatibility, and ownership/lifetime contracts.
6. Implement the smallest coherent change and add/update focused tests. If inspection proves that
   no further safe optimization is possible in a function, a concise source comment may record the
   concrete reason (for example, an unavoidable format or ownership boundary). Record profiling
   findings and failed experiments in the supplied PRD/progress log, or in the PGF benchmark archive
   when no PRD was supplied; do not leave dead experimental code or retain a measurable regression
   just to preserve an experiment.
7. Re-run the managed-only performance analysis and compare it with the archived baseline using
   identical fixtures, dimensions, quality levels, runtime, Release configuration, and ownership
   path. Record timings, allocations, profiler findings, and environment. A failed attempt must be
   explicitly recorded with what changed and the measured result; revert it unless it has an
   independently justified correctness or maintainability benefit.
8. Run the full relevant functional suite (codec tests, native-oracle parity/round-trip tests, and
   any affected host or Browser/WASM tests). If anything fails, fix it and return to step 7 before
   accepting the iteration.
9. Run the existing managed-vs-C++ performance comparison matrix. The comparison tests and native
   oracle are permanent gates; never delete, weaken, or replace them. Only accept the optimization
   when the complete managed result improves beyond measurement noise and parity remains green.
10. Update the active PRD/docs with the before/after evidence and mark the sub-item complete when a
    PRD was supplied. Otherwise, write the same evidence to the PGF benchmark archive and identify
    the next candidate there. Commit this accepted optimization batch as one focused commit.
    Immediately begin the next loop at step 2 (or step 1 if the workload/baseline changed). Do not
    stop merely because a stage or one candidate is complete.

Healthy long-running builds, profilers, and benchmarks must be allowed to finish. Interrupt only for
a crash, demonstrable hang/no progress, or a genuinely hours-scale run.

## Exit rule

Exit the autonomous loop only after a fresh diagnostic profile and representative full end-to-end
managed-only benchmark show that no remaining managed optimization candidate can improve the target
workload by more than 2 percent. The threshold applies to the complete operation, not an isolated
kernel; account for normal benchmark noise rather than treating one noisy sample as proof. A
microbenchmark win below 2 percent does not justify added complexity unless it produces a larger
end-to-end gain.

Before exiting, run and record the full relevant comparison matrix, confirm the native-oracle suite is
green, reconcile the supplied PRD when present (otherwise reconcile the benchmark archive), verify
the worktree and commits, and state the final managed and managed-vs-C++ results. Do not claim
completion merely because one optimization shipped or the result feels good enough.

## Guardrails

- Never remove, weaken, or silently change existing C++ comparison benchmarks or native-oracle tests.
  They are regression and parity gates after every optimization.
- Keep profiling-only diagnosers out of normal timing runs unless explicitly requested; profiler
  overhead belongs in a separate diagnostic run.
- Compare like with like: same machine, runtime, configuration, fixture, and ownership path, with
  archived raw reports. Do not infer a general speedup from one noisy sample.
- Do not add SIMD without a measured hotspot, scalar fallback, parity coverage, and an end-to-end
  improvement that survives the 2 percent rule.
- Treat allocation-free as an explicit ownership contract: convenience APIs may allocate owned
  results, while warmed reusable/workspace paths must state what remains caller-owned.

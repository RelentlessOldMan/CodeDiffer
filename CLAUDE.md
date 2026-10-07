# CodeDiffer — project instructions

## ComputeWarden: gate intensive work

This project opts **IN** to the ComputeWarden heavy-compute gate (same as CodeCompass, CodeCarver,
CodeSpawner — they coordinate through the one gate so we don't all peg the box at once). Leases are for
**intense** work only: something that saturates a resource for minutes, not something that merely
touches it. Before such an operation, `computewarden_acquire` (owner `codediffer`, a short description,
`leaseSeconds` covering the expected duration, and `resources` = **exactly** what it saturates, all in
**one** call). If it is not acquired — or status is `UNKNOWN` — do **not** start: report the blocker and
ask whether to wait/retry or proceed. Renew during long runs; `computewarden_release` as soon as it ends.

**Which resources, per operation** (measured on death 1.0.9: 68,661 files, ~94 GB a tree; IRISH is on a
1 Gbit link that tops out ~95–110 MB/s):

| Operation | Lease | Why |
|---|---|---|
| Cold `compare` / `compare3` / `apply` over IRISH (no or partial hash cache: GBs to read) | `network` | Reads at link speed for 10–60 min (189 GB in 33 min). CPU ~1–2 %, ~160 MB RAM: not cpu/ram. |
| Compare reading CodeCompass ledgers (only uncached big + changed files read, e.g. 17 GB) | `network` | Several minutes at link speed. |
| Cold compare of large **local** trees | `disk` | Reads the trees end to end. Add `cpu` only if a run is seen pegging cores (hashing has kept up with I/O so far). |
| `codecompass index` of a big tree on IRISH (e.g. to test its ledger) | `cpu`, `network` | 12 cores parsing at ~70 MB/s for ~20 min a tree. Add `disk` if indexing a local tree. |
| CodeSpawner `gen --preset death` / bulk `mutate` | `cpu`, `disk` (+ `network` if writing to IRISH) | Writes ~90 GB. |
| Timing / benchmark runs (e.g. cold compare3 measurement, old-vs-new build timings) | the resource being measured (`network` for IRISH) | Others on the same resource would skew the numbers, even when the run itself is light. |
| `report --large` / `blockdiff` only when they read tens of GB (many giant files) | `network` or `disk` (where the files are) | Otherwise trivial. |

**No lease** (light or short, even on death):
- Warm `compare` / `compare3` (cache hits: ~0 bytes read, a stat per file, 1–4 min; a few % CPU).
- `verify` against the delta/conflict oracles (a warm compare of the two trees plus the changed files, 30 s–2 min).
- `apply` dry run or `--write` after a warm compare (reads the changed files of three trees, ~1–2 min).
- The HTML report (`--html`, `report <id>`, `write_report`: seconds, a ~20 MB write).
- `dotnet build`, the unit suite (seconds), `dotnet publish`, small fixtures, reads/edits/searches, git.

Nothing here has been seen to need `ram` or `gpu`: compares stream and hold only the verdicts. If a run is
ever seen holding a large share of RAM, add `ram` for it and note it here.

## The ground-truth contract (CodeSpawner coupling)

CodeDiffer's correctness oracle is CodeSpawner's **delta manifest** (`<corpus>-delta.json`), locked
2026-10-02. The stable coupling point is the versioned manifest + the `diffTruthSha` canonical digest,
NOT the corpus bytes. Always hard-assert `_meta.manifestVersion` before trusting a manifest, and
independently reproduce `diffTruthSha` from the four canonical sections before relying on it (reproduce
CodeSpawner's shipped `digest-selftest` golden vector first — same bar CodeCarver held them to). The
full contract is in [`docs/diff-delta-contract.md`](docs/diff-delta-contract.md).

## Stack / conventions

- .NET / C#, matching the family. Libraries target **net8.0** (the fleet floor, so the work box builds
  from source); the CLI and MCP server target **net10.0** (MCP SDK is first-party .NET). Self-contained
  single-file publish is the "grab it and go" distribution.
- `src/CodeDiffer.Core` — the engine (walk · hash · classify · hunks · giant-file block index · digest).
- `src/CodeDiffer.Cli` — the `codediffer` command, built as **`CodeDiffer.Cli.exe`**.
- `src/CodeDiffer.Mcp` — the agent-facing stdio MCP server (paged, bounded queries), its own exe
  **`CodeDiffer.Mcp.exe`**. Same model as CodeCompass (`CodeCompass.Cli.exe` + `CodeCompass.Mcp.exe`):
  two exes over one Core; the CLI does not reference the MCP project.
- `tests/CodeDiffer.Tests` — xUnit; seed it from the prototype-review regression bugs (see DESIGN.txt).
- Public-repo conventions live in `C:\Playground\RelentlessOldMan\Project_Instructions\REPO-SETUP.md`.

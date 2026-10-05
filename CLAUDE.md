# CodeDiffer — project instructions

## ComputeWarden: gate intensive work

This project opts **IN** to the ComputeWarden heavy-compute gate (same as CodeCompass, CodeCarver,
CodeSpawner — they coordinate through the one gate so we don't all peg the box at once). Before
starting a machine-saturating operation, `computewarden_acquire` (owner e.g. `codediffer`, a short
description, a `lease_seconds` covering the expected duration). If it is not acquired — or status is
`UNKNOWN` — do **not** start: report the blocker and ask whether to wait/retry or proceed.
`computewarden_release` when done (crash-safe via lease expiry).

**Gate these** (peg most/all cores or sustain heavy disk/network I/O — any duration):
- A full diff of a large tree (≥ a few GB, or any run touching the ~90 GB `death` corpus, local or the
  `\\IRISH\TestHole\death` share) — the walk + hash + block-diff saturates cores and disk/SMB.
- Generating a test corpus + variant via the vendored CodeSpawner (`gen --preset death`, bulk `mutate`).
- The big test tier (`check.ps1 -Big`) and any release/bench sweep.

**Do NOT gate**: a single `dotnet build`, the fast unit suite, a diff of a small fixture, reads/edits/searches.

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

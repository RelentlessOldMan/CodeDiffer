# CodeDiffer 🔀

**A 2-/3-/n-way diff tool for VERY large trees — ~100 GB, individual files over 1 GB, local or over a
network share — built agent-first so an AI coding agent can diff two trees, see what changed, and port a
change from one to another without being crushed by the scale or drowned in output.**

> ⚠️ **WIP — not yet real-world tested.** This is an early scaffold. The design is locked
> ([`DESIGN.txt`](DESIGN.txt)) and the ground-truth contract with
> [CodeSpawner](https://github.com/RelentlessOldMan/CodeSpawner) is frozen
> ([`docs/diff-delta-contract.md`](docs/diff-delta-contract.md)); the engine is being built against it.
> The output model and scale approach are specified ([`docs/OUTPUT.md`](docs/OUTPUT.md)) but **not yet
> validated on a genuine large tree.**

Classic diff tools (`diff -r`, `git diff --no-index`, WinMerge/Meld/Beyond Compare) are built for
human-scale trees and for humans or shell pipelines. Point one at a dozens-of-GB firmware tree with 1 GB
machine-generated register headers over SMB and it falls over or floods you. An agent diffing such a tree
has a second failure mode: a monolithic result blows its whole context window. CodeDiffer is built for
exactly this case.

## The idea

- **The output is a session, not a document.** A compare writes an on-disk result store (outside the
  compared trees); the agent pages bounded queries against it and a self-contained HTML report lazily
  loads from it. Nothing materializes the full diff up front.
- **A cost-ordered funnel:** walk + stat → hash (cached) → reason → hunks, where only the last tier is
  lazy and per-file. A full 90 GB compare costs the agent ~a few hundred tokens plus the files it opens.
- **Giant files diffed at the block level, never whole** — line-aligned ~1 MB blocks, hash-compared,
  only differing blocks read; the full hunk set is emitted by reference as a `.patch`.
- **Honest at scale** — every cap/sample disclosed; every `modified` carries a reason
  (`content\|eol\|whitespace\|encoding\|binary\|metadata`); never "modified" with an empty diff.
- **Change porting is first-class** — diff A→B, dry-run onto C with per-hunk `applied\|fuzzy\|conflict`;
  3-way merge under diff3 conflict semantics.

Full output model: [`docs/OUTPUT.md`](docs/OUTPUT.md).

## Family

CodeDiffer is one of a family of local, large-repo tools (shared author, stack, and patterns):

- **CodeCompass** — fast local code search / nav over huge repos. CodeDiffer borrows its memory-mapped /
  streaming / SMB-aware / block-sidecar patterns, and (v2) its symbol index for structural-diff
  blast-radius. CodeDiffer has **no dependency** on it; a fresh index is an optional warm hash source.
- **CodeCarver** — reachability slicing: carve a huge repo to the minimal buildable slice.
- **CodeSpawner** — deterministic synthetic corpus generator + ground-truth manifest. CodeDiffer's
  correctness oracle: its bulk `mutate` regenerates the same ~100 GB corpus with a dialable set of
  changes and emits a delta manifest CodeDiffer asserts against.

## Use it

Two exes over one engine (the CodeCompass model):

- **`CodeDiffer.Cli.exe`** — `compare <left> <right>` (status + reason per file; `--patch` for a
  `git apply`-able patch), `compare3 <base> <v1> <v2>` (3-way: v1 only · v2 only · agreed · merged ·
  conflict), `apply <left> <right> <target> [--write]` (port a change set onto a third tree), `diff <a> <b>`,
  `blockdiff`, `verify`. `help` lists the flags.
- **`CodeDiffer.Mcp.exe`** — the stdio MCP server for agents. Register it with
  `claude mcp add codediffer -- <path>\CodeDiffer.Mcp.exe`. Tools: `start_compare` / `start_compare3`
  (background, return an id), `get_summary` (constant size), `list_files` (paged/filtered), `get_file_diff`
  (one file, capped — a 2-way patch, or a 3-way merge with diff3 conflict markers; the overflow goes to a
  file), `get_stats`, `export_changeset` (whole patch to a file), `apply_changeset` (port the changes onto a
  third tree by 3-way merge; dry run unless `write=true`). Compares live in the server's memory for its
  session (the most recent 8).

## Build & test

```powershell
dotnet build CodeDiffer.slnx -c Release
dotnet test  CodeDiffer.slnx -c Release
```

Libraries target **net8.0** (fleet floor); the CLI + MCP server target **net10.0** (requires the .NET 10
SDK to build). See [`DESIGN.txt`](DESIGN.txt) for architecture and the regression suite seeded from a
prototype-diff-tool review.

## Contributing

This is a personal tool, published as-is — **issues and pull requests aren't accepted** (PRs auto-close).
Fork it and make it your own. 🔀

## License

MIT — see [LICENSE](LICENSE). © 2026 RelentlessOldMan.

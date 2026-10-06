# CodeDiffer — Output Model

How CodeDiffer presents a diff of a very large tree (~100 GB, 1 GB+ files, over SMB) without crushing
the machine, the agent, or the human — and without being TMI. This is the companion to the ground-truth
contract in [`diff-delta-contract.md`](diff-delta-contract.md) and the rationale in `../DESIGN.txt`.

## 1. The output is a session, not a document

A compare produces one on-disk **result store** — a manifest of *what changed*, not the change text —
written to a fresh timestamped directory **outside both compared trees** (so a re-run never overwrites,
and we never diff our own reports). From it, three consumers each pull only what they need:

- **Agent (MCP):** paged queries, bounded responses. Never the whole thing.
- **Human (HTML):** a small shell + lazy per-file loads. Never one giant page.
- **Automation:** streaming JSON + `.patch` files by reference.

## 2. The cost funnel — why 90 GB doesn't crush us

Every path is classified before any content is read; we descend only when asked.

| Tier | Work | Cost on ~90 GB | Emits |
|---|---|---|---|
| **0 Walk + stat** | enumerate both trees, read size/mtime *off the enumeration* (no per-file stat round-trips); walk dirs concurrently, bounded | minutes, bounded RAM | per-path status candidate |
| **1 Hash** | SHA-256 of raw bytes where size matches; cached by `(path,size,mtime)`; giants hashed in ~1 MB chunks | streamed | identical vs modified (confirmed) |
| **2 Reason** | classify modified → `content\|eol\|whitespace\|encoding\|binary\|metadata` from hashes + a small head read | cheap | the reason label |
| **3 Hunks** | real line diff — **only for the one file a consumer opens**, bounded by a per-file cap | bounded per file | hunks, or "summary only" |

Tiers 0–2 run over the whole tree and produce only counts + per-file status. **Tier 3 is lazy.** A full
90 GB compare costs the agent roughly a few hundred tokens for the summary plus whatever handful of files
it chooses to open.

## 3. Giant files — diffed at the block level, never whole

Reusing CodeCompass's block/positional sidecar idea: the file is cut into line-aligned ~1 MB blocks,
each with a content hash. To diff header_A vs header_B we compare *block hashes* and only read +
line-diff the blocks that differ — a few MB, not a gigabyte, streamable over SMB. Then we **aggregate**
and return a bounded summary with the full hunk set *by reference*:

```
regmap_block0.h   reason=content   1.04 GB → 1.04 GB
  52,341 replace hunks · +52,341 / −52,341 lines (5.0% of 1,047,000 lines)
  spread: blocks 0–1046 (uniform, stride≈20)         ← the run-rule pattern, detected
  showing hunks 1–20 of 52,341  ·  full patch: compare_7f3a/regmap_block0.h.patch
```

The agent sees ~15 lines; the human sees a collapsed row that virtualizes / links to the `.patch` on
expand. Nobody loads 52k hunks into a context window or a browser DOM.

## 4. What the agent gets (the anti-TMI surface)

`get_summary` — constant size regardless of repo:

```
compare 7f3a · \\IRISH\death\base  vs  \\IRISH\death\v1
51,873 files   89.7 GB
  identical   50,102    (hidden by default)
  modified     1,624    content 1,590 · eol 20 · whitespace 8 · encoding 4 · binary 2
  added          102
  deleted         45
  renamed         18    (3 pure, 15 rename+edit)
changed bytes ≈ 2.1 GB across 12 giant headers + 1,612 source files
top movers: regmap_block0.h (52k hunks), src_9214.c (310), …
```

`list_files(status, path_glob, reason, page)` — paged, filtered, **diffs not included**: path · status ·
reason · hunk count · ±lines. Hides identical by default (mostly noise on big trees).

`get_file_diff(path, context=3, max_lines=2000)` — one file, hard cap, `"capped: summary only, N hunks
total"` fallback for the pathological ones. Diffs inline when small, by reference (a `.patch` handle)
when large.

`get_stats` — totals by status/reason, byte mass, biggest/most-changed.

Change-porting tools: `export_changeset(A→B)` and `apply_changeset(onto=C)` dry-run → per-hunk
`applied | fuzzy | conflict`.

**Built so far** (`CodeDiffer.Mcp.exe`, views in `CodeDiffer.Core/Sessions`): `start_compare` (background,
returns an id, optional wait), `get_summary`, `list_files` (`status`/`path_glob`/`reason`/page, `lines=true`
renders ±lines for just that page), `get_file_diff` (cap `max_lines`, page with `start_line`; overflow written
whole to a `.patch`), `get_stats`, `export_changeset` (to a file, `literal` for a byte-exact `git apply`).
`apply_changeset(target, write=false)`: diff3 with base = left, change side = right, target = C, on the same
merger the 3-way contract verifies. Per region applied | fuzzy (shifted line) | already | conflict (diff3 is
strict: a change touching a target edit conflicts, like git). A file with any conflict is never written;
others are replaced atomically. Binary / EOL- or encoding-only / >16 MB / non-round-trippable files apply
only when C equals the base byte for byte. Compares live in the server process (most recent 8), not yet an
on-disk store.

`start_compare3(base, v1, v2)` (CLI `compare3`): base→v1 then base→v2 (sequential, so the second reuses
the base hashes the first cached instead of re-reading the base over SMB), then every touched path is
v1 only | v2 only | agreed | merged | conflict, the conflict kinds being content, modify/delete,
add/add, rename/rename, path collision, binary, large. Renames are followed (renamed on one side, edited
on the other ⇒ merged at the new name). The same `get_summary` / `list_files` / `get_file_diff` / `get_stats`
serve it; `get_file_diff` shows the merged file with diff3 markers (`<<<<<<< v1 / ||||||| base / ======= /
>>>>>>> v2`). A clean merge equals porting base→v1 onto v2 (tested).

## 5. The human HTML report

Self-contained shell + a sidecar data dir in the result store. Summary tiles; a tree view with folder
roll-ups; **identical hidden by default**; status/reason filter toggles; a path filter box. Each file's
diff is **loaded on expand** from the store (not embedded), so a 90 GB compare is a small page plus data
loaded on demand. Side-by-side and inline modes; intra-line + syntax highlighting; huge per-file diffs
virtualize or link to the `.patch`. **Everything is escaped** (the prototype's injection bug). Written
incrementally to a unique/timestamped dir outside the compared trees.

## 6. Honesty is the guardrail against a silent crush

Every bounded answer says it's bounded (the CodeCompass contract applied to diffs):

- "showing 20 of 52,341 hunks" — a cap is a floor, not a total.
- "2 files exceed the diff cap — compared by hash only" — a skipped file is disclosed, never dropped.
- A `modified` **always** carries a reason; never "modified" with an empty diff (the prototype's
  headline bug: EOL/encoding-only changes reported as modified-with-nothing-to-show).
- `identical` = compared-and-equal, distinct from `not compared` (skipped by a cap).
- Deterministic output: two runs over the same inputs are byte-identical, so a diff of results is real.

## 7. Defaults (tunable)

| Setting | Default | Why |
|---|---|---|
| Hide identical | on (all outputs) | identical is noise on big trees |
| Per-file inline diff cap | ~2,000 lines / 256 KB | above → summary + `.patch` by reference |
| Giant-file threshold | 128 MB | matches CodeCompass streaming; always block-level + by-reference |
| Result store location | fresh timestamped dir **outside** both trees | never overwrite; never diff own output |
| Hash cache | on, keyed `(path,size,mtime)` | second compare of a near-identical tree is seconds |
| SMB walk | concurrent, bounded; metadata off enumeration | overlaps round-trips on a latency-bound share |
| Metadata-only diffing (mode) | **off** (opt-in) | over SMB mode is mostly noise |

Net: the agent pays for a summary plus what it opens; the human gets a shareable shell that lazily
loads; the 1 GB header is touched a few MB at a time and reported as a bounded, honest summary with the
full detail one `get_file_diff` (or one click) away.

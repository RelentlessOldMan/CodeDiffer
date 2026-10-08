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
renders ±lines for just that page), `get_file_diff` (cap `max_lines` and 256 KB, lines over 2,000 characters cut,
page with `start_line`; overflow written whole to `diffs\<path>.patch` in the result directory — the path
mirrored, so two files never share a name; a side gone or locked since the compare is said in one line), `get_stats`, `export_changeset` (to a file, `literal` for a byte-exact `git apply`).
`apply_changeset(target, write=false)`: diff3 with base = left, change side = right, target = C, on the same
merger the 3-way contract verifies. Per region applied | fuzzy (shifted line) | already | conflict (diff3 is
strict: a change touching a target edit conflicts, like git). A file with any conflict is never written;
others are replaced atomically. Binary / EOL- or encoding-only / >16 MB / non-round-trippable files apply
only when C equals the base byte for byte (compared and copied streamed, so a file of any size works). A
lone CR is content: merging across line endings converts only CRLF.
Stopping `apply --write` part way is safe: Ctrl+C stops between files (the report says how many were written
and how many were not reached), each file is written whole via `<file>.codediffer.tmp` and a rename, and
running the same apply again finishes the job — what is done comes out "already", a rename stopped between
writing the new path and deleting the old one is completed (and one already merged into the target's own
edits comes out "already"; a rename without edits, whose new path is only much like the file and not its exact
bytes, is "already" with a note saying it was judged by likeness), and a killed run's temp file is reused. A
case-only rename is left as a conflict (on Windows it is the same file).

**Progress and partial answers (built):** while a compare runs, `get_summary` says what it is doing (listing
files with counts per side · checking contents N/M files, GB read as they stream and MB/s · a rough time
left: by bytes while files are being read, by the recent file rate in the small-file tail, by files when
everything is a cache hit) and lists the differences found so far. Adds and removes are known right
after the walk (flagged: a rename may still pair them up), size-changed files a moment later, same-size
edits as their contents are checked. `list_files` and `get_file_diff` already work on that partial set
(in the order found, so pages don't shift); `get_stats`, the export, apply and the report wait for the end.
A running `compare3` shows each of its two compares and the paths changed on both sides so far. The CLI
draws the same line on stderr (redrawn in place, or a line every 30 s when stderr is redirected). Death
base→v1, warm: all 1,561 differences listed at 0:30 of a 1:40 run; "about 1:20 left" at 0:30 (actual 1:18).

**Cancel (built):** `cancel_compare(id)` stops a running compare or compare3; in the CLI the first Ctrl+C does
the same (a second one quits at once; exit 130). It stops within a chunk per file in flight, in every phase
including rename detection, the streamed reason check of a large file and compare3's merge (death, cold, 25 s
in: stopped in 0.1 s), saves the hashes it already read to the ledgers, and is saved as state
`cancelled`. So a cancelled cold run is not wasted: the next run of the same compare reads only the rest. A
cancelled `--rehash` keeps the old ledger entries it didn't get to; a finished one keeps none it didn't re-read
(nor one dated after it ran, from a clock that was ahead).
Stopping a report (Ctrl+C) stops a giant file's block diff mid-read too.

**Result store (built):** every finished compare — MCP or CLI — is saved to a fresh
`<results>\yyyyMMdd-HHmmss-<id>\` (default `%LOCALAPPDATA%\CodeDiffer\results`, `CODEDIFFER_RESULTS_DIR`
overrides; refused if it would land inside a compared tree). `compare.json` (format/version, kind, state
running → done | failed | cancelled, roots, options, read cost, timings, counts) is written first as "running",
with the process and machine running it, so a crashed compare leaves an honest trace: once that process is gone
it is listed as `stopped` (and `results --prune` may delete it at once; one whose process is seen still running
is never pruned, however long it has run; a "running" one from another machine, or one whose process can't be
checked, is kept a day). A compare still running in another process (another MCP
server, a CLI run) is listed, but only that process can answer about it; it opens here once it is done.
A malformed `compare.json` is reported as a corrupt result, never an error that ends the server; a path with an
unpaired UTF-16 surrogate (legal on NTFS) is saved losslessly, so it reopens as the same file; `changes.jsonl` (2-way, every path) or `v1.jsonl`/`v2.jsonl`/
`entries.jsonl` (3-way) hold the verdicts. Only verdicts are stored, not file contents: any compare_id (or
the directory) reopens without re-comparing (`list_compares`, CLI `results`), and a diff rendered later
from the live trees warns when a file's size no longer matches what was compared. Capped patches, apply
reports and the HTML report go into the same directory.

Renames cost little on a warm compare: the pure (byte-identical) pass hashes only adds and removes whose size
occurs on both sides, in parallel, and uses the hash ledgers (a second run reads nothing for it); the edited
pass reads the text candidates in parallel, skips every pair whose line counts alone rule out the 50% threshold,
and scores the rest in parallel — exactly the contract's similarity, the same pairs as scoring all of them. Both
are counted in the bytes read. The edited pass is bounded, like git's rename limit: past 5,000 × 5,000 candidate
pairs, or 1 GB of their text, it is skipped and a note says so — those files are listed as added and removed
(identical renames are still found), rather than scored for minutes or held in memory.

`start_compare3(base, v1, v2)` (CLI `compare3`): base→v1 then base→v2, sequential, and the second reuses
the first's base listing and the base hashes it proved (no re-listing, no re-reading, not even a per-file
stat of the base; both compares judge the same snapshot of it), then every touched path is
v1 only | v2 only | agreed | merged | conflict, the conflict kinds being content, modify/delete,
add/add, rename/rename, path collision, file/directory clash (one side has a file where the other has a
directory: v2 turns `D/` into a file `D` while v1 adds `D/z.txt`), binary, large. A file one side only moved
(byte-identical) and the other changed in place merges to the change at the new name — binary and large files
too, taken whole. A side with no line break at all has no line-ending style: it never "changes the line endings"
(a CRLF file cut to one line is not CRLF → LF). Renames are followed (renamed on one side, edited
on the other ⇒ merged at the new name). The same `get_summary` / `list_files` / `get_file_diff` / `get_stats`
serve it; `get_file_diff` shows the merged file with diff3 markers (`<<<<<<< v1 / ||||||| base / ======= /
>>>>>>> v2`). A clean merge equals porting base→v1 onto v2 (tested). A file's encoding (BOM, UTF-16) and
line-ending style (LF / CRLF / mixed) merge 3-way like its lines: a side that changed them wins (v2's LF→CRLF
is kept when v1 also edited the file), the same change on both sides agrees, and different changes on both
sides are a conflict of kind `encoding` or `line endings` (both files listed, nothing written); when the lines
conflict too, the file is a `content` conflict written with markers in v1's encoding and line endings, and its
note says the encoding or line-ending conflict as well. A mixed file keeps each line's own ending; a lone CR
stays part of its line.

**Merge overlay (built):** `write_merge(id[, out_dir])` (CLI `compare3 … --merge-out DIR`) writes the merge
as an overlay on v1, never the whole tree: `files\` holds only what the merge changes in v1 (v2's one-sided
changes, clean merges in the merged encoding and line endings, text conflicts with diff3 markers),
`deletes.txt` the v1 paths to delete (v2's deletes, the old side of a move), `conflicts.txt` the conflicts
with markers plus those that can't be one file (binary, large, modify/delete, rename/rename, path collision, file/directory clash, encoding, line endings)
with each side's file, and `OVERLAY.txt` how to apply it: delete the paths in `deletes.txt` from v1, remove the
directories that left empty, then copy `files\` over v1 (deletes first, so a file v2 turned into a directory, a
directory v2 turned into a file, or a case-only rename lands right).
A move's old path is listed only once its new file is written, so a failed read never loses v1's copy. Each
file is written beside its name and renamed into place (a failed write leaves nothing half-written), and a
merge is checked against the compare as it is written: a file whose merge changed since (a tree edited in
between) is listed as not written instead. `INCOMPLETE.txt` is present until the overlay is finished.
Unlike `apply --write` (which skips any file with a conflict), conflicts land in the overlay with markers.

`codediffer apply-overlay <overlay-dir> <target> [--write] [--again]` does those steps (on v1, or better a copy
of it): the deletes, then the directories the deletes left empty, then `files\`. Dry run unless `--write`.
Before touching anything it refuses an unfinished overlay (`INCOMPLETE.txt`), one written from an incomplete
compare, a `deletes.txt` line that is not a plain path inside the target, and a target and overlay inside each
other; it deletes files only, never a directory in a listed path's place, and never writes or deletes through a
symlink / junction in the target (that file is listed as failed). Like `apply`, Ctrl+C stops between files, each is written whole (temp +
rename), and running it again finishes the job (a path already deleted, or already holding the overlay's
bytes, comes out "already" — a case-only rename already done, and a file v2 turned into a directory already in
place, too). The overlay holds `APPLYING.txt` while it runs and `APPLIED.txt` (target, time)
once done with nothing failed — after a failure it stays unapplied, so the same apply can be run again once the
cause is fixed; a second finished apply to the same target is refused unless `--again`, since it would
overwrite conflicts resolved since. On death the dry run onto v1 takes 1.5 s (1,354 files, 33.3 MB, 0 deletes).

Full-scale check (2026-10-05, CodeSpawner `death` 1.0.9 base / v1 / v2 on `\\IRISH\TestHole`, 68,661 base
files, `--threads 4`, warm hash cache): `compare3` in 3:59 (base→v1 124 s, base→v2 109 s, merge 5 s; 0 bytes
re-hashed), 1,723 touched paths → 369 v1 only · 162 v2 only · 251 merged · 941 conflict, 11,371 conflict
regions. `verify death_1.0.9-conflict.json --base --v1 --v2` (30 s): the merge's own decomposition
(11,371 conflict · 32,615 clean) reproduces `conflictTruthSha` exactly. `apply` base→v1 onto v2 (dry run,
1:57) agrees: 620 files clean, 941 conflict, 21,246 hunks applied, 11,371 conflict, 0 fuzzy.

Cold and warm compare3 (2026-10-06, 8 threads cold / 4 warm): **cold, nothing cached: 51:03** — base→v1
32:58 reading 188.8 GB at ~100 MB/s (the link), base→v2 18:00 reading only v2 (94.4 GB, base from the
first compare), merge 5 s, HTML report 6 s; verdicts identical file for file to the warm run. **Warm:
2:13** with the base shared between the two compares (base→v2 44 s), against 2:52 before (84 s). On a
cold run the bytes go first (biggest files first), then a tail of ~58k small files takes ~3.5 min for
0.1 GB, which is why the time-left estimate follows bytes, then the recent files-per-second rate.

CodeCompass v2 ledger at full scale (2026-10-06, CodeCompass 1.0.243): `codecompass index` of base and v1 on
IRISH (66,337 of 68,661 files each; it skips the rest by size), then `compare` base→v1 with an empty
CodeDiffer cache: 129,552 sides from CodeCompass's ledgers, 17.3 GB read (exactly the files CodeCompass
skipped, plus both sides of the 1,561 modified files), 4:19. The 68,661 per-file verdicts are identical to
a run on CodeDiffer's own ledger. Locally, a same-size edit with the mtime put back, a delete + recreate
with the same size and mtime, and a move over an existing file were all re-read and reported modified.

## 5. The human HTML report

Self-contained shell + a sidecar data dir in the result store. Summary tiles; a tree view with folder
roll-ups; **identical hidden by default**; status/reason filter toggles; a path filter box. Each file's
diff is **loaded on expand** from the store (not embedded), so a 90 GB compare is a small page plus data
loaded on demand. Side-by-side and inline modes; intra-line + syntax highlighting; huge per-file diffs
virtualize or link to the `.patch`. **Everything is escaped** (the prototype's injection bug). Written
incrementally to a unique/timestamped dir outside the compared trees.

**Built:** `write_report` / CLI `report <id>` (or `--html` on `compare`/`compare3`) writes `report\` into the
result directory: `index.html` (static shell, no external resources, light/dark), `data/index.js` (the file
list + summary) and one `data/d/N.js` per file diff, loaded by script tag on expand — so it opens from disk
with no server. Tiles double as status filters; path filter (text or glob) and reason filter; a folder tree
(single-child chains compacted, per-folder status badges, children built only when opened); inline or side
by side with intra-line marks; 3-way merges colored by v1/base/v2 section with a conflict stepper (a merged
file is shown as its conflict blocks with context, whole blocks until 3,000 lines or 1 MB, the head of a first
block that alone is bigger; whatever is left out links the whole merged file). Diffs over
3,000 lines or 1 MB are cut (lines over 2,000 characters too) with the whole diff written alongside and
linked; a file whose diff can't be read or rendered is listed with why; Ctrl+C stops rendering and still
writes the report, the rest listed without a diff; files over 16 MB are listed as "large"
unless `--large`; past `--max-diffs` (5,000) files are listed with a note; the footer totals all of it. All
data reaches the page as JSON and is inserted as text, never as HTML. Not built: syntax highlighting.

## 6. Honesty is the guardrail against a silent crush

Every bounded answer says it's bounded (the CodeCompass contract applied to diffs):

- "showing 20 of 52,341 hunks" — a cap is a floor, not a total.
- "2 files exceed the diff cap — compared by hash only" — a skipped file is disclosed, never dropped.
- A `modified` **always** carries a reason; never "modified" with an empty diff (the prototype's
  headline bug: EOL/encoding-only changes reported as modified-with-nothing-to-show).
- `identical` = compared-and-equal, distinct from `not compared` (skipped by a cap).
- A file that can't be read (locked, gone since the walk, access denied) never stops the compare and is never
  called identical: it is listed `[unreadable]` with why (a pair as modified with no reason, an add or remove
  as itself, left out of rename matching), every summary warns, and the CLI exits 3. Files are opened so a
  writer can keep writing (a log being appended to is still read), and the hashes read are kept even when a
  compare fails.
- Symlinks and junctions are not followed (one can point outside the tree, or back into it); each skipped one
  is counted and every summary says how many (compare3's too, per side). A file one tree has where the other
  has a link is not "added" or "removed" but unknown — `[unreadable]`, a `behind a link` conflict in compare3,
  never ported or written as a delete. Other reparse points — OneDrive / cloud placeholders, deduplicated
  files — are ordinary files and directories and are compared. Paths past 260 characters are listed and
  checked like any other; a name ending in '.' or ' ' (which Windows' path API opens as another file), or a
  device name like `nul` (which it opens as the device), is skipped and said, and a timestamp past year 9999 counts as unknown (the file just isn't cached).
- An incomplete compare (a directory that couldn't be listed) is never acted on: `apply`, `apply_changeset`,
  the merge overlay and a whole patch (`--patch`, `export_changeset`) refuse it, since every file under that
  directory would look deleted.
- Names that differ only in case are one file on Windows: a remove and an add like that (`Foo.c` → an unrelated
  `foo.c`) are left to do by hand by `apply`, and in compare3 a v2 file landing on a name a conflict keeps in v1
  is a `path collision`, never written over v1's file.
- A hash ledger that can't be saved after the compare (disk full, a file held open) is a note, never a lost
  result: the next compare reads those files again.
- A command given an option it doesn't take (`--no-chache`), an extra word, or a flag without its value stops
  at once with the usage (exit 64), instead of running an hour without it; so does a bad `-U` / `--max-diffs`
  value, a results directory inside a compared tree, and `verify` given only one of `--base` / `--variant`.
- `whitespace` means only whitespace: the same lines but for indentation, trailing spaces / tabs and the
  spacing round punctuation (`x=1` → `x = 1`). A space that joins or splits a word (`return x` → `returnx`),
  a line joined, split or added, and NBSP / NEL (characters, not whitespace) are `content`. Whitespace is
  ASCII space, tab, VT and FF, whole or streamed alike.
- Among identical files the rename is the best match, not the first listed: `a/LICENSE` and `b/LICENSE`
  identical, `b/LICENSE` renamed to `b/COPYING` and `a/` deleted gives `b/LICENSE → b/COPYING`. Empty files
  never pair as renames, and nor does a file that is only a BOM (an editor's "empty" `.cs`): that is empty text.
- `apply` carries a change's re-encoding with its edits (the right side dropped the BOM, or went UTF-16 →
  UTF-8, and edited a line): the target, still in the base's encoding, is written in the right side's, and the
  note says so. A target that re-encoded the file its own way keeps its own encoding, also said.
- `verify` on a delta with renamed-and-edited files reads each one's hunks against its old path in the base
  (the contract lists them under the new path), and takes the rename as that file's operation; the 3-way
  reconstruction checks a side the manifest records no change for is the base, not assumed to be.
- A malformed manifest, hunk coordinates outside the files, a manifest file missing from the tree and a
  corrupt saved result (a bad status, a number out of range) are errors or failed checks, never a crash.
- Text that isn't valid UTF-8 (a cp1252 / Latin-1 file) is read byte for byte, never with U+FFFD in place of
  the bad bytes: `25°C` → `25±C` is a `content` change, not "the same text, encoding changed". Such a file that
  only gained a UTF-8 BOM is `encoding`, small or large.
- A file is binary when it has a NUL and no BOM: anywhere in it when it is read whole (up to 8 MB), in its first
  8 KB past that. So text that turns binary further in is `binary`, and no patch ever carries raw NULs.
- `codediffer diff` with a file that isn't there is an error (exit 2), never "the whole other file added": an
  added or deleted file is asked for with `/dev/null` (or `NUL`) as the absent side. Both sides get the right
  file's name, so `git apply` changes that file rather than renaming it.
- `verify` fails a hunk whose op doesn't fit its line counts (an `insert` with old lines, a `delete` with new
  ones), as the contract defines them, even when its coordinates rebuild the file.
- A whole patch (`--patch`, `export_changeset`) is for `git apply`: what it can't carry — binary and large
  files, text that isn't UTF-8, unreadable files, the eol/encoding-only notes — is described in `#` lines
  outside any `diff --git` section, which git skips, so the rest still applies; the summary counts them as
  NOT CARRIED. So is a file behind a link in the other tree (a patch would write or delete through it), and a
  path another change's path differs from only in case (`Foo.c` → `foo.c`, or a directory renamed so): on
  Windows `git apply` refuses the whole patch over one of those. A byte-identical rename is a header-only rename, binary or not; a UTF-8 BOM is kept. A rename
  found by line similarity always carries its diff, even at "100% similar" (one line in 2,001, reordered lines,
  line endings only): the list says "(but edited)", and it never counts as pure.
- A file over 8 MB is not decoded whole to find its reason: up to 64 MB it is compared streamed, as bytes
  (BOM, whitespace and line endings normalized byte by byte by the same rule, stopping at the first real difference), so
  a large LF→CRLF change is still `eol`; past 64 MB, or in UTF-16/32, a difference is `content` unchecked.
- `verify` checks CodeDiffer, not only the manifest: with the trees it runs CodeDiffer's own compare and
  requires exactly the manifest's files, reasons and rename similarities (death 1.0.9: 1,561/1,561 and
  1,354/1,354); the 3-way gate needs the exact decomposition, or reconstruction AND a merge conflicting in
  exactly the manifest's files, AND compare3 itself — over the whole trees, lines with their endings —
  conflicting in exactly the manifest's files (death 1.0.9: 941 of 941).
- Deterministic output: two runs over the same inputs are byte-identical, so a diff of results is real.

## 7. Defaults (tunable)

| Setting | Default | Why |
|---|---|---|
| Hide identical | on (all outputs) | identical is noise on big trees |
| Per-file inline diff cap | 2,000 lines / 256 KB, lines over 2,000 chars cut | above → window + hunk map + `.patch` by reference |
| Large-file threshold | 16 MB | above → never line-diffed whole: a block-level diff by reference (`get_file_diff`, `report --large`), a `large` conflict in compare3, byte-for-byte only in `apply` |
| Result store location | fresh timestamped dir **outside** both trees | never overwrite; never diff own output |
| Hash cache | on, keyed `(path,size,mtime)` | second compare of a near-identical tree is seconds |
| SMB walk | concurrent, bounded; metadata off enumeration | overlaps round-trips on a latency-bound share |
| Metadata-only diffing (mode) | **off** (opt-in) | over SMB mode is mostly noise |

## 8. CLI exit codes

| Code | Meaning |
|---|---|
| 0 | done: nothing conflicts and nothing is missing |
| 1 | done, with conflicts (`compare3`, `apply`), a file that failed (`apply-overlay`), a `verify` gate failed, or a prune that could not delete everything |
| 2 | error: a tree or file not found, a refused directory, an unreadable manifest, a failed `--html` / `--merge-out` |
| 3 | done but INCOMPLETE: a directory could not be listed or a file could not be read (the warnings say which) |
| 64 | usage: a missing argument, an unknown option, a flag without its value |
| 130 | stopped by Ctrl+C (a compare, an apply, or a report written part way) |

Net: the agent pays for a summary plus what it opens; the human gets a shareable shell that lazily
loads; the 1 GB header is touched a few MB at a time and reported as a bounded, honest summary with the
full detail one `get_file_diff` (or one click) away.

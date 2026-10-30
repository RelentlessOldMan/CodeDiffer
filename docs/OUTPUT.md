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
| **1 Hash** | SHA-256 of raw bytes where size matches; cached by path, trusted on size, mtime, ChangeTime and file id (see §4); giants hashed in ~1 MB chunks | streamed | identical vs modified (confirmed) |
| **2 Reason** | classify modified → `content\|eol\|whitespace\|encoding\|binary\|metadata` from hashes + a small head read | cheap | the reason label |
| **3 Hunks** | real line diff — **only for the one file a consumer opens**, bounded by a per-file cap | bounded per file | hunks, or "summary only" |

Tiers 0–2 run over the whole tree and produce only counts + per-file status. **Tier 3 is lazy.** A full
90 GB compare costs the agent roughly a few hundred tokens for the summary plus whatever handful of files
it chooses to open.

## 3. Giant files — diffed at the block level, never whole

A file over 16 MB (either side) is never line-diffed or decoded whole. Both files are streamed once through a
content-defined chunker (FastCDC-style cuts: chunks of 16–256 KB, ~64 KB on average, each XxHash128: the hashes
only match chunks; whether the files differ is the compare's byte-for-byte read), in bounded memory whatever their
size, and the two chunk lists are matched: an edit dirties only the chunks around it, so one
inserted byte near the front doesn't shift everything after it. What comes back is a bounded list of **changed byte
ranges**, not line hunks (e.g.):

```
Large files a/regmap_block0.h and b/regmap_block0.h differ: 1,047 changed region(s), 68,616,192 of 1,117,782,016 bytes (6.14%) -> 1,117,782,016 bytes
  replace old[0, +65,536) -> new[0, +65,536)
  replace old[1,048,576, +65,536) -> new[1,048,576, +65,536)
  ... 1,027 more region(s)
```

The agent sees a few lines; nobody loads 52k hunks into a context window or a browser DOM. A patch describes such
a file in `#` lines (copy it by hand); the HTML report lists it, its byte ranges only with `--large`. Where changes
are scattered too widely to align block by block in time, the block diff stops at its work cap and says `coarse`:
its regions are wider than the change, and more is unchanged than they show.

## 4. What the agent gets (the anti-TMI surface)

`get_summary` — constant size regardless of repo:

```
compare 7f3a · \\fileserver\death\base  vs  \\fileserver\death\v1
51,873 files   89.7 GB
  identical   50,102    (hidden by default)
  modified     1,624    content 1,590 · eol 20 · whitespace 8 · encoding 4 · binary 2
  added          102
  deleted         45
  renamed         18    (3 pure, 15 rename+edit)
changed bytes ≈ 2.1 GB across 12 giant headers + 1,612 source files
top movers: regmap_block0.h (52k hunks), src_9214.c (310), …
```

`list_files(status, path_glob, reason, type, hide_noise, page)` — paged, filtered, **diffs not included**: path · status ·
reason · hunk count · ±lines. Hides identical by default (mostly noise on big trees).

- `path_glob`: a glob (`src/**/*.c`; one without `/` matches file names anywhere: `*.h`), or several split by `;`
  (`*.c; *.cpp`), any of which matches; `!` before one leaves those paths out (`*.c; *.h; !third_party/**`), and
  `/…/` is a case-insensitive regex over the whole path. A rename matches on either of its paths. The HTML report's
  filter box takes the same, plus plain text to look for.
- `type`: a comma list of file types, `code · build · docs · binary`, from a built-in table: a known file name
  first (`CMakeLists.txt`, `requirements.txt`, `Makefile` are build), then the extension (`.c .cs .py .hlsl` code;
  `.vcxproj .sln .json .xml .ini` build; `.txt .md .log .csv` and any extension not known, docs), and binary when
  the bytes are (a modified file's reason) or the extension says so. A view only: it never changes a status, a
  reason or the delta contract. The summary counts the changed files by type.
- `hide_noise`: `all`, or a comma list of `stamps · numbers · comments · whitespace · eol · encoding`,
  leaves out the modified files that are only that. The last three are reasons. The first three are a tag on a
  `content` change (`noise` in the result, never a reason, so the delta contract and its digest don't see it): with
  that kind taken out of both sides, and blank lines and spacing aside, the two are the same. The tag names the fewest
  kinds that make them so: `stamps,comments` is a file hidden only when both are.
  - **stamps**: RCS / CVS / Subversion / Perforce keywords (`$Id: … $`, `$Revision: 7 $`, `$Date$`), expanded or not.
  - **numbers**: a word with a digit in it, and any joined to it by `. : / -` (a build number, a date, a time, a
    version, a hash), in a file that isn't code. Only the digits are the number: the letters of a word in it (but a
    hex ID's) still count, so `agent07` → `agent12` is numbers and `agent07` → `robot07` a real change. In code, a changed number is a real change.
  - **comments**: by the file's language, from its name or extension (C-like `//` `/* */`, `#`, `--`, `;`, `%`, `'`,
    `REM` / `::`, `<!-- -->`, PowerShell `<# #>`), with a marker inside a string not taken as one. Prose, JSON
    and kinds not known have none.

  It is worked out only for a pair read whole (each side up to 8 MB, the same limit as the reason's). A file renamed
  to another language isn't checked for comments. The summary has a `noise` line, and `list_files` shows a file as
  `[content, noise: comments]`.

  3-way, it is each side's 2-way tag: a path is noise when every side that touched it changed it only in noise (v1
  stamps only, or both only comments that merge), and `hide_noise` leaves it out when it leaves out each side's. A
  conflict is never noise (it needs deciding whatever it is), but a `stamps` conflict whose sides changed only
  stamps is. A side shows as `v1 content (noise: stamps)`.

`get_file_diff(path, context=3, max_lines=2000)` — one file, hard cap, `"capped: summary only, N hunks
total"` fallback for the pathological ones. Diffs inline when small, by reference (a `.patch` handle)
when large. A text pair with too many changes to align exactly in time (past the line diff's work cap: most of
their lines differ, or more than about 35,000 lines do however long the files) comes out as one replace over the differing middle: still a correct patch,
but its `+`/`-` counts are of that replace, not of the lines that changed. It is marked `coarse` wherever the counts
show: `get_file_diff`'s first line, `list_files`, `get_stats`, the HTML report, and a note on stderr from
`codediffer diff`. Lines only one of the two files has are no work: the diff sets them aside, as git's does, so a
file rewritten with mostly new lines still diffs line by line, and merges as narrowly as git's.

`get_stats` — totals by status/reason, byte mass, biggest/most-changed.

Change-porting tools: `export_changeset(A→B)` and `apply_changeset(onto=C)` dry-run → per-hunk
`applied | fuzzy | conflict`.

**Built so far** (`CodeDiffer.Mcp.exe`, views in `CodeDiffer.Core/Sessions`): `start_compare` (background,
returns an id, optional wait), `get_summary`, `list_files` (`status`/`path_glob`/`reason`/`type`/`hide_noise`/page, `lines=true`
renders ±lines for just that page), `get_file_diff` (cap `max_lines` and 256 KB, lines over 2,000 characters cut,
page with `start_line`; overflow written whole to `diffs\<path>.patch` in the result directory — the path
mirrored, so two files never share a name; one asked for with other context under `diffs-U<n>\`; a side gone or locked since the compare is said in one line), `get_stats`, `export_changeset` (to a file, `literal` for a byte-exact `git apply`).
`apply_changeset(target, write=false)`: diff3 with base = left, change side = right, target = C, on the same
merger the 3-way contract verifies. Per region applied | fuzzy (shifted line) | already | conflict (diff3 is
strict: a change touching a target edit conflicts, like git). A file with any conflict is never written;
others are replaced atomically. Binary / EOL- or encoding-only / >16 MB / non-round-trippable files apply
only when C equals the base byte for byte (compared and copied streamed, so a file of any size works). A
lone CR is content: merging across line endings converts only CRLF. A target in another line-ending style than the
base is merged ignoring line endings and written in its own style (a mixed base too); a target still in the base's
style takes a change that converts the file to one style, its own new lines included, as compare3 does.
With `--resolve-stamps` (MCP `resolve_stamps=true`) a file whose conflicting lines differ only in keyword stamps
is merged instead: the target's stamp is kept where it changed it, else the change's is applied (noted "only keyword
stamps collided").
Removes are done first, then renames, then adds and edits, so a directory that became a file (or the reverse)
ports in one run: a directory the removes and renames leave empty is removed before the adds (one still holding
anything, the target's own files included, stays, and so does any directory behind a link).
Stopping `apply --write` part way is safe: Ctrl+C stops between files (the report says how many were written
and how many were not reached), each file is written whole via a `<file>.<id>.codediffer.tmp` it creates and a rename (a file already there under such a name, a real one or a killed run's leftover, is never written over or deleted), and
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
Stopping a report (Ctrl+C) stops a giant file's block diff mid-read too, and a compare3 report's merges.
In the MCP server a cancelled request stops the same way: `write_report` (written whole, the rest listed
without a diff), `apply_changeset` (between files, as Ctrl+C does `apply`), `write_merge` and `get_file_diff`.

**Killed outright, resumed (built):** a process ended with no cancel (taskkill, a crash, a host killing the
server) runs no catch, so the hashes are also saved *while* contents are read: at least a minute apart, only when
new ones came in, and backing off to stay under ~5% of the run when a save is slow (written as a new snapshot and
a manifest swap, so a kill mid-save leaves the old ledger or the new one). Re-running the same compare is the
resume: it reads only what wasn't saved (the walk is done again; a giant file being read at the kill is read
again). Files are read biggest first, so the first save comes once the first giants finish. A clean shutdown
stops a running compare as a cancel does and records why: the MCP host stopping the server, the CLI's console
window closed, a logoff or shutdown (`stopped: …` in its run record and `compare.json`).

**Run record (built):** `run.json` in the result directory, in numbers only (no path, file name or error text,
so it can be shared as is): rewritten every 15 s while the compare runs and once at its end with how it ended
(done, failed + the exception's type, cancelled, `stopped: …`, `crashed` + type). A run killed outright leaves
its last heartbeat, so it says when it died (within 15 s), in which phase, how far it got and what it was using.
It holds process memory (working set and its peak, private, .NET heap and its peak, full GCs), CPU time, threads,
handles, the machine's cores and memory load, and per compare: files listed, same-size pairs and bytes checked,
bytes read (since v1.0.142 with what the classifier read for a verdict it had no record of), cache hits, differences found, wall time per phase, mid-run hash saves, and the content phase's worker
time split into opening files, waiting for bytes (the network, over a share) and comparing+hashing.
Its CPU time (`cpuSeconds`, since v1.0.125) is the process's cycle count over the processor's rated clock
(`cpuFrom: cycles`). Windows' own CPU time is sampled at the clock tick, and a compare's threads work in short
bursts between reads that mostly fall between ticks: for a cold local compare3 it showed 5 s where the cycles say
about 30 s. Off Windows, or without a rated clock, it is that sampled figure (`cpuFrom: sampled`), and so is every
CPU time recorded before v1.0.125: read those as far too low.
Since v1.0.132 it also counts what the run did underneath, to find where a run's CPU or memory goes without a
profiler: every exception thrown during it, caught or not (`exceptions`: by type and Windows error code, never
the message); which way each live stat of a file went (`liveStats`: answered by name, one query on a handle,
separate queries, a query the server refused and its status, and files opened plainly after a server refused an
overlapped open with error 87); and what each phase allocated (`phaseAllocatedMB`, the whole process's).
All of these are the process's counts since the run started: a run's own when it runs alone (the CLI, or an MCP
server running one compare at a time); compares an MCP server runs at once each count the others' too.
Since v1.0.134 a Windows error code is given only for a file error (`IOException 87`); every other exception
is its type alone, because an `ArgumentException`'s own code reads as Windows error 87 as well. A compare's
`starting` and `done` aren't counted either: in a compare3 the second compare is starting while the first runs,
and the first is done while the second runs and the merge after, so those phases showed the other's bytes (a
first compare's `done` of many gigabytes, and the second's held the merge and the result's save). The merge's own
time and bytes are a line of their own (`merge`), and since v1.0.135 so is the result's save (`save`, written
after the last compare's last phase). Since v1.0.135 a `Win32Exception` is counted with its own code (its HResult is
a generic failure), CodeDiffer's own stat and listing errors carry their Windows code (they were a bare
`IOException`), and `runs` says where the CPU time came from (`cycles` or `sampled`).
Since v1.0.136 the merge allocates about half what it did (death_1.3.1's 1,985 merged files: 1,418 MB → 734 MB, the
same merge): a line diff's exact pass keeps one bit per diagonal and step where it kept a copy of each step's
diagonals (D²/2 bits, not D² ints: ~560 KB, not ~36 MB, for a diff of 3,000 changed lines), valid UTF-8 isn't encoded
again to see it reads back, and pairing runs of identical lines makes no closure per line.
Since v1.0.137 the `merge` line has two under it: each step's thread time and bytes (`other` · `read` · `decode` ·
`split` · `diff` · `align` · `stamps`: each step's own, so a diff inside the stamps' merge is the diff's, and the times add
up to the workers' time, not the wall), and the files merged line by line (their lines, the MB read) with how their line
diffs were made (one side a prefix, exact, linear-space past the exact pass's budget, coarse past the work cap).
Since v1.0.139 a third: the files both sides modified that the merge didn't open. A file both modified in place is
merged from the three versions' hashes, which the compares know: agreed when v1 and v2 hold the same, else as the merge
store (`merges.bin` beside the ledgers) remembers those three versions merging, kept per merger version, classify size
and stamp resolving. A warm compare3 over unchanged trees then reads no file in its merge. The store is never trusted
with the cache off or a tree rehashed. And the lines a merge splits are cut straight from the UTF-8 bytes, the lines a
side shares with the base's head and tail reusing the base's strings, not decoded whole and split again.
`codediffer runs [--max N]` and the MCP tool `get_run_telemetry` print it, newest first.

**Result store (built):** every finished compare — MCP or CLI — is saved to a fresh
`<results>\yyyyMMdd-HHmmss-<id>\` (default `%LOCALAPPDATA%\CodeDiffer\results`, `CODEDIFFER_RESULTS_DIR`
overrides; refused if it would land inside a compared tree). `compare.json` (format/version, kind, state
running → done | failed | cancelled, roots, options, read cost, timings, counts) is written first as "running",
with the process and machine running it, so a crashed compare leaves an honest trace: once that process is gone
it is listed as `stopped` (and `results --prune` may delete it at once; one whose process is seen still running
is never pruned, however long it has run; a "running" one from another machine, or one whose process can't be
checked, is kept a day). A compare still running in another process (another MCP
server, a CLI run) is listed, but only that process can answer about it; it opens here once it is done.
A done compare with a directory that couldn't be listed or a file that couldn't be read (what the CLI exits 3 for)
is listed as `INCOMPLETE`, not `done`: its verdicts there are unknown (one saved before 2026-10-16 is so listed
only for its unlisted directories, since its unreadable files weren't totalled).
A malformed `compare.json` is reported as a corrupt result, never an error that ends the server; a path with an
unpaired UTF-16 surrogate (legal on NTFS) is saved losslessly, so it reopens as the same file; `changes.jsonl` (2-way, every path) or `v1.jsonl`/`v2.jsonl`/
`entries.jsonl` (3-way: each side's changes, its unchanged files a count in `compare.json`) hold the verdicts. Only verdicts are stored, not file contents: any compare_id (or
the directory) reopens without re-comparing (`list_compares`, CLI `results`), and a diff rendered later
from the live trees warns when a file's size no longer matches what was compared. Capped patches, apply
reports and the HTML report go into the same directory; one pruned by another process while this one still holds
the compare is saved again first, so nothing written there is left unlisted and never pruned. While one is written
the result is held (a lock file in `<results>\.locks\`, shared by any number of writers in any process): a prune
leaves a held result alone (`not deleted: … is in use`, exit 1, prune again later), and a write that meets a prune
already deleting its result waits for it, then saves the result again, so a write told it succeeded is in a listed
result. A prune from CodeDiffer 1.0.61 or earlier doesn't take the lock.

Renames cost little on a warm compare: the pure (byte-identical) pass hashes only adds and removes whose size
occurs on both sides, in parallel, and uses the hash ledgers (a second run reads nothing for it); the edited
pass reads the text candidates in parallel, skips every pair whose line counts alone rule out the 50% threshold,
and scores the rest in parallel — exactly the contract's similarity, the same pairs as scoring all of them. It
keeps each removed file's best few candidates, not every pair over the threshold (scanning a file again, for more,
if they all go elsewhere), so thousands of near-alike files (Unity `.meta`, generated stubs) pair as before in
little memory: 2,000 × 2,000 such files take ~5 s. Both
are counted in the bytes read. The edited pass is bounded, like git's rename limit: past 5,000 × 5,000 candidate
pairs (at the bound, minutes: ~160 s for 5,000 × 5,000 files of 2,000 lines; 1,000 × 1,000 takes ~6 s), or 1 GB of
their text, it is skipped and a note says so — those files are listed as added and removed
(identical renames are still found), rather than scored for minutes or held in memory.

`start_compare3(base, v1, v2)` (CLI `compare3`): base→v1 then base→v2, sequential, and the second reuses
the first's base listing and the base hashes it proved (no re-listing, no re-reading, not even a per-file
stat of the base; both compares judge the same snapshot of it; since v1.0.145 v2 is listed while base→v1
runs), then every touched path is
v1 only | v2 only | agreed | merged | conflict, the conflict kinds being content, stamps (a content conflict whose colliding lines
differ only in version-control keyword stamps, `$Id: … $`: with the stamps bare it merges cleanly; written with
markers, a stamp to pick; with `--resolve-stamps`, MCP `resolve_stamps=true`, it is merged instead, each stamp
taken from the side that changed it and v2's where both did, noted "only keyword stamps collided"), modify/delete, add/add, rename/rename, path collision, file/directory clash (one side has a file where the other has a
directory: v2 turns `D/` into a file `D` while v1 adds `D/z.txt`), binary, large (a side over the text limit, or
the base when both sides cut a large file down: no file that big is read whole to merge it), behind a link and
unopenable name (whether the path changed is unknown: never merged). Both sides cutting (or adding) copies of a run
of identical lines while one also changes the run — v1 drops an `#endif` of three, v2 turns one into `#else` — is a
content conflict, not v2's text alone: nothing in the lines tells whether they cut the same copy or one each (so
merging a merged file again with a side can now conflict there rather than say "already"). A file one side only moved
(byte-identical) and the other changed in place merges to the change at the new name — binary and large files
too, taken whole. A side with no line break at all has no line-ending style: it never "changes the line endings"
(a CRLF file cut to one line is not CRLF → LF). Renames are followed (renamed on one side, edited
on the other ⇒ merged at the new name), except one too weak or tied to trust (below), whose note says so. The same `get_summary` / `list_files` / `get_file_diff` / `get_stats`
serve it; `get_file_diff` shows the merged file with diff3 markers (`<<<<<<< v1 / ||||||| base / ======= /
>>>>>>> v2`). A clean merge equals porting base→v1 onto v2 (tested). Among identical lines (a run of one
repeated statement, a column of `}`), a line a side edited in place is a replace of the line it stands on — not,
as Myers and git write it, an insert there and a delete of a copy at the run's far end — so two sides editing the
same line of a run conflict, where `git merge-file` keeps both edits side by side as a clean merge. An insert or a
cut beside identical lines (a blank, a `}`, a `{`) can stand anywhere along them; one put hard against the other
side's change slides apart from it, so a function deleted on one side beside the next one's signature edited on the
other merges clean (as git, placing the cut otherwise, merges it), unless the other side changed one of the lines it
could move across: that change could be to a copy the cut takes, and stays a conflict. Two inserts at the same place
never slide apart: which goes first is not in the base, so that stays a conflict too, unless one goes back against
its own side's edit beside it (a rewritten test whose last part the differ split off to the other's place). A file's encoding (BOM, UTF-16) and
line-ending style (LF / CRLF / mixed) merge 3-way like its lines: a side that changed them wins (v2's LF→CRLF
is kept when v1 also edited the file), the same change on both sides agrees, and different changes on both
sides are a conflict of kind `encoding` or `line endings` (both files listed, nothing written); when the lines
conflict too, the file is a `content` conflict written with markers in v1's encoding and line endings, and its
note says the encoding or line-ending conflict as well. A mixed file keeps each line's own ending; a lone CR
stays part of its line. A file that isn't UTF-8 in one of the three (a cp1252 / Latin-1 byte, as in a test of
legacy names) but has no BOM and no NUL is text, as git reads it: all three are merged line by line on their bytes,
so each line is written back exactly as it came, a UTF-8 side's included (`apply` ports such a file the same way).
But where a side only re-saved the 8-bit base as UTF-8 (the same text, read as Latin-1 or as cp1252 — whichever
gives that side's text; they differ only in 0x80–0x9F, cp1252's “ ” – €), the three are merged as text in that code
page: that side changed no line, and the merge is written in UTF-8, the other side's edits too — not one side's lines
in UTF-8 and the other's in the old code page, nor a conflict on every line with an accent the other side edited.

**Merge overlay (built):** `write_merge(id[, out_dir])` (CLI `compare3 … --merge-out DIR`) writes the merge
as an overlay on v1, never the whole tree: `files\` holds only what the merge changes in v1 (v2's one-sided
changes, clean merges in the merged encoding and line endings, text conflicts with diff3 markers),
`deletes.txt` the v1 paths to delete (v2's deletes, the old side of a move; an unpaired surrogate in a name,
which UTF-8 can't hold, written `\uD800`–`\uDFFF` and read back by `apply-overlay`), `conflicts.txt` the conflicts
with markers plus those that can't be one file (binary, large, modify/delete, rename/rename, path collision, file/directory clash, encoding, line endings)
with each side's file, and `OVERLAY.txt` how to apply it: delete the paths in `deletes.txt` from v1, remove the
directories that left empty, then copy `files\` over v1 (deletes first, so a file v2 turned into a directory, a
directory v2 turned into a file, or a case-only rename lands right).
A move's old path is listed only once its new file is written, so a failed read never loses v1's copy. Each
file is written beside its name and renamed into place (a failed write leaves nothing half-written), and a
merge is checked against the compare as it is written: a file whose merge changed since (a tree edited in
between) is listed as not written instead. `INCOMPLETE.txt` is present until the overlay is finished.
Ctrl+C (in `compare3 --merge-out`; a cancelled `write_merge` request in the MCP server) stops the writing,
a giant file's merge included: the overlay keeps its `INCOMPLETE.txt` and is never applied (exit 130). Write
it again into a new or empty directory.
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
overwrite conflicts resolved since. On death 1.0.9 the dry run onto v1 takes 1.5 s (1,354 files, 33.3 MB, 0 deletes).

**Conflicts settled in bulk (built):** the overlay also holds `conflicts.json`, the conflicts for a program: each
one's base path, kind, note, its markers file in `files\` and the delete made for it (a rename), where v1 and v2
have the file, and v2's file's size and time. `codediffer conflicts <overlay-dir> [<pattern>...] [--kind K]
[--depth N] [--list]` (MCP `list_conflicts`) counts the undecided and decided ones and groups the undecided by kind,
folder and file type. `codediffer resolve <overlay-dir> v1|v2|regenerate|undo <pattern>... [--kind K] [--all]
[--write]` (MCP `resolve_conflicts`) settles every conflict a rule picks, in the overlay itself, so `apply-overlay`
applies it unchanged: `v1` and `regenerate` move a markers file to `set-aside\` and drop a rename's delete, so v1's
file stays; `v2` does the same, then copies v2's file into `files\` at v2's path (refused if its size or time changed
since the overlay was written) and lists v1's copy at another path, or one v2 deleted, in `deletes.txt`; `undo` takes
choices back, exactly. `decisions.json` records each choice, the rule and what it changed. Dry run unless
`--write`. A pattern is `/`-separated with `*`, `?` and `**`; without a `/` it is a name at any depth, with one it
is anchored at the top and takes what is under it; a conflict matches by its base path or either side's. A
conflict already decided is left alone until undone, one the overlay failed to write is refused (write it again),
and a path collision or file/directory clash can't be taken from v2 (another change lands on its path). An
overlay that is applied or being applied is refused. While a resolve writes, the overlay holds `RESOLVING.json`
(what it is doing): `apply-overlay` refuses it, and the next `resolve --write` finishes it first. Exit 1 when a
conflict the rule picked was refused. An overlay from before v1.0.150 has no `conflicts.json`: write it again.

Full-scale check (2026-10-05, CodeSpawner `death` 1.0.9 base / v1 / v2 on a share, 68,661 base
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

Local baseline on the current corpus (2026-10-16 to 10-19, CodeSpawner `death_1.3.1`: 74,775 files, 91.4 GB a
tree, base / v1 / v2 on a local SSD): `verify` v1 cold 10:11, 1,989/1,989; v2 6:52, 1,986/1,986; the conflict
verify 0:15, an exact 16,708 conflict / 47,724 clean split, compare3 conflicting in exactly 1,935 files. `apply`
dry run: 54 files apply, 1,935 conflict; 31,382 hunks applied, 0 fuzzy, 16,708 conflict. Cold compare3 of the
`d0` subtree (3 × 58 GB, more than the 32 GB of RAM) with v1.0.110: 116–196 s wall (66–68 s CPU by the tick-sampled count, far below the real cost; see run.json above).

Over the share (2026-10-19, v1.0.110, `death_1.3.1` vs `_v1` on a share, 8 threads, empty CodeDiffer
and CodeCompass caches): cold `compare` **34:43**, 182.8 GB read at ~95–101 MB/s (the link), walk 7 s, 0 pauses,
0 unreadable; 1,989 modified, every one tagged comment-only noise (v1's edits append `/*v1:…*/` to lines). Then
`verify` of the v1 delta on the same trees with that cache: 2:07, digest reproduced, hunks 1,989/1,989 exact,
shas 0 wrong, the compare's file operations 1,989/1,989.
The same day, cold `compare3` of base / `_v1` / `_v2` over the share (empty caches, after commit 6195a05):
**57:54** — base→v1 2,282 s (182.8 GB), base→v2 1,179 s (91.4 GB: v2 alone, 72,785 base files reused),
classify + merge 14 s; 1,990 touched, 1,935 conflicts, 16,708 conflict regions, 50 merged, 0 pauses,
0 unreadable. The conflict verify after it took 3:02 (digest reproduced, the exact 16,708 / 47,724 split,
compare3 conflicting in exactly the manifest's 1,935 files). The `apply` dry run took 1:17 (54 apply, 1,935 conflict;
31,382 hunks applied, 0 fuzzy, 16,708 conflict). Every number matches the local baseline.

CodeCompass v2 ledger at full scale (2026-10-06, CodeCompass 1.0.243): `codecompass index` of base and v1 on
the share (66,337 of 68,661 files each; it skips the rest by size), then `compare` base→v1 with an empty
CodeDiffer cache: 129,552 sides from CodeCompass's ledgers, 17.3 GB read (exactly the files CodeCompass
skipped, plus both sides of the 1,561 modified files), 4:19. The 68,661 per-file verdicts are identical to
a run on CodeDiffer's own ledger. Locally, a same-size edit with the mtime put back, a delete + recreate
with the same size and mtime, and a move over an existing file were all re-read and reported modified.

Why it matters, and the three everyday cases it speeds up (two releases compared again, current code against
a release, two trees both in progress): see "Compare again in minutes, not hours" in the README.

The cache trusts an entry only if size, mtime, ChangeTime and file id (where both sides know it) all match the
listing, and it records one only from a read whose handle stat didn't change across it. Some servers give a
file one id in a directory listing and another on an open handle (one share seen in use did, for a third of its
files, which held nearly all its bytes): such a file, its size, mtime and ChangeTime agreeing, is cached with no id
and told by those three alone, and the summary counts it ("cached by size, modified and change time only").
Before v1.0.89 such files were never cached, so every compare read them again.

When one side of a same-size pair is cached and the other isn't (compare3's base→v2, where base comes from
base→v1), the uncached file is read beside the cached one on local trees: while their bytes match only XxHash128
runs, and a file equal to the end, the cached file's live XxHash128 still its recorded one, takes the cached SHA-256,
proven by byte compare; a file that differs gets its own SHA-256 from its start. The cached side is read again, so
the read cost counts it (base→v2 shows about base→v1's bytes, not half). An unchanged file then skips its own
SHA-256 pass, nearly all of a cold compare's CPU (on death's d0, 3 × 58 GB cold, the tick-sampled CPU went from
116 s to 67 s, a count that misses most of the real cost), wall no slower. On a share each file hashes alone (a second read there costs link
time, and the hashing keeps up with the link), and never with `--rehash-side`, which reads only the trees named.

`--rehash` re-reads every tree and refreshes its cache; `--rehash-side` does that for the trees named and uses
the others' cache: `compare --rehash-side left|right`, `compare3 --rehash-side base|v1|v2` (several with commas,
e.g. `v1,v2`). It checks one tree's cache against its bytes, or times one side cold, without reading the rest
again. The summary's cache field names it (`cache on, rehash v1`). A file stamped within
the last moments before its read (3 s; 1 h on a share, whose clock may differ) is held pending and trusted only
on a later stable read. By default a hit is confirmed by a live stat of the file too. `--fast-stat` skips that and
trusts the listing alone: a same-size edit made through another hard link to the file, which this name's listing
may not show, can then read as identical; use it only on a tree nothing else writes. So its verdicts are listed
but never patched or ported: `--patch` with `--fast-stat` is refused before the compare, and `export_changeset`
or `apply_changeset` on such a saved compare says to compare again without it. On death (warm, 4 threads) it
takes 16–19 s against 100–110 s, the per-file stat being nearly all of a warm compare's time over a share.

`codediffer statcheck <dir>` measures how far a share's listing lags its open handles. Opening a file to measure
it refreshes the share's stale entry for it, so a second statcheck straight after reads fewer mismatches: the
first run's numbers are the ones that describe the share. When the listing's FileId and the handle's disagree, a
last section breaks those files down against the ones that agree: whether each id holds still (the tree listed
again, the file opened again), how the two relate bit for bit, and the files' sizes, attributes, link counts and
folders. Such a file is cached with no id (above).

## 5. The human HTML report

Self-contained shell + a sidecar data dir in the result store. Summary tiles; a tree view with folder
roll-ups; **identical hidden by default**; status/reason filter toggles; file-type chips (code · build · docs ·
binary, each with its count: a click hides or shows that type); a path filter box (text, `*.c; *.h`, `!test/**`,
`/regex/`, as `path_glob`); noise chips (stamps · numbers · comments · whitespace · eol · encoding, as
`hide_noise`: a file is hidden when all of its noise is), and two presets, **Code changes** (code files, noise hidden)
and **Everything**. A noise file's row says so: `content (noise: stamps)`. Each file's
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
  as itself, left out of rename matching), every summary warns, and the CLI exits 3. Since v1.0.127 the why
  starts with its cause, and a pair's with its side, `[right, in use] …` (causes: denied, in use, missing, path too
  long, offline (a cloud file's errors only, since v1.0.135), network, disk error, else the Windows error code, or
  the error's type when it has none); an add or remove is its one side's file. The warning ends with them counted, no names:
  `By side and cause: v1 denied 2 · base in use 1` (compare3 names the trees base / v1 / v2). A pair's side is
  the one that fails to open again; `a side` when both open by then. Files are opened so a
  writer can keep writing (a log being appended to is still read), and the hashes read are kept even when a
  compare fails. A file the server won't open for overlapped reads (Windows error 87, seen on a share for a few
  files a plain open reads fine) is opened plainly instead and read the same (since v1.0.132).
- Trees that stop answering are not 28,000 unreadable files. After any failed read the compare checks that each
  tree's root is still there and that a file it read earlier still opens. If they do, the file alone is at fault:
  it is tried again (after 1 s, then 4 s; a vanished file at once is final) and then listed `[unreadable]` as above.
  If they don't, the whole compare pauses: no read starts, stderr says `PAUSED: the trees stopped answering (<the
  error> [Windows error N])` at once, the progress line shows the pause and its cause, and the check is repeated
  (1 s growing to every 30 s). When it passes, stderr says `resumed`, every paused read starts over and nothing
  is marked unreadable for the pause; the summary notes how many pauses there were and how long. If the trees
  don't answer within `--share-wait` minutes (default 30; the MCP server waits 30), the compare stops with exit 3
  and says so. What it read is in the hash cache (saved every minute as it reads, and on the way out), so the same
  compare run again reads only the rest. Every pair still unreadable when the rest is done is tried once more
  before the result is written. Every failed read is counted by cause (its Windows error code, or the exception's
  type when there is none): the failed tries, how many then worked, how many were given up on (listed
  unreadable), and the pauses it began; and each pause is logged with when it began, how long it lasted and
  whether the trees came back. That is codes and counts only, no paths, so it can be shared as it is: the summary's
  note `failed reads by cause: Windows error 64: 120 failed tries, 118 then worked, 2 given up, 1 pause(s)`, the
  saved result (`readFailures`, `pauses`), and `runs`, which shows it while the compare is still running, with
  the unreadable count so far.
- Files are what is compared: a directory added or removed with no files in it doesn't show. A directory named
  `.git` (anywhere below the root, in any case) is never walked, on any side and by every command (compare, compare3, apply,
  statcheck): a repository's object store isn't the tree's content, and its packs would swamp the answer. A `.git`
  *file* (a worktree's or submodule's pointer) is an ordinary file and is compared.
- Symlinks and junctions are not followed (one can point outside the tree, or back into it); a link a server
  follows itself (Samba's default) arrives as a plain directory, and one that lists exactly what a directory above
  it lists (the same names and file ids: `x -> ..`) is that directory again, skipped as a link instead of listed
  over and over until the server gives up. Each skipped one is counted and every summary says how many (compare3's too, per side). A file one tree has where the other
  has a link is not "added" or "removed" but unknown — `[unreadable]`, a `behind a link` conflict in compare3,
  never ported or written as a delete. Other reparse points — OneDrive / cloud placeholders, deduplicated
  files — are ordinary files and directories and are compared. Paths past 260 characters are listed and
  checked like any other; a name ending in '.' or ' ' (which Windows' path API opens as another file), or a
  device name like `nul` (which it opens as the device), is not read but listed as unknown — `[unreadable]` (a
  directory so named is one such row: nothing in it was listed), exit 3, a `#` line in a patch, never ported, an
  `unopenable name` conflict in compare3 — and a timestamp past year 9999 counts as unknown (the file just isn't cached).
- An incomplete compare (a directory that couldn't be listed) is never acted on: `apply`, `apply_changeset`,
  the merge overlay and a whole patch (`--patch`, `export_changeset`) refuse it. The warning names the directories
  (a few; `compare.json` keeps them all, `kind: "unlisted"` among the skipped paths), and what the other tree has
  under one is not "removed" or "added" but unknown — `[unreadable]`, an `unlisted directory` conflict in compare3.
- Names that differ only in case are one file on Windows: a remove and an add like that (`Foo.c` → an unrelated
  `foo.c`) are left to do by hand by `apply`, and in compare3 a v2 file landing on a name a conflict keeps in v1
  is a `path collision`, never written over v1's file.
- A hash ledger that can't be saved after the compare (disk full, a file held open) is a note, never a lost
  result: the next compare reads those files again. A ledger that is corrupt (an offset or length out of range)
  is no cache, never a failed compare.
- An HTML report written over an old one keeps the old one's marker until the rest is gone (read-only files
  too; a link inside removed, never followed), so a replace that fails part way leaves a directory that is still
  replaced next time, never one refused as not a report.
- A command given an option it doesn't take (`--no-chache`), an extra word, or a flag without its value stops
  at once with the usage (exit 64), instead of running an hour without it; so does a bad `-U` / `--max-diffs`
  value, an option without the one it goes with (`-U` / `--literal` without `--patch`; `--out`, `--large`,
  `--include-identical`, `--max-diffs` without `--html`; `--html` with `--no-save`), a report `--out` inside a
  compared tree, that is a file, or that is a directory other than one of our reports, and `verify` given only one
  of `--base` / `--variant`. A results directory inside a compared tree, a `compare3 --merge-out` in, or
  holding, the report's `--out`, and a report `--out` or `--merge-out` that holds the results directory (where the
  compare is saved first) or can't be made (a file, under a file, on a drive that isn't there), are refused before
  the compare too (exit 2). `--html` whose result could not be saved
  says there is no report and exits 2, `compare` and `compare3` alike. `--patch` renders with `--threads` threads.
  "Inside" is by what the paths resolve to, not only their spelling: a tree named through a junction, symlink,
  subst or mapped drive is the same tree, for the results directory, the overlay, the report and `apply-overlay`.
- `list_files(reason="unreadable")` lists the files the listings mark `[unreadable]`, of any status; another
  reason lists the modified and edited renamed files with it. `get_file_diff` given a path as a listing shows it —
  an unpaired surrogate as U+FFFD — finds that file, and a `path_glob` matches that form too. When several files
  show alike, the answer lists their escaped names (`c\uD801.txt`), and either one asked for so is found; the
  surrogate itself can't be passed (JSON's reader refuses it, before CodeDiffer sees the call). The HTML report
  shows such a name by its escaped form, so two of them are never two rows alike. A diff too big to
  show inline is saved under the path mirrored, or, where the tree has a file `a` and a directory `a.patch/`, or
  files a case-sensitive tree tells apart by case (`Foo.c`, `foo.c`), under `diffs/_clash/` by the path's hash; it is
  written beside its name and moved into place, so pages asked for while it is open never collide. A name that is
  several files but for case, none of them exactly, is answered with their names; one asked for with more context than the default never changes the hunk count
  a listing shows. `export_changeset`'s `out_path` must be a file path outside both trees: a device (`NUL`, `CON`),
  a root, a directory or a path with a NUL is an `error:` answer. A reopened compare's export, like its
  `write_merge` and report, says it was rendered from the trees as they are now.
- On a 3-way compare, `list_files`'s `reason` and `lines` and `get_file_diff`'s `context` are 2-way only: given,
  they are an error, never silently ignored. `write_merge` with no `out_dir` writes `merge\` in the result
  directory, then `merge-2\`, `merge-3\`, … once that holds an overlay (or a file has the name), never refusing its own
  default: one another writer claims at the same moment moves it on to the next. Any other failure (a full disk, a
  directory that can't be listed) is the `error:` it is, never a reason to try the next name.
- A compare id is looked up as an id first: a folder in the working directory named like one is not that compare
  (a result directory given by its path still opens).
- `results --prune` never deletes through a link: a junction or symlink in the results directory is not a saved
  compare, whatever it points at. A saved path that could reach outside its tree (`../x`, rooted, a drive) is a
  corrupt result, never acted on.
- `whitespace` means only whitespace: the same lines but for indentation, trailing spaces / tabs and the
  spacing round punctuation (`x=1` → `x = 1`). A space that joins or splits a word (`return x` → `returnx`) or
  two operator characters (`- -x` → `--x`, `/ *` → `/*`, `> =` → `>=`), a word and a quote (`L "x"` → `L"x"`), a
  digit and a dot (`1 .5` → `1.5`), a number's exponent and its sign (`1e + 5` → `1e+5`, `0xe + 1` → `0xe+1`), a space taken out of a quoted string (`split(" ")` → `split("")`,
  `"Total: "` → `"Total:"`), a space after a `#define`'s name (`F(x)` → `F (x)`: a function-like macro made one
  that expands to `(x)`) or inside an `#include <…>` (`<a.h >`: another file name), a line joined, split or added,
  a space taken out of a line continuation where a backslash escapes (C, Python and the like: a `\` ending a line
  joins it to the next, so `foo \⏎  bar` → `foo\⏎bar` is `foo bar` → `foobar`, a string goes on past one, and a
  `\␠` ending a line, no continuation, made one by trimming the space),
  and NBSP / NEL (characters, not whitespace) are `content`. Whitespace is ASCII space, tab, VT and FF, whole or streamed alike. Strings and comments aren't
  parsed: a quote not escaped opens a string until the same quote or the line's end, and a run of spaces widened
  inside a string literal, or a space there turned into a tab (`' '` → `'\t'`, `"a b"` → `"a  b"`), reads as
  whitespace: inside a string as outside it, a run's width and kind aren't kept, only whether it is there. The
  verdict is the same for every kind of file: where the language reads indentation or spacing (Python's
  indentation, a Makefile recipe's tab turned into spaces, a YAML block's indent, a shell's `FOO=bar` →
  `FOO = bar`), a change it reads as code is still `whitespace` here — the diff shows it. Where a line reads more than one way, a space is kept if any reading
  puts it in a string, so a guess wrong for the file only makes a verdict stricter: an apostrophe is read as a quote
  and as not one (`can't` in a comment, a Rust `'static`), and a string after a raw prefix (`r"…"`, `R"(…)"`,
  `r#"…"#`, a backtick string, C#'s `"""…"""`) with its backslashes as text and as escapes. Where a backslash
  escapes, and in C#, one reading more skips `//` and `/* … */` comments (a `3"` in one opens no string), closes a
  C++ raw string only at its `)delim"` and a `"""` or `'''` string only at the same three quotes, so a quote inside
  either is text: `R"(msg="Hello , world")"` → `Hello, world` and `/* 3" wide */ printf("%d , %d")` → `%d, %d` are
  `content`. So
  `/* it's */ puts("don't a= b");` → `a=b` is `content`, and a reformat beside a raw string ending in a backslash
  is `content` too. A string is read a line at a time: one that goes on past its line (a Python `"""` docstring, a C#
  raw or multi-line verbatim string, a JS template literal) is a string on its first line only, so a space taken
  out of it on a later line reads as whitespace — the review's diff shows it. Which quote is escaped is the file's language's, by its extension:
  - C, C++, Java, JS/TS, JSON, Python, Go, Rust, HLSL/GLSL/USF and the like: a backslash escapes (one after an
    odd run of them: in `"\\"` the second quote closes the string), so `if (c == '\'' || c == '"') {` →
    `if (c=='\''||c=='"') {` is `whitespace`;
  - C# (`.cs`, `.csx`): the same, but in a verbatim string (`@"…"`, `$@"…"`, `@$"…"`) a backslash is text and `""`
    is a quote: in `@"C:\temp\"` the quote after the backslash closes the string, so `Log("a, b")` → `Log("a,b")`
    beside it is `content`;
  - PowerShell (`.ps1`, `.psm1`, `.psd1`): a backtick escapes, in a double-quoted string; batch (`.bat`, `.cmd`):
    nothing does;
  - any other file, `.m` among them (Objective-C escapes, MATLAB doesn't): `whitespace` only if it is so with a
    backslash read as an escape and as text, both;
  - a rename between two of these (`a.c` → `a.txt`): both, as for any other file, so `compare A B` and
    `compare B A` agree.

  A backslash before a space escapes it (taken out, the backslash escapes what follows): that space is kept, but
  never trailing whitespace. A whitespace-only change is diffed line for line (each changed line a replace of
  itself), never a line paired with an identical one elsewhere.
- A reason is the text's: line endings changed along with the encoding (UTF-8 CRLF → UTF-16 LF) are `eol`, and
  the patch note says the encoding changed too; a shown diff of a whitespace or content change says so as well,
  and so does an edited rename whose only edit is its encoding (a BOM added, cp1252 → UTF-8), which would
  otherwise show as a bare rename header. Line endings in a note are counted in the file's own encoding (UTF-16's
  CRLF is not "LF"). A note is of the whole file up to 16 MB (an LF file with one CRLF line at its end is
  "mixed"), so it never contradicts the reason; past 16 MB it says it is of the file's first 16 MB.
- Among identical files the rename is the best match, not the first listed: `a/LICENSE` and `b/LICENSE`
  identical, `b/LICENSE` renamed to `b/COPYING` and `a/` deleted gives `b/LICENSE → b/COPYING`. Empty files
  never pair as renames, and nor does a file that is only a BOM (an editor's "empty" `.cs`): that is empty text.
  Equally similar edited renames are paired the same way: the same name and directory first, then the same name,
  then the same directory, so `a/g.c`, `b/f.c` → `c/f.c`, `c/g.c` never cross-pair.
- A rename is always listed, but compare3 and apply follow only one they can trust; any other is marked "not
  followed by compare3 or apply: …" (in the CLI listing, `list_files` and the saved result) and is merged and
  ported as the remove and the add it may be, so an edit the other side made to the old file is a delete/modify
  conflict, not written into an unrelated new one. Not followed:
  - a tie: another file is as good a match, at the same level of name and directory (both pairings of two
    identical files moved to two new names are held);
  - a file with fewer than 3 significant lines (a one-line header, a stub) that changed its name;
  - an edited rename whose significant lines are under 80% alike, unless they are at least the threshold
    alike and the file kept its name or its directory.
  Significant lines leave out the leading comment block (a licence header, a shebang; `#include` is code) and
  lines without a letter or digit (braces), so two files alike only in shared boilerplate are not followed.
  A same-name move is always followed, however small the file. `verify` and the delta contract are unchanged:
  they check the listing.
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
  only gained a UTF-8 BOM is `encoding`, small or large. Re-saved as UTF-8, it is `encoding` when it reads the same
  as Latin-1 — unless it has a byte in 0x80–0x9F: those are cp1252 punctuation (`“ ” €`), and a UTF-8 side holding
  U+0093 for one is a byte-for-byte "conversion", `content`. (A correct cp1252 → UTF-8 re-save is `content` too: it
  is not decoded as cp1252.)
- A file is binary when it has a NUL and no UTF-16/32 BOM (UTF-8 never needs a NUL, so a UTF-8 BOM excuses
  none): anywhere in it up to 16 MB (the size a patch line-diffs, reading it whole; past 8 MB the rest is scanned
  for a NUL after its head), in its first 8 KB past that. So text that turns binary further in is `binary`, the
  compare and its patch always agree, and no patch ever carries raw NULs.
- `codediffer diff` with a file that isn't there is an error (exit 2), never "the whole other file added": an
  added or deleted file is asked for with `/dev/null` (or `NUL`) as the absent side. Both sides get the right
  file's name, so `git apply` changes that file rather than renaming it.
- `verify` fails a hunk whose op doesn't fit its line counts (an `insert` with old lines, a `delete` with new
  ones), as the contract defines them, even when its coordinates rebuild the file. A hunk takes its text from
  the variant, so rebuilding is not enough: the hunks must be the canonical ones — coalesced (an unchanged line
  between any two), each where the walk is on both sides (`newStart` = the new lines before it + 1, a delete's
  too), and no replace wider than its change (its first and last lines differ on the two sides, one that replaces
  lines line for line leaves none of them as it was, and one of a different number shares no line between its old
  and new lines: an insert or delete dressed as a replace, or two hunks merged over the lines between). `whitespace`
  records are checked too (each hunk must change only whitespace), run-rule hunks are expanded as they are checked (a rule
  claiming lines to 2³¹ fails at the file's end, never held) and the files streamed, and a renamed file's reason is compared with CodeDiffer's own verdict on the pair. A `metadata`
  record (content identical) expects the compare to report its file unchanged: CodeDiffer doesn't diff
  metadata. A `binary`, `eol`, `encoding` or `metadata` record carrying hunks fails: the contract gives those
  reasons none. A path twice in the delta (two `modified` records, or removed and renamed from) is refused (exit 2).
- `get_file_diff` / `export_changeset` / `-U` context past the file's length is the whole file; a negative one is
  refused (export) or none, never a corrupt hunk header. A patch written with no context (`-U 0`, `context=0`)
  says in a `#` line at its top, and in its summary, that it needs `git apply --unidiff-zero` — which has no context
  to check a hunk's place by, so it can apply one at the wrong place without a word. For an exact apply use context 1 or more, or `codediffer apply`.
- A whole patch (`--patch`, `export_changeset`) is for `git apply`: what it can't carry — binary and large
  files, text that isn't UTF-8, unreadable files, the eol/encoding-only notes — is described in `#` lines
  outside any `diff --git` section, which git skips, so the rest still applies; the summary counts them as
  NOT CARRIED. So is a file behind a link in the other tree (a patch would write or delete through it), and a
  path another change's path differs from only in case (`Foo.c` → `foo.c`, or a directory renamed so): on
  Windows `git apply` refuses the whole patch over one of those, and so is a path with an unpaired surrogate
  (a legal Windows name UTF-8 can't spell: git would look for another file). A byte-identical rename is a header-only rename, binary or not; a UTF-8 BOM is kept. A rename
  found by line similarity always carries its diff, even at "100% similar" (one line in 2,001, reordered lines,
  line endings only): the list says "(but edited)", and it never counts as pure.
- A file over 8 MB is not decoded whole to find its reason: up to 64 MB it is compared streamed, as bytes
  (BOM, whitespace and line endings normalized byte by byte by the same rule, stopping at the first real difference), so
  a large LF→CRLF change is still `eol`; past 64 MB, or in UTF-16/32, a difference is `content` unchecked.
- `verify` checks CodeDiffer, not only the manifest: with the trees it runs CodeDiffer's own compare and
  requires exactly the manifest's files, reasons and rename similarities (death_1.3.1: 1,989/1,989 and
  1,986/1,986); the 3-way gate needs the exact decomposition, or reconstruction AND a merge conflicting in
  exactly the manifest's files AND every region being what it says against the trees (a conflict both sides
  changed, differently, that some minimal alignment of each side leaves without a base line both sides keep — diff3
  ends a region at one, so a conflict widened over unchanged lines or two regions joined fails, while among repeated
  lines any alignment as good as the best counts; a clean merge its side changed, whose old and new lines share none
  (one side's change is one hunk of a minimal diff), neither overlapping nor abutting another change, the other
  side's or its own side's; an edit both sides made alike only where they really did), AND compare3 itself
  — over the whole trees, lines with their endings — conflicting in exactly the manifest's files and merging
  none it leaves out (death_1.3.1: 1,935 of 1,935). Every modified record's `oldSha`/`newSha`/`oldSize`/`newSize`
  is checked against the trees' files, whatever its reason: the digest only proves the manifest agrees with itself.
- `verify` reads a sharded delta (CodeSpawner 1.1.x `--shard-size`) from its index, `<corpus>-delta.index.json`;
  a page alone is refused. Each page in the index's catalog must be beside it with the catalog's sha256, index,
  count and first/last path, every page but the last full, the records in ascending path order across the pages,
  and no `<corpus>-delta.shard-*.json` the catalog leaves out (the stem taken from the catalog's page names too,
  so an index renamed to anything else is still checked): a missing, extra, altered or misplaced page is an
  error (exit 2), never a partial verify. The pages in order are the delta's `modified`, so the digest and the
  tree checks run exactly as on the monolithic delta.
- `verify` reads `_meta.deltaKind` `diff` (or none: the first deltas had none) and `conflict-3way`; any other kind
  (CodeSpawner's planned `symbol`, a typo, a number) is refused (exit 2), never verified as a diff. A conflict
  manifest without both its `conflicts` and `mergedClean` arrays, or with a clean merge whose `side` is not `v1`
  or `v2` (a `v3` would have been checked against v1), is refused too.
- Deterministic output: two runs over the same inputs are byte-identical, so a diff of results is real.

## 7. Defaults (tunable)

| Setting | Default | Why |
|---|---|---|
| Hide identical | on (all outputs) | identical is noise on big trees |
| Per-file inline diff cap | 2,000 lines / 256 KB, lines over 2,000 chars cut | above → window + hunk map + `.patch` by reference |
| Large-file threshold | 16 MB | above → never line-diffed whole: a block-level diff by reference (`get_file_diff`, `report --large`), a `large` conflict in compare3, byte-for-byte only in `apply` |
| Result store location | fresh timestamped dir **outside** both trees | never overwrite; never diff own output |
| Hash cache | on, keyed by path, trusted on size + mtime + ChangeTime + file id, live-checked | second compare of a near-identical tree is seconds |
| SMB walk | concurrent, bounded; metadata off enumeration | overlaps round-trips on a latency-bound share |
| Metadata-only diffing (mode) | **off** (opt-in) | over SMB mode is mostly noise |

## 8. CLI exit codes

| Code | Meaning |
|---|---|
| 0 | done: nothing conflicts and nothing is missing |
| 1 | done, with conflicts (`compare3`, `apply`), a file that failed (`apply-overlay`), a conflict refused (`resolve`), a `verify` gate or a `selftest` check failed, or a prune that could not delete everything |
| 2 | error: a tree or file not found, a refused directory, an unreadable manifest, a failed `--html` / `--merge-out`, a file or directory that could not be read or written where the command can't go on without it (denied, a share lost) |
| 3 | done but INCOMPLETE: a directory could not be listed or a file could not be read (the warnings say which), or a report written with a diff it could not read or render (listed with why); or a compare stopped because the trees stopped answering for longer than `--share-wait` (what it read is cached: run it again) |
| 64 | usage: a missing argument, an unknown option, a flag without its value |
| 130 | stopped by Ctrl+C (a compare, an apply, or a report or merge overlay written part way) |

## 9. Measured costs (for sizing a run)

Measured on death 1.0.9 (68,661 files, ~94 GB a tree), the share on a 1 Gbit link that tops out ~95–110 MB/s:

| Operation | Cost |
|---|---|
| Cold `compare` / `compare3` / `apply` over the share (no or partial hash cache) | reads at link speed for 10–60 min (189 GB in 33 min); one SHA-256 pass per pair at link speed, an estimated fifth of a core (the ~1–2 % once listed here was the tick-sampled count), ~160 MB RAM |
| Compare reading CodeCompass ledgers (only uncached big + changed files read, e.g. 17 GB) | several minutes at link speed |
| Cold compare of large local trees | reads the trees end to end, at disk speed; hashing keeps up |
| CPU of a cold local compare (counted by cycles, files in the OS cache, a 6-core desktop CPU) | ~5–6 CPU-s per GB of tree: one SHA-256 pass per identical pair, ~0.25 GB/s a core without SHA instructions; compare3's v2 adds ~1 CPU-s per GB (read beside base: XxHash128 only). death_1.3.1 slice, 5.5 GB × 3: `compare` 6 s wall / ~25–34 CPU-s, `compare3` 10.5 s / ~29–39 CPU-s |
| compare3's merge (the files both sides changed, read and merged line by line) | about 7 times their three versions' bytes allocated (death_1.3.1: 1,985 files, 102 MB read, 734 MB allocated, 3–4 s on one core); only the files' own text and lines, not held after |
| Warm `compare` / `compare3` (cache hits) | ~0 bytes read, a stat per file: 1–4 min, a few % CPU |
| `verify` against the delta / conflict manifests | a warm compare of the two trees plus the changed files: 30 s–2 min |
| `apply` dry run or `--write` after a warm compare | reads the changed files of three trees: ~1–2 min |
| HTML report (`--html`, `report <id>`, `write_report`) | seconds, a ~20 MB write |
| `report --large` / `blockdiff` | trivial, unless many giant files mean tens of GB to read |
| `selftest` (5 compares, 3 dry-run applies, the MCP server's run of each tool, 3 reports) | warm, about 2 warm compares' worth of each per tree: death_1.3.1 base / v1 / v2, 3:00 on one core (1:40 on 8); a `--cold` first compare adds one full read |

Memory stays small throughout: compares stream and hold only the verdicts (peak working set about 160–380 MB).

Threads (since v1.0.127): `compare`, `compare3`, `apply` and `apply-overlay`, and the MCP server's `start_compare` /
`start_compare3`, run 64 threads (32 before v1.0.141) when any tree is on a share (a UNC path, a mapped or network drive) and
min(cores, 8) otherwise (a drive Windows can't identify counts as a share); `--threads N` (MCP `threads`, up to
64) sets it for a run. Over a share a file is mostly a
wait on the server, so more threads overlap more waits: a warm compare of ~30k files over a work share took 2:08
at 8 threads and 0:32 at 32, the same verdicts. On a local disk more threads only queue. Since v1.0.140 a run
starts all its threads at once: each blocks on the server, and the runtime otherwise adds one only about every
half second past its starting count, so a 20-second phase never reached them (at work, 64 and 128 threads both
ran about 68 and timed the same).

Net: the agent pays for a summary plus what it opens; the human gets a shareable shell that lazily
loads; the 1 GB header is touched a few MB at a time and reported as a bounded, honest summary with the
full detail one `get_file_diff` (or one click) away.

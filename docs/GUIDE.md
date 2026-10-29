# CodeDiffer — how to

Each task below is a few commands you can copy, then what to look at in the output. The commands are the CLI
(`CodeDiffer.Cli.exe`, in the plugin's `bin\`; the examples call it `codediffer`). An agent does the same through the MCP tools: the
[skill](../plugin/skills/codediffer/SKILL.md) says which tool does what. The paths are made up; the outputs are from
the small example trees `docs/examples/make-examples.ps1` writes. Every flag is in `codediffer help`; the full output
model is [`OUTPUT.md`](OUTPUT.md).

Nothing here writes inside a compared tree unless you say `--write`. Results are saved under
`%LOCALAPPDATA%\CodeDiffer\results` (or `CODEDIFFER_RESULTS_DIR`).

- [Compare two trees](#compare-two-trees)
- [Look at one file's diff, or the whole patch](#look-at-one-files-diff-or-the-whole-patch)
- [Make the HTML report](#make-the-html-report)
- [Compare three trees (base, v1, v2)](#compare-three-trees-base-v1-v2)
- [Write the 3-way merge and apply it](#write-the-3-way-merge-and-apply-it)
- [Port a change onto another tree](#port-a-change-onto-another-tree)
- [Compare again later (the hash cache)](#compare-again-later-the-hash-cache)
- [Compare over a network share](#compare-over-a-network-share)
- [Check CodeDiffer on your own trees (selftest)](#check-codediffer-on-your-own-trees-selftest)
- [Find, reopen and clean up saved compares](#find-reopen-and-clean-up-saved-compares)
- [How it differs from other diff tools](#how-it-differs-from-other-diff-tools)

## Compare two trees

```powershell
codediffer compare D:\trees\release-1.0 D:\trees\release-1.1
```

```
    M README.md [whitespace]
    M assets/logo.png [binary]
    M config/settings.ini [eol]
    M src/cart.py [content]
    A src/discounts.py
    M src/inventory.py [content]
    D src/legacy_tax.py
    R src/pricing.py -> src/prices.py  (87% similar, but edited)
compare: D:\trees\release-1.0  vs  D:\trees\release-1.1
  files      9
  identical  1   (hidden)
  added      1
  removed    1
  renamed    1   pure 0 · edited 1
  modified   5   content 2 · eol 1 · whitespace 1 · encoding 0 · binary 1
  noise      0   stamps 0 · numbers 0 · comments 0   (content changes that are only that)
  ...
```

- Each changed file gets a letter (`M` modified, `A` added, `D` removed, `R` renamed) and, if it's modified, why: `content`, or only
  `whitespace`, `eol` (line endings), `encoding` or `binary`. A modified file always has a reason; none is
  reported changed with nothing to show.
- `noise` counts content changes that are only version-control stamps (`$Id: ... $`), numbers (outside code) or
  comments. An agent can leave them out (`list_files hide_noise=all`).
- Identical files are counted, not listed.
- The run is saved: its id is in `codediffer results`, and the report, a patch or a merge can come from it later
  without comparing again.

## Look at one file's diff, or the whole patch

```powershell
codediffer diff D:\trees\release-1.0\src\cart.py D:\trees\release-1.1\src\cart.py
codediffer compare D:\trees\release-1.0 D:\trees\release-1.1 --patch > release.patch
```

`--patch` writes a git-style unified diff of the whole compare to stdout (`git apply` takes it); the summary goes to
stderr. For files over a gigabyte, `codediffer blockdiff <a> <b>` streams both once and reports the changed byte
ranges, never the whole file. From an agent: `get_file_diff` (one file, capped) and `export_changeset` (the whole
patch, to a file).

## Make the HTML report

```powershell
codediffer compare D:\trees\release-1.0 D:\trees\release-1.1 --html
codediffer report <id>
```

Either one writes a static page that opens from disk: a folder tree with filters for status, reason and path,
and each file's diff, inline or side by side, loaded when you open the file. `--out DIR` chooses where it goes,
`--include-identical` lists the identical files too, and `--large` also block-diffs files over 16 MB. Live examples:
a [2-way report](https://relentlessoldman.github.io/CodeDiffer/examples/compare/) and a
[3-way report](https://relentlessoldman.github.io/CodeDiffer/examples/compare3/).

## Compare three trees (base, v1, v2)

The usual case: two lines of work that started from one release, and you want to know what each changed and
what collides.

```powershell
codediffer compare3 D:\trees\release-1.0 D:\trees\team-a D:\trees\team-b
```

```
  MG src/catalog.py  [v1 content · v2 renamed->src/products.py]  (0 conflict / 2 clean region(s))  -> src/products.py
  CF src/coupons.py  [v1 added · v2 added]  add/add  (1 conflict / 0 clean region(s))
  CF src/old_api.py  [v1 deleted · v2 content]  delete/modify  (1 conflict / 0 clean region(s))  — v1 deleted it; v2 modified it
  MG src/orders.py  [v1 content · v2 content]  (0 conflict / 3 clean region(s))
  CF src/shipping.py  [v1 content · v2 content]  content  (1 conflict / 0 clean region(s))
  AG src/version.py  [v1 content · v2 content]
...
  v1 only          1    take v1
  v2 only          1    take v2
  agreed           1    both made the identical change
  merged           2    both changed it, in separate regions: merges cleanly
  CONFLICT         3    add/add 1 · delete/modify 1 · content 1
```

- Every path either side touched lands in one bucket: `v1 only`, `v2 only`, `agreed` (both made the same change),
  `merged` (both changed it in separate places, so it merges cleanly) or `CONFLICT`, with the kind of conflict.
- The list shows the paths both sides touched (`MG` merged, `CF` conflict, `AG` agreed); `--all` lists every path.
- A rename on one side is followed: v1's edit to `catalog.py` merges into v2's `products.py`.
- `--resolve-stamps` merges a file whose only collision is keyword stamps, instead of calling it a conflict.
- Exit code 1 means there are conflicts; 0 means everything merges.

## Write the 3-way merge and apply it

```powershell
codediffer compare3 D:\trees\release-1.0 D:\trees\team-a D:\trees\team-b --merge-out D:\merge
codediffer apply-overlay D:\merge D:\work\team-a-copy            # dry run: says what it would do
codediffer apply-overlay D:\merge D:\work\team-a-copy --write
```

`--merge-out` writes the merge as an overlay on v1, in a new or empty folder:

- `files\`: the files to copy over v1. They're v2's version where only v2 changed them, the merged text where both did, and
  the text with diff3 conflict markers where they collide.
- `deletes.txt`: the paths to delete from v1 first.
- `conflicts.txt`: what needs a person. It lists the files with markers, and the conflicts it couldn't merge
  (binary, delete/modify, ...), where v1's version stays until you decide.
- `OVERLAY.txt`: the same steps, written out.

`apply-overlay` does those steps in order. It's a dry run unless `--write`, and it's safe to stop and run again. Apply it
to a copy of v1, or to v1 itself if that's what you mean to change. From an agent: `write_merge`.

## Port a change onto another tree

The change from `left` to `right` (a fix on one branch, say) applied to a third tree by 3-way merge:

```powershell
codediffer apply D:\trees\release-1.0 D:\trees\release-1.0-hotfix D:\trees\release-2.0            # dry run
codediffer apply D:\trees\release-1.0 D:\trees\release-1.0-hotfix D:\trees\release-2.0 --write
```

```
dry run · D:\trees\release-1.0 -> D:\trees\release-1.0-hotfix onto D:\trees\release-2.0
files: 2 would apply · 1 already there · 4 conflict
hunks: 3 applied · 0 fuzzy (shifted) · 1 already · 4 conflict
conflicts:
  conflict         src/catalog.py — target has no src/catalog.py
  conflict         src/shipping.py · 1 conflict (first at base line 4, target line 4)
  ...
nothing was written — run again with --write to apply the clean files
```

Each hunk is `applied`, `fuzzy` (it applied at a shifted spot), `already` (the target has it) or `conflict`. `--write` writes
the clean files and leaves conflicted ones untouched. From an agent: `apply_changeset` (a dry run unless `write=true`).

## Compare again later (the hash cache)

Nothing to do: it's on by default. The first compare of a tree reads every byte and saves each file's hash, keyed
by its size, dates and file id, in a cache outside the tree. A later compare reads only the files that changed since then.
On a ~94 GB tree over a 1 Gbit share, the first base-vs-v1 compare took 33 minutes and a later one under 2.

- `--rehash` reads everything again and refreshes the cache. `--rehash-side left|right` (or `base|v1|v2` on
  `compare3`) does that for one tree only.
- `--no-cache` reads every byte and neither uses the cache nor saves to it.
- By default each cached file's live size and dates are checked before its hash is trusted. `--fast-stat` trusts
  the directory listing alone. It's faster, but only for a tree nothing is writing to, and it can't make a patch or be applied.

## Compare over a network share

Point it at the UNC path or the mapped drive. It sees the share and runs 64 threads (8 at most on a local disk),
since each file over a share is mostly a wait on the server. `--threads N` overrides it.

If the share stops answering mid-run, the compare pauses, says so, and carries on when the share is back. It doesn't
list the files as unreadable. After `--share-wait` minutes (default 30) it stops with exit code 3, and everything it
read is cached, so running it again picks up where it left off. Ctrl+C keeps what was read too.

## Check CodeDiffer on your own trees (selftest)

```powershell
codediffer selftest D:\trees\release-1.0 D:\trees\release-1.1 --name myrepo
codediffer selftest D:\trees\release-1.0 D:\trees\team-a D:\trees\team-b --name myrepo     # 3-way
```

It runs every feature on your trees (it only reads them) and checks each result another way:

- a second compare gives the same answers;
- the MCP server answers as the CLI does;
- sampled files really differ the way they're said to;
- the patch turns left into right;
- the apply dry runs agree with the compare.

It prints `report: <path>\index.html`, a page listing the checks and what each compare cost (time, data read, peak memory), with
the history of earlier runs on that repo. Exit code 0 means every check passed; 1 means one failed. `--cold` reads every byte
first, for honest first-run timings.

## Find, reopen and clean up saved compares

```powershell
codediffer results                       # saved compares, newest first, with ids
codediffer report <id>                   # the HTML report of one, without comparing again
codediffer runs                          # how recent runs went: time per phase, memory, data read
codediffer results --prune --keep 20     # dry run; add --yes to delete
```

## How it differs from other diff tools

`diff -r`, `git diff --no-index` and the folder-compare tools (WinMerge, Meld, Beyond Compare) are good tools,
built for a person looking at a tree of ordinary size. CodeDiffer is built for two other cases:
trees of ~100 GB with single files over a gigabyte, often on a network share, and an AI agent as the reader.

| | `diff -r` | `git diff --no-index` | Folder-compare apps | CodeDiffer |
|---|---|---|---|---|
| Who reads the result | a person or a script, as one stream | a person or a script, as one stream | a person, in a window | an agent, in paged, bounded answers; a person, in a static HTML report |
| Comparing the same trees again | reads every byte again | reads every byte again | reads again, or trusts size and date | reads only the files changed since the last run |
| Why a file changed | that it changed (flags can ignore whitespace) | the patch; `--stat` counts lines | highlights; some mark minor differences | a reason on every change: content, whitespace, line endings, encoding, binary, plus noise (stamps, numbers, comments) |
| A 1 GB file | a line diff of the whole file | a line diff, or "binary" | loads it, or calls it binary | content-defined blocks, reported as changed byte ranges |
| Renames | no | yes, by similarity | varies | yes, by similarity, followed through 3-way merges and apply |
| 3-way | no (`diff3` is per file) | no | some do, mostly per file | whole trees: v1 only, v2 only, agreed, merged, conflict, plus a merge overlay to apply |
| Port a change to a third tree | `patch`, hunk by hunk | `git apply` | manual | `apply`: 3-way merge per hunk, dry run first |
| A share that stops answering | the run fails | the run fails | the run fails or stalls | it pauses and carries on; what it read is cached |
| Checking itself | — | — | — | `selftest` on your own trees; `verify` against CodeSpawner's ground truth |

What that adds up to:

- **It pays for what changed, not for the tree's size.** After the first run, a compare of two releases, of a release
  against current code, or of two trees still being worked on reads only the edited files: minutes, not hours.
- **An agent's context never floods.** A compare is a saved session you query: a constant-size summary, paged file
  lists, one capped diff at a time. A 90 GB compare costs the agent a few hundred tokens plus the files it opens.
- **It's honest at scale.** Every cap and sample is disclosed, every modified file says why, and an unreadable
  file is listed as unreadable, never as identical.
- **Its answers are checked.** Its correctness is tested against CodeSpawner's generated corpora, whose changes are
  known exactly (the 3-way merge reproduces their conflict list), and `selftest` checks it on your own trees.

Where another tool fits better: for a few files or a small tree you want to read by eye, a folder-compare app's
interactive merge view is the better seat, and inside a git repo, `git diff` between commits already knows what changed.

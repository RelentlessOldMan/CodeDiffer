# Diff Delta Contract — CodeDiffer ↔ CodeSpawner (v1, LOCKED 2026-10-02)

The ground-truth contract CodeDiffer verifies against. CodeSpawner's bulk `mutate` emits a
base→variant `<corpus>-delta.json`; CodeDiffer composes `truth = base ⊕ delta` and asserts its diff
output equals it. The stable coupling point is this schema + the `diffTruthSha` digest — **not** the
corpus bytes. Hard-assert `_meta.manifestVersion == 1` before trusting any manifest.

(Locked in the claudes-chatroom design session; CodeSpawner keeps the authoritative generator-side copy
in its own `docs/`. This is CodeDiffer's consumer-side record.)

## Granularity

Line/hunk primary; size/hash summary for binary. Structural/syntactic diff is deferred v2. Output must
be patch-applicable unified diffs (tested against `git apply`/`patch`).

## Delta schema

```jsonc
{
  "_meta": { "manifestVersion": 1, "baseSeed": 1337, "editSeed": 42, "diffTruthSha": "<hex>" },
  "fileOps": {
    "added":    ["blockA/.../new_7.c"],
    "removed":  ["blockB/.../src_5.c"],
    "renamed":  [{ "from": "…/a.c", "to": "…/b.c", "similarityMilli": 900 }],
    "modified": [
      { "path": "…/src_3.c", "reason": "content",
        "oldSha": "<hex>", "newSha": "<hex>", "oldSize": 1234, "newSize": 1240,
        "hunks": [ { "op": "replace", "oldStart": 120, "oldLines": 1, "newStart": 120, "newLines": 1 } ] }
    ]
  }
}
```

- **Hunks** — 1-based unified-diff coords. `op ∈ insert|delete|replace` (insert ⇒ `oldLines` 0;
  delete ⇒ `newLines` 0; replace ⇒ both ≥ 1). Contiguous touched lines **coalesce** into one hunk.
- **Shas** — SHA-256 of raw file bytes, lowercase hex (= CodeDiffer's prefilter hash). `oldSize`/`newSize`
  are decimal bytes (feed the size-prefilter + binary summary).
- **Reason** (THE honesty-contract ground truth; a `--edit-kind` knob generates each):
  `content | eol | whitespace | encoding | binary | metadata`.
  - `content` — textual edit; line-add/line-remove ride `content` with `op=insert/delete` (renumber path).
  - `eol` — LF↔CRLF; bytes differ, zero textual hunks.
  - `whitespace` — reindent / trailing; hunks present, all whitespace-only.
  - `encoding` — UTF-8↔UTF-16 / BOM; bytes differ, decoded text identical, zero textual hunks.
  - `binary` — blob edit; a `modified` record with shas+sizes, **zero** hunks (no byte-range truth in v1).
  - `metadata` — content identical (`oldSha==newSha`), metadata differs; fixture-sized; CodeDiffer
    metadata-diffing is OFF by default (opt-in).
  - **Never** report `modified` with no reason / empty diff.
- **Renames** — `similarityMilli = round(sim*1000)`, sim = commonLines / max(oldLineCount,newLineCount)
  over EOL-normalized lines (multiset intersection). Graded ~0.9/0.6/0.3; decoy non-renames scattered
  for false-positive scoring. `rename+edit` hunks live in the modified/hunks-explicit set keyed by `to`.

## Giant files — run-rule hunk

Files ≥ giant-min-mb use deterministic STRIDE selection; the delta carries a compact run-rule instead
of hundreds of thousands of hunks:

```json
{ "op": "replace", "kind": "run", "stride": 20, "rangeStart": 1, "rangeEnd": 1047000, "perHunk": 1 }
```

Expansion (byte-exact, both sides): touched lines = `{ rangeStart + k*stride : k=0,1,… while ≤ rangeEnd }`,
1-based **inclusive**, each a `perHunk`-length replace. CodeDiffer materializes this to explicit hunks and
patch-applies. Normal files stay explicit-hunks.

## 3-way

Joint generator (V2's line selection aware of V1's). Base B → V1, V2; emits two deltas + a conflict
manifest `{ path, baseStart, baseLines, v1Hunk, v2Hunk }` + the expected merged-clean hunk set. Model =
**diff3 line-range overlap** (git semantics): conflict iff both sides modify overlapping base ranges
differently; non-overlap → clean; identical-change-both-sides → clean (one fixture). Dial
`--overlap-fraction f` → graded 0 / 50 / 100 % conflicts.

## Integrity digests

Reuse the **exact** canonical form locked for `indirectTruthSha` (across CodeSpawner/CodeCompass/
CodeCarver): ordinal (byte-wise UTF-8) sort; `US`=0x1F between fields, `RS`=0x1E between records,
`GS`=0x1D between sections; bools as `0`/`1`; dedup-then-sort each section; **all sections always
emitted** (empty = header + zero records); `sha256` lowercase hex over the UTF-8 bytes.

`_meta.diffTruthSha` — four sections, THIS fixed order:

1. `modified-files` — `path · reason · oldSha · newSha · oldSize · newSize`
2. `hunks-explicit` — `path · op · oldStart · oldLines · newStart · newLines`   (op ∈ `insert|delete|replace`)
3. `hunks-run`      — `path · op · stride · rangeStart · rangeEnd · perHunk`
4. `renames`        — `from · to · similarityMilli`

3-way gets a **separate** `conflictTruthSha` over `[conflicts-3way, merged-clean]` (field orders pinned
at build step 4), kept off `diffTruthSha` exactly as `indirectTruthSha` is kept off `prevTruthSha`.

**Acceptance gate:** CodeSpawner ships a `diffTruthSha` golden vector in its `digest-selftest`;
CodeDiffer reproduces it independently before we call a build step locked (the bar CodeCarver held
CodeSpawner to for the first digest).

## Sharded transport (additive, CodeSpawner 1.1.x)

`mutate --shard-size N` pages a 2-way diff delta's `modified` out of the manifest; 3-way artifacts are never
sharded. `manifestVersion` stays `1`, `deltaKind` stays `"diff"`, and `diffTruthSha` is sharding-invariant
(computed over the whole record set exactly as for the monolithic delta).

- **Index** `<corpus>-delta.index.json`: `_meta` (as today plus `shardSize`, present only here), `fileOps` with
  `added`/`removed`/`renamed` inline and no `modified`, and `shards[]` = `{ file, firstPath, lastPath, count,
  shardSha }` in page order. `shardSha` = sha256 of the page file's bytes.
- **Page** `<corpus>-delta.shard-NNN.json`: `{ shardIndex, count, modified[] }`, no `_meta`. The records are
  path-ordinal sorted and sliced in order: every page but the last holds exactly `shardSize`.

CodeDiffer routes on `deltaKind == "diff"` **and** `_meta.shardSize`, refuses a page passed alone, and checks
each page against the catalog (beside the index, by file name only; sha256, `shardIndex`, `count`, first and last
path), the records ascending across pages with no path twice, and no `<corpus>-delta.shard-*.json` beside the
index that the catalog leaves out. The pages in order are `modified`, and the digest must reproduce. A missing,
extra, altered or misplaced page is a hard error, never a partial verify. Fixture: `\\IRISH\TestHole\codediffer-fixtures\sharded-delta`
(250 modified in 3 pages of 100; the monolithic delta beside it carries the same `diffTruthSha`).

## Build sequence

CodeSpawner ships each with a labeled fixture + golden vector before the next:

1. hunk schema + shas/sizes + `diffTruthSha` + digest-selftest vector
2. reason-classes + `--edit-kind`
3. rename + decoys
4. joint 3-way + `--overlap-fraction`

CodeDiffer stands up its verify-adapter against (1) first and reproduces the vector. Ping arrives on the
`code-differ` / claudes-chatroom channel when (1)'s bytes are on disk.

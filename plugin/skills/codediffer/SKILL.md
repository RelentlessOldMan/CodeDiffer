---
name: codediffer
description: Use the CodeDiffer MCP tools to diff, 3-way compare or port changes between large directory trees, instead of running diff -r, git diff --no-index or reading files side by side.
---

When you need to compare two or three directory trees (versions of a codebase, a release against a branch,
a fork against its upstream), use the **CodeDiffer** MCP tools. They handle trees of ~100 GB and single
files over 1 GB, locally or on a network share, and every answer is bounded so the context never floods.

- **start_compare** / **start_compare3**: compare 2 trees, or base + two variants (3-way: v1 only, v2 only,
  agreed, merged, conflict). They run in the background and return a compare_id; repeat compares are fast
  (hash cache).
- **get_summary**: constant-size counts; while running, progress and the differences found so far.
- **list_files**: the changed files, paged and filtered by status, reason or path.
- **get_file_diff**: one file's diff (2-way) or its merge with diff3 conflict markers (3-way), capped.
- **get_stats**: detailed totals: files and bytes per status and reason, the biggest changed files.
- **export_changeset**: the whole patch to a file. **apply_changeset**: port the changes onto a third tree
  by 3-way merge (dry run unless write=true).
- **write_merge**: a 3-way compare's merge as an overlay on v1. **write_report**: the HTML report, for a human.
- **list_compares**: saved compares (any id reopens without re-comparing). **cancel_compare**: stop one.
- **get_run_telemetry**: how recent compares ran, numbers only (safe to share): how each ended, including a run
  killed outright, peak memory, time per phase, and network vs work. A killed or stopped compare resumes by
  running the same compare again: the hashes it saved while reading are not read again.

Start with the summary and list_files, then open only the files you need. Never pass write=true unless the
user asked for the change to be written.

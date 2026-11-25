---
name: au-toolkit-shift-before-next-window-1118
description: "After a live window the toolkit's canonical app dir IS the live dir; shift the toolkit (wN→wN+1) before any rehearsal, or build -Replace moves live aside"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-18T07:46:36.994Z
---

On 18.11 at 05:39Z, rehearsal x1 reused the W15w toolkit after W15w had gone live. The toolkit's canonical App was F:\ecr-backup\land-ui\app-w15w, which is now the dir live runs from. Its scratch ProcRx only matched scratch paths, so `build-w15w -Replace` moved the live app dir aside.
- Live kept running from memory, and 36 locked DLLs stayed in place.
- Restored at 05:42Z with robocopy and hash-verified. No downtime.

**Why:** the toolkit is hard-wired to one window. After that window, its "new" app dir becomes "live".

**How to apply:** before the rehearsal for the next window:
- copy the drafts to wN+1 with placeholders: wN→wN+1, then wN−1→wN;
- set LiveSha, PrevShort, PrevApp, PrevBak, PreMigrations, PrevLastMigration, MustContain, SeedReviewed blobs, NewPermissions, ExpectedUiStringBefore, and the ProcRx char class;
- keep the K1BaselineFile path unshifted;
- build-w15x now refuses -Replace if ANY process command line contains the target dir.

Related: [[au-w15x-restore-word-1118]], [[window-active-marker]].

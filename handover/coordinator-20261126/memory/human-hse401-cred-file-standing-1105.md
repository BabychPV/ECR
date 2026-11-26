---
name: human-hse401-cred-file-standing-1105
description: "Human 05.11 — C:\\Users\\Babyc\\Downloads\\cred.txt is the standing credential file for HSE401 tools; keep it, never delete"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-08T15:28:20.433Z
---

The human, 05.11, relayed by the coordinator: «я його зберіг ще раз хай завжди його викристовуює і не видаляє» ("I saved it again; always use it and don't delete it").

**Rule:** `C:\Users\Babyc\Downloads\cred.txt` is the standing `-CredentialFile` for the tools/hse401 Live steps.
- Use it for every window.
- Do NOT delete it after a window. Earlier I deleted it after the HSE401 load.
- Never print, copy or commit its contents, and keep it outside git worktrees.
- Check only its path and format. The format is one line, TSV `username<TAB>password`.

**Why:** the human does not want to hand the file over again for every window.

⛔ **Format check = counts only.** Report the number of lines, the number of TAB parts and the length of each part. Never print any field, not even «user»: on 05.11 the separator was a space, so «field 0» was the whole line and the password ended up in tool output.

**How to apply:**
- If the format is wrong (for example, only a password), ask the human through the coordinator to fix the file.
- Never edit the file yourself.

✎ 08.11, the human in the Аудит-2 session (quiz answer «Так, лише scratch»): other sessions (Analiz, Permites, coordinator) may use this account for scratch screenshots through `.sync-local\ready\f13-runner\run-acceptance.mjs`.
- The runner reads the file in memory only and creates no accounts.
- It refuses live :6094/:5094 and any non-localhost base. Live use stays only within the windows.

Related: [[human-standing-live-window-word-1029]], [[f13-acceptance-script-1108]].

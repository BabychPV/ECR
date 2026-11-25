---
name: session-ids-1116
description: "SendMessage ids of PE, AN, AU and the coordinator sessions (as of 16.11)"
metadata:
  node_type: memory
  type: reference
  originSessionId: 1228c0fb-82af-4640-8ea1-ceed97bb30ab
  modified: 2026-11-16T17:53:55.815Z
---

Direct SendMessage ids, as of 16.11. They may change if a session restarts; verify them with ListAgents.
- Coordinator: local_1c021b21-ea97-4f6e-901c-f2138b51d42e
- PE: local_7e7ad761-49d4-49ed-8140-cf27cd12307f
- AN: local_a44029c0-4f5f-4d6c-9739-bc1572939472
- AU (Аудит-2): local_d01ea2c3-1a69-4b85-9793-996093526301

The B155 C-8 answer (coordinator): count Submitted sheets only, across the projects the template route applies to (minus projects with their own override), filtered to what the caller can see, plus a total. Related: [[human-b155-approver-roles-1116]].

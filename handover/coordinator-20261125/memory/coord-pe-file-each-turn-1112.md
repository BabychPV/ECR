---
name: coord-pe-file-each-turn-1112
description: "Coordinator's decisions for Permites live in H:\\ECR\\.sync-local\\coord\\PE.md — read it at the start of every turn"
metadata:
  node_type: memory
  type: feedback
  originSessionId: cf628973-e8fd-4d56-8225-4afdfb54feb9
  modified: 2026-11-12T03:54:42.764Z
---

From 2026-11-12 the coordinator writes its decisions for PE into `H:\ECR\.sync-local\coord\PE.md` (letters get capped). Read it at the start of every turn.

**Why:** cross-session letters are capped; the file is the durable channel.

**How to apply:** first action of each turn — Read that file; act on new lines. Related: [[questions-go-to-coordinator]], [[coordinator-mandate]].

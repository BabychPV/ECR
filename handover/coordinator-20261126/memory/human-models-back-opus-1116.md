---
name: human-models-back-opus-1116
description: "16.11 Fable at 98 % — back to Opus; subagent model per task type, passed explicitly; no Fable until 22.11"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-16T20:43:20.776Z
---

Human rule 16.11 (via coordinator), after the Fable weekly limit hit 98 %: the session runs on Opus. Subagents by judgement, model passed EXPLICITLY on every Agent call:
- opus — F13 mutation passes, anything touching code or security judgement;
- sonnet — docs / inventory / runbook edits;
- haiku — searches and read-only lookups.
No Fable anywhere until the limit resets on 22.11.

✎ Later on 16.11 (human, via the coordinator): Fable is FORBIDDEN outright, with no date given. Stop any running Fable subagent; continue inline or restart it on opus. Never pass `model: fable`.

**Why:** the temporary Fable mode ([[human-temp-fable-no-subagents-1116]]) ended with the limit; a Fable subagent stalls at 100 %.

**How to apply:** set `model` on each Agent call per the list above; never `fable` before 22.11.

✎ 16.11, later (human, via the coordinator): Fable is FORBIDDEN, not just paused until 22.11. Never pass model: fable; stop and restart any Fable subagent on opus or sonnet.

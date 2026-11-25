---
name: human-lower-pace-then-fable-1110
description: "10.11 ~15:10Z: human chose to lower the pace until the weekly reset (Sun 15.11 15:00Z) and to move sessions to Fable next week; supersedes the morning «full speed»"
metadata:
  node_type: memory
  type: project
  originSessionId: e3c77d43-fb3b-47d3-b246-67858348a93f
  modified: 2026-11-10T15:03:19.273Z
---

On 10.11 (~15:10Z) the human said: «Давай знизимо темп а наступного тижня перейдем на фейбл».

**Why:** weekly limit was at 61 % and would run out the next night. The free «Fable reset» only refunds Fable spend, which was 0 % — all usage was Opus, so the reset behind [[human-full-speed-limit-reset-1110]] gives nothing back.

**How to apply:**
- Until Sun 15.11 15:00Z: sessions finish in-flight lanes only, then stop; one session continues after the W15o window. No parallel subagents, no extra full e2e, no polling loops; coordinator control cron hourly, not every 15 min.
- From the week of 16.11: switch Аналіз, Permites, Аудит-2 to Fable (set_session_model) — do it then without asking again. The coordinator session is already on Fable.
- Supersedes [[human-full-speed-limit-reset-1110]]; consistent with [[human-budget-full-then-moderate-1108]].

✎ 11.11: Fable switch ON HOLD — human «і ми поки не працюємо на фейбеле». Stay on claude-opus-5-5 until the human says otherwise.

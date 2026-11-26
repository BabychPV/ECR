---
name: human-max-speed-reset-1111
description: "11.11 ~10:20Z: human «Прискорюйся максимально ми отримали можливість ресету для всіх моделей» — max speed, AN/PE/AU switched to Fable, PE resumed, 2 subagents each"
metadata:
  node_type: memory
  type: project
  originSessionId: 63e87d11-16e6-467b-afcb-ccd5cf06c240
  modified: 2026-11-11T10:23:23.401Z
---

Human 11.11 ~10:20Z: «Прискорюйся максимально ми отримали можливість ресету для всіх моделей». Replaces [[human-budget-full-then-moderate-1108]] (85 % cap / moderate) and [[human-lower-pace-then-fable-1110]].

**Why:** a limit reset for all models is available, so budget is no longer the constraint.

**How to apply:** AN, PE, AU switched to claude-fable-5-2 (set_session_model). AN and PE up to 2 parallel subagents, heavy .NET AN ≤2, PE ≤2, AU ≤1. PE resumed: Layout mode + Land 1.0.16.0 (AN-173, required inputs). AN: AN-171/172 → HSE400 in parallel → AN-165. Quality rules (F13, full CI, perf numbers, windows) unchanged.

**11.11 ~13:00Z:** human: «наразі ми не обмежені токенами потрібна швидкість та якість, але якщо є можливість оптимізувати і зробити більше роботи за тиж токени то ок» — tokens are not a constraint; speed + quality first; efficiency welcome but never at the cost of speed. Don't warn about the weekly % any more.

**11.11 ~16:00Z:** human: «і ми поки не працюємо на фейбеле» — all sessions (AN, PE, AU, coordinator) run on claude-opus-5-5 (checked get_session); do NOT switch to Fable until the human says so. The «from 16.11 → Fable» plan is on hold.

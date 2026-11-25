---
name: subagent-model-choice-1102
description: "02.11 human via coordinator: set model on every Agent call — haiku logs/CI/listing, sonnet routine F13 short/stand runs, opus migration/data/grants/live windows"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-16T14:36:02.429Z
---

Human 2026-11-02 (verbatim, relayed by coordinator): «не забувай про доцільне використання токенів та моделей для саб агантів без втрати якості».

Mapping (coordinator): haiku = log/CI reading, file listing; sonnet = routine F13 short form, stand runs; opus = migration/data/grant analysis, live-window decisions. Unsure → higher. Reports ≤5 lines.

✎ 03.11 human (quiz via coordinator): moderate economy, target ≤0.6 %/h of weekly budget, no stopping. opus ONLY for design/migration/security; sonnet for implementation and tests; haiku for search/CI logs; one agent at a time; letters ≤5 lines (hash, CI ids, flags). Quality gates unchanged.

✎ 06.11 human (via coordinator) — SUPERSEDES the 03.11 sonnet line: any subagent that writes code, fixes, tests or does perf work runs on **opus** ("so the batch works on the first try"); haiku/sonnet only for pure read-only search. A sonnet agent's partial edits were reviewed/redone by opus.

✎ 09.11 human directly to the coordinator: «не забувай в залежності від задачи обирати оптимальну можель для сабагентів і це мають робити і інші сесії». The 06.11 table stays. Every Agent call names its model and gives a one-line reason. All three sessions were told the same on 09.11. Final mapping:
- **haiku:** reading logs and CI, grep, file lists, status probes;
- **sonnet:** read-only review, F13 of a UI-only RC, stand runs, screenshots, writing docs;
- **opus:** any code, fix, test or perf work; migrations, Sql, grants; security; live-window decisions.

✎ 16.11 human (via coordinator, Fable at 98 %): back to the 09.11 table — session on Opus; opus = code, tests, mutations, perf; sonnet = docs, letters, plans, simple mechanical edits; haiku = searches and read-only lookups (Explore-type). NO Fable anywhere (no `model: fable`) until the reset on 22.11. Pass the model explicitly on every Agent call.

**Why:** weekly token burn rising (11→20 % in a few hours on 02.11).

**How to apply:** always pass `model` in Agent calls. Full F13 with migration/Sql/grants and live windows → opus; F13 of UI-only RCs → sonnet. Related: [[subagent-use-by-judgement-1030]], [[token-saving-loop]].

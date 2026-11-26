---
name: human-quality-acceptance-rules-1108
description: "08.11 human «якість реалізації складних задач погана» — acceptance script, live-backup hand pass, fresh-eyes agent, leftover grep, mockup first"
metadata:
  node_type: memory
  type: feedback
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-11-08T10:21:54.597Z
---

Rules for every complex or UI lane, effective 08.11 (via the coordinator, from the human's complaint «якість реалізації складних задач погана»):
1. Before code, the coordinator sends an acceptance script in the human's words (screen → action → what the user sees). READY needs evidence for EVERY line (screenshot + pass/fail), else no RC.
2. Hand pass on a NEW scratch DB restored from the LIVE backup (never over EcrLandUi), not on seed data.
3. A «fresh eyes» agent with no code context walks the script as a new user; anything confusing is P2 and blocks.
4. Removing a concept → grep for leftovers (texts, tooltips, other pages, seed).
5. Big UI restructures: clickable mockup first (docs/design/hybrid), send the path, NO code until the human approves.

**Why:** UAT 08.11 found unclear banners, stale texts, screens that disagree, filters that don't filter — green CI and seed-data e2e didn't catch them.
**How to apply:** don't start UI code without a script; budget the live-backup restore + fresh-eyes walk into every READY. Related: [[ui-follows-hybrid-mockup]], [[human-quiz-answers-1104]].

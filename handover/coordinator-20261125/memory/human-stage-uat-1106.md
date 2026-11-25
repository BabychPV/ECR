---
name: human-stage-uat-1106
description: Human 06.11 — the project is in testing/UAT; prod is too early, so pre-prod DB/IB items are not urgent
metadata:
  type: project
---

The human, 06.11: «ми поки тестуємо тому поки вихід на прод зарано зараз UAT».

**Why:** the human is testing on the live stand; production rollout is not planned yet.
**How to apply:**
- Priorities are UAT defects and features from the human's testing.
- DB-1..DB-9 and IB-1/IB-2 (the «перед продом» items) are deferred. Don't push them on the human or the DBA now.
- AU-13 (two clean passes) stays as the gate before a UAT handout.

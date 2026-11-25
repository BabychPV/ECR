---
name: stacked-pr-base-delete
description: Мерж батьківського PR із --delete-branch закриває дочірній stacked PR; плюс на stacked-базі тригеряться не всі сім гейтів
metadata: 
  node_type: memory
  type: feedback
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-09-19T08:05:22.047Z
---

У stacked PR (`gh pr create --base <гілка-батька>`) дві пастки, обидві тихі:

1. **`gh pr merge <батько> --squash --delete-branch` ЗАКРИВАЄ дочірній PR**
   разом із видаленою базовою гілкою. Дочірній PR після цього **не
   відкривається назад**: `gh pr reopen` дає
   `GraphQL: Could not open the pull request`, а `gh pr edit --base main` —
   `Cannot change the base branch of a closed pull request`. Гілка-голова ціла,
   тож лікується лише **новим PR** із тією самою головою і базою `main`.

2. **На stacked-базі тригеряться не всі гейти.** У цьому репозиторії PR із
   базою, відмінною від `main`, отримує лише `client`, `server`,
   `a11y (dark)`, `a11y (light)` — `build`, `test` і `honesty-guard` не
   запускаються взагалі. Виглядає як «4/7 зелених, нічого не йде», і легко
   прочитати як збій CI.

**Why:** обидві пастки коштують часу саме тоді, коли черга мержів найдовша —
дочірній PR зникає в мить, коли батько нарешті пройшов.

**How to apply:** перед мержем батька — `gh pr edit <дочірній> --base main`
(поки той ще відкритий). Тоді `--delete-branch` безпечний, і дочірній одразу
починає ганяти всі сім гейтів. Порядок мержу лишається той самий: батько,
потім дочірній.

Пов'язане: [[merge-cadence]], [[dev-rules]].

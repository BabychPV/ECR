---
name: verify-ci-not-local-subagent-claims
description: "'Усі гейти зелені' спиралось на локальні прогони інтегратор-сабагента, не на факт GitHub CI — реальний honesty-guard був FAILURE в той момент; перевіряти статус PR напряму через gh, не довіряти 'зробила й перевірила' як синоніму 'CI зелений'"
metadata:
  type: feedback
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-11-09T18:34:50.054Z
---

2026-09-23: після серії влитих клієнтських гілок я написала Головному "усі
гейти зелені" — але це твердження базувалось ЛИШЕ на тому, що
сабагент-інтегратор локально прогнав tsc/eslint/vitest/dotnet test у
своєму worktree й отримав зелені числа. Головний прямо зупинив: реальний
статус PR на GitHub CI (`gh pr view 458 --json statusCheckRollup`) на той
момент показав `honesty-guard: FAILURE` (справжній, не транзиєнтний —
`NotImplementedException` у бойовому коді `IEvaluationContext.cs:60`) і
`server: IN_PROGRESS`, `mergeStateStatus: BLOCKED`.

**Why:** локальний прогін тестів у worktree сабагента і фактичний CI-гейт
на GitHub — це РІЗНІ речі. Локальний прогін не бачить сторожів, що
працюють лише в CI-середовищі (`honesty-guard.sh` у цьому репозиторії),
і не відображає реальний стан гілки на origin, доки CI не відпрацював.
"Зробила й перевірила" (локально) ≠ "запушила й CI дав зелений" —
Головний прямо назвала цю різницю.

✎ Уточнення того самого дня: сам `honesty-guard: FAILURE` виявився
хибнопозитивним сторожа (`.github/guards/honesty-guard.sh` ловив підрядок
"NotImplementedException" у XML-doc-КОМЕНТАРІ, що пояснював, чому винятку
НЕМАЄ — не в самому коді; `git grep` по всьому `dev/integration` дав рівно
один збіг, саме цей коментар). Це не скасовує урок нижче: сам факт, що я
написала "усі гейти зелені" ДО перевірки CI, лишається помилкою незалежно
від того, справжнім чи хибним виявився конкретний червоний чек. Пов'язане:
[[source-text-guards-are-brittle]] (той самий клас крихкості — сторожі по
тексту джерел падають на коментарях).

**How to apply:** перш ніж писати "усі гейти зелені"/"готово до мержу"
комусь (людині чи peer-сесії) — перевірити статус PR НАПРЯМУ:
```powershell
$env:PATH = "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd" + ";" + $env:PATH
gh pr view <N> --json statusCheckRollup,mergeable,mergeStateStatus
```
(`gh` сам по собі є в `PATH` через `Get-Command gh`, але потребує `git` у
`PATH` для роботи з репозиторієм — а звичайний `git`/Git for Windows тут
відсутній узагалі, лише всередині Visual Studio, звідси явний шлях вище;
див. [[toolchain-paths]]). Якщо якийсь чек ще `IN_PROGRESS` — так і писати
"CI ще йде", не екстраполювати з локальних чисел сабагента на весь гейт.

✎ 2026-11-09: те саме з e2e. Агент написав «усі шарди пройшли після
rerun», я переслав координатору як READY. Насправді в спробі 2 перезапустили
лише шарди 1–2, а шард 3/6 (apt 429) лишився червоним, і run уже не можна
було перезапустити. **Перед «e2e зелений» перевіряй кожен шард ОСТАННЬОЇ
спроби** (`gh run view <id> --json jobs` → conclusion кожного job), а не
звіт агента.

Пов'язане: [[toolchain-paths]] (шлях до git/gh), [[full-suite-before-push]]
(інший аспект тієї самої дисципліни — не економити на повноті перевірки).

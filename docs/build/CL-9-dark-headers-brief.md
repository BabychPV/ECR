# CL-задача: Dark-шапки сітки і світла заливка (розширення Q12 «B»)

Джерело: приймальна живого стенда 5094 (13.10), знімки dark-01-T7_00 і dark-05-T9a_17a (F:\ecr-backup\land-ui\acceptance-2026-10-13\rcu\screenshots\). Рішення: координатор (за мандатом), 13.10; людиною пряму не підтверджено — позначити в реєстрі як «рішення координатора».

## Рішення (для docs/build/decisions або QUIZ-ANSWERS-нотатки, docs-коміт через Аудит-2)
Q12 «B» розширюється на ШАПКИ колонок: у темній темі світла заливка (відносна яскравість ≥ 0,8, функція isLightFill) відображається токеном `--sunken` (макет D15-01), а не літералом кольору шаблону; насичені/темні заливки лишаються як є. Колір не вигадується: заливка автора не змінюється в даних, лише не світиться світлим на темному. Світла заливка КОМІРОК даних (LAND_INPUT #FFFFFF) у Dark = «без заливки».

## Задача для хмарної лінії (клієнт, від main, один lane)
Стан: правило Q12 не реалізовано. Файли:
- `src/Ecr.Web/src/features/grid/cellAppearance.ts:56-59` (`cellAppearanceOf`): додати спільну `isLightFill(hex)` (luminance ≥ 0,8; `shared/theme/contrast.ts` вже має luminance/contrast); видавати пару `--ecr-cell-fill-light` / `--ecr-cell-fill-dark` замість єдиного `--ecr-cell-fill`; у dark для світлої заливки змінну не задавати (або `--sunken` для шапок), текст на заливці підбирає `bestTextOn`.
- `src/Ecr.Web/src/features/grid/cellEditors.css:123-131` (комірки) і `:161-168` (шапка `.rgHeaderCell.ecr-header-styled`): dark-варіант `var(--ecr-cell-fill-dark, …)`, шапка — `--sunken`.
- `cellAppearanceClassOf` (:77-86): клас `ecr-cell-filled` у dark не ставити для світлих заливок.
- `userCellStyle.ts:124` `headerStyleProps` — перевірити, що шлях шапки проходить через ту ж логіку.
Не змінювати: серверну модель, дані StyleDef, Light-тему.

## Тести (обов'язково, червоне до фіксу)
- unit у `features/grid/__tests__`: світла заливка → немає `--ecr-cell-fill-dark`; насичена (#CCFFCC? ні — світла; #FF0000 — насичена) лишається; текст ≥ AA.text.
- e2e Playwright dark на реальному стенді: `P1-V1-0003` (7.0 Water consolidation, шапка C6D9F1) і `P1-V1-0010` (9a.17a, шапка CCFFCC): `getComputedStyle(.rgHeaderCell).backgroundColor` не світлий (luminance < 0,5) у dark і без змін у light.
- Перевірити `keyboardPath.spec.ts` і наявні знімкові тести grid (читають фон шапок); оновити знімки Light/Dark.
- Знімки Light/Dark T7_00 і T9a_17a (№3 TableDef 322, не №4 застарілий залишок) для звіту.

## Пов'язане
- Дублі «9a.17a/21a/21b» (№4/№6 = застарілі T9a_03/06/07) — не цей lane; лікується Prune.
- P2 список документів 44–51 с — LP-45 (окрема діагностика).

# CL-задача: Dark-шапки сітки і світла заливка (розширення Q12 «B»)

**Статус:** реалізовано в RC41 «Ч» (`rc/2026-12-06`; lane `lane/cloud/dark-column-headers` `181da90e`, зведено через `lane/integ-ch2`); CL-9 `done` після мержу #527. Бриф нижче приведено до фактичної реалізації (початкова редакція від 13.10 мала поріг 0,8 і заливку комірок — вони не діють).

Джерело: приймальна живого стенда 5094 (13.10), знімки dark-01-T7_00 і dark-05-T9a_17a (F:\ecr-backup\land-ui\acceptance-2026-10-13\rcu\screenshots\). Рішення: координатор (за мандатом), 13.10; людиною пряму не підтверджено — позначити в реєстрі як «рішення координатора».

## Рішення (діє)
Q12 «B» розширюється на ШАПКИ колонок: у темній темі світла заливка шапки (відносна яскравість WCAG **≥ 0,6**, `isLightHeaderFill`, константа `LightHeaderLuminance`) відображається токеном `var(--ecr-sunken)` (макет D15-01), а не літералом кольору шаблону; насичені/темні заливки лишаються як є (`#FFC000` L≈0,58 — ні, `#C6D9F1` L≈0,68 — так). Колір не вигадується: заливка автора не змінюється в даних, лише не світиться світлим на темному. Колір тексту автора не підміняється. Заливки КОМІРОК даних (`cellStyles`) цією задачею не змінюються.

## Реалізація (за `origin/lane/integ-ch2`)
- `src/Ecr.Web/src/features/grid/userCellStyle.ts`: `LightHeaderLuminance = 0.6`; `isLightHeaderFill(fill)` (hex + `luminance(fill) ≥ 0,6`); `headerStyleProps` додає клас `ecr-header-fill-light` (поруч із `ecr-header-styled`) лише для світлої заливки шапки.
- `src/Ecr.Web/src/shared/theme/contrast.ts`: `luminance` експортовано.
- `src/Ecr.Web/src/features/grid/cellEditors.css`: у Dark `.rgHeaderCell.ecr-header-styled.ecr-header-fill-light` → `background-color: var(--ecr-sunken)`, колір тексту — `var(--ecr-cell-fg-dark, var(--ecr-grid-text))`.
- Не змінено: серверна модель, дані StyleDef, Light-тема, `cellAppearance.ts` (пара `--ecr-cell-fill-light/dark` не вводилась).

## Тести
- unit `features/grid/__tests__/userCellStyle.test.ts`, «Dark: шапка колонок»: `isLightHeaderFill` на 5–6 кольорах (`#C6D9F1`, `#CCFFCC`, `#D9D9D9`, `#FFFF00` — світлі; `#FFC000`, `#1F3864` — ні); клас `fill-light` лише для світлої; `--ecr-header-fg-dark` не з'являється.
- e2e `src/Ecr.Web/e2e/headerFillDark.spec.ts` (`ECR_E2E_OPTIONAL=1`, сторінка `/_key-commit-gate/document?headerFill=<hex>`, не живий стенд): P1-V1-0003 (`c6d9f1`), P1-V1-0010 (9a, `ccffcc`), `d9d9d9` — у Dark luminance фону < 0,2; у Light — заливка автора без змін; `#ffc000` і `#1f3864` у Dark не змінюються.
- Знімки Light/Dark T7_00 і T9a_17a (№3 TableDef 322, не №4 застарілий залишок) — для звіту приймальної.

## Пов'язане
- Дублі «9a.17a/21a/21b» (№4/№6 = застарілі T9a_03/06/07) — не цей lane; лікується Prune.
- P2 список документів 44–51 с — LP-45 / CL-10 (окрема діагностика).

# Конвеєр зведення RC (ECR)

Джерело цього документа — фактичний конвеєр зведення RC 12–15.10.2026, а не нова
політика. Первинним лишається `CLAUDE.md`; за розбіжності чинний він. Документ
фіксує порядок дій, шаблони листа й опису PR та типові пастки, на яких уже
втрачали час.

## 1. Роль «зведення RC»

- Збирає lane-гілки в `rc/<дата>` і відкриває PR `rc/<дата>` → `main`.
- Після всіх перевірок передає координатору PASS-рядок (шаблон — розділ 3).
- Мерж робить координатор: `gh pr merge N --merge --match-head-commit <sha>`,
  без `--delete-branch`.

## 2. Порядок зведення

**(a) Склад.** Lane від СВІЖОГО `origin/main` (або stacked, якщо джерело stacked),
лінійно:

- `git cherry origin/main origin/lane/<x>` — брати лише коміти зі знаком `+`;
- `git cherry-pick -x`;
- dev-merge-коміти і дублі комітів, що вже є в `main` (інший хеш, той самий
  patch-id), НЕ брати.

**(b) Конфлікти.**

- `docs/CHECKSUMS.txt` і `docs/tz/10-decisions.md`: залишити всі рядки, `D-nn` за
  порядком; суму брати з повідомлення падіння `SpecificationBaselineTests`, не
  руками.
- `docs/build/*` — береться «наше» (ours).
- `model.json` НІКОЛИ руками: лише `tools/land/model-gen/run-model-gen.ps1` і
  перевірка SHA256 до/після.
- BOM і LF зберігати: UTF-8 BOM у `tools/land/*.ps1` і README — побайтна
  перевірка `EF BB BF`.

**(c) Перевірки перед пушем.**

- `ParseFile` для `tools/land/**/*.ps1` — 0 помилок;
- `dotnet build Ecr.sln`;
- `dotnet test tests/Ecr.Architecture.Tests` повністю;
- `git merge-tree --write-tree --name-only origin/main HEAD` — чисто.

**(d) Пуш.** У нову lane: `git push origin HEAD:refs/heads/lane/integ-<x>`.
Один повний CI на SHA.

**(e) RC і PR.** `rc/<дата>` ставиться на SHA зеленого CI. PR з описом за
шаблоном розділу 4; ризики називати прямо.

**(f) Upgrade і smoke.** `main` → tip ×2 + smoke 25/25 — лише якщо є міграція,
seed чи зміна в `src`. Для tools/tests/docs — діф-аргумент замість прогону.
Вікно: `F:\EcrData`, порт з вільного діапазону, DROP баз після прогону,
worktree лише під `H:\ecr-wt`.

**(g) PASS-рядок** координатору (розділ 3).

**(h) Після мержу.**

- main → dev: merge-коміт, ff-пуш;
- зняти `rc` і lane з bundle (критерій: ancestor `main` або `git cherry` без `+`);
- `git worktree remove`.

## 3. Шаблон PASS-листа

Одним рядком, далі вкладені пункти:

```
PASS: PR #<N>, rc/<дата> @ <sha>
  - склад комітів: <кількість, lane-джерела>
  - 8 required: build, test, honesty-guard, server, client, a11y (dark), a11y (light), pr-source — <зелені/skipping>
  - CI/Evidence: <run id, вердикт>
  - ч.1 (хто/вердикт): <виконавець, PASS/FAIL>
  - upgrade/smoke: <main→tip ×2 числа, smoke N/N або діф-аргумент>
  - ризики: <прямо, або «немає» з обґрунтуванням>
  - мерж: gh pr merge <N> --merge --match-head-commit <sha>
```

## 4. Шаблон опису PR

```
Клієнтські залежності: <немає / перелік>

## What
<що змінено>

## Why
<чому>

## How tested
<команди й результати>

## Risks
<ризики прямо>

## Depends on
<#N / немає>

🤖 Generated with [Claude Code](https://claude.com/claude-code)
```

## 5. Правила швидкості

- Один повний CI на SHA.
- RC = SHA зеленої пачки.
- Upgrade паралельно зі збіркою, не після.
- Без дублів push + PR.
- Ч.1 Аналізу — паралельно, не після.
- `e2e-stand` не required: якщо йде понад 60 хв — мерж за обґрунтуванням.
- Після мержу СКАСОВУВАТИ зайві CI/e2e на змерджений `rc` і зняті lane.

## 6. Скрипти вікон (Land)

- Виклик `Load-LandRows` / `Invoke-LandRecalc` / `Build-LandTemplate` — лише
  через hashtable-splat: `$p = @{ Param = 'v' }; & .\script.ps1 @p`.
- Перед запуском на стенді — перевірка scriptblock / `ParseFile`.
- `ConfirmStand` обов'язковий.
- Пастки Windows PowerShell 5.1: `Dictionary.Contains`, `GetNewClosure`,
  `-AsPlainText`.

## 7. Заборонене

- `--force`, `--no-verify`, `--admin`;
- cherry-pick/checkout у `H:\ECR` (лише worktree під `H:\ecr-wt\`);
- обхід класифікатора;
- видалення гілок без bundle і критерію;
- мерж по «ще йде».

## 8. Типові пастки (фактичні інциденти)

- Дублі комітів при stacked-зведенні: `merge-tree` чистий, а в PR конфлікт.
- `-B` у `git worktree add` перезаписує локальну гілку.
- BOM зникає після ручного розв'язку конфлікту.
- Не запускати Architecture без git у `PATH` (`StatusFromGitScriptTests`).
- Тест із великою заливкою роздуває тестову БД понад 192 МБ: порціями +
  `CHECKPOINT`.
- `Add-Type System.Web.Extensions` відсутній на Linux CI.
- NuGet-advisory NU1902/NU1903 як помилка: bump родини пакетів, не suppress.

## 9. Підтвердження

Розділи вище виведено з інцидентів зведення RC 12–15.10.2026 (серія тижневих і
денних RC-PR до `main`): конфлікти stacked-зведення, дублі patch-id, зникнення
BOM у `tools/land`, падіння `StatusFromGitScriptTests` без git, роздуття тестової
БД, відсутність `System.Web.Extensions` на Linux, NU1902/NU1903. Номери окремих
PR навмисно не наведено — вони не перевірялися при написанні документа.

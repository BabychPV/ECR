# Лист готовності: lane/integ-seed-split (сід 09-seed.sql → частини seed/)

- Гілка / lane: `lane/integ-seed-split`
- Пункт: поділ сіду, щоб паралельні lane не конфліктували на хвості 09-seed.sql
- Хеш tip: `a5a86e29bd826714e88f3097b0a20bfb0262700a`
- База: `origin/main` `4cfeff59d4ef7aed007f05d7a7edf6effc262b1d` (#671)
- Лінійна: 2 коміти, merge-комітів 0. `ac1f38630` — механічний (згенеровано tools/split-seed.ps1), `a5a86e29b` — код/споживачі/сторожі
- Sql: так — `Persistence/Sql/09-seed.sql` → `Persistence/Sql/seed/00-prelude.sql` + `seed/features/*.sql` (342) + `seed/99-epilogue.sql`; додано лише каркас #UiChanged/#UiRemoved (порожні таблиці, без ефекту)
- EF-міграція: ні. Контракт openapi/schema.d.ts: ні
- shared/ui, package*.json, lock, CLAUDE.md, .github: ні (src/test/seedSql.ts — новий тестовий хелпер)

## ⚠ Перезапуск перед RC
re-run: tools/split-seed.ps1 on newest main before RC.
Якщо в main після 4cfeff59d хтось змінив 09-seed.sql (напр. origin/rc/2026-11-02-p102 → +COLL:ui-live-refresh):
`git checkout origin/main -- src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql` → `powershell -NoProfile -File tools/split-seed.ps1` (~20–45 с, сам перевіряє побайтну реконструкцію й видаляє 09-seed.sql) → `[CHORE]`-коміт. Спробу на p102 зроблено: OK, +1 файл фічі.

## CI
- CI (повний, push lane/integ-*): run 41392869683 — success
- Evidence: run 41392869737 — success

## Докази
- (a) Реконструкція з файлів на диску = оригінал побайтно (sha256 25315B8A… = 25315B8A…, 2 996 682 байти); режим -Verify червоніє на зміні 1 байта
- (b) Свіжа схема (-SmallFiles) EcrSeedSplitA (оригінал) / EcrSeedSplitB (зведений сід): 154 таблиці, 15 непорожніх, 20 673 рядки, EXCEPT в обидва боки 0 (sys_ecr.UiString 20303, Language, UiStringRevision, sec.Permission 48, Role 9, RolePermission 104, User, PasswordPolicy, uom.Dimension 23, Unit 59, doc.PeriodPolicy, rpt.ReportDef 4, ReportVersion 4, aud.SecurityEvent 7, __EFMigrationsHistory); Revision 3 = 3
- (c) Повторний прогін — 0 змін (лічильники + CHECKSUM_AGG усіх таблиць, Revision 3); проба #UiChanged/#UiRemoved: застосовано раз, повтор — 0
- Бази EcrSeedSplitA/B і свої EcrTest_* видалено

## Локально
- build ✓; Architecture 1157/1176 (19 — StatusFromGitScriptTests, середовище); Infrastructure Seed* 41/41; Calculations/Adapters/Application/Api/Scenarios — вузько, зелені; vitest 16 файлів 348/348, a11y 12/12; tsc, eslint чисті

## Хто зводить
- Координатор (RC з SHA a5a86e29b після перезапуску поділу на найновішому main)

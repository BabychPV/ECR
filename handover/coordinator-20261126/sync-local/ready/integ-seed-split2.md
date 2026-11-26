# Лист готовності: lane/integ-seed-split2 (сід 09-seed.sql → частини seed/, на methodology-rc2)

> ⛔ ЗАСТАРІЛО: замінено `lane/integ-seed-split3` @ 554b4d60f (база 9cb658da9) — див. integ-seed-split3.md.

- Гілка / lane: `lane/integ-seed-split2` (замінює `lane/integ-seed-split` @ a5a86e29b — та гілка на старій базі 4cfeff59d)
- База: `origin/lane/integ-methodology-rc2` `2c237f3553c45be62b1328c96217dba36c50b769` (= main + live-refresh p102 + methodology; тип не рухався)
- Хеш tip: `920c01102d73ac92d53a563b971ded2cd8ed4ce3`
- Лінійна: 2 коміти, merge-комітів 0. `013380ec7` — лише вивід tools/split-seed.ps1 (версія з a5a86e29b) + сам скрипт; `920c01102` — cherry-pick a5a86e29b (код, споживачі, сторожі), без конфліктів
- Sql: 09-seed.sql → seed/00-prelude.sql + seed/features/*.sql (345) + seed/99-epilogue.sql; додано лише каркас #UiChanged/#UiRemoved (порожній)
- EF-міграція: ні. Контракт: ні. CLAUDE.md, .github, lock: ні

## ⚠ Перезапуск
if methodology tip moves, re-run split-seed.ps1 (19 s): `git checkout <новий tip> -- src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql` → `powershell -NoProfile -File tools/split-seed.ps1` → `[CHORE]`-коміт лише з виводом скрипта.

## CI
- CI (повний, push lane/integ-*): run 41397027075 — success
- Evidence: run 41397027123 — success

## Докази
- -Verify: реконструкція з файлів на диску побайтно = 09-seed.sql бази (sha256 5D7DBE77… = 5D7DBE77…, 3 020 555 байт)
- Нові файли проти поділу на 4cfeff59d: лише COLL-ui-live-refresh, COLL-ui-methodology-categories, METH-categories — тому EXCEPT-доказ БД не повторювався (на 4cfeff59d: 154 таблиці, EXCEPT 0/0, повтор сіду — 0 змін; див. integ-seed-split.md)
- Локально: build ✓; Architecture 1176/1176 (з SeedSplitLayoutTests); Infrastructure Seed* + DeployScriptsRerun 41/41; свої EcrTest_* видалено
- Старий worktree H:\ecr-wt\seed-split видалено

## Хто зводить
- Координатор (RC з SHA 920c01102; повний CI уже зелений на цьому SHA)

# Лист готовності: lane/integ-seed-split3 (сід 09-seed.sql → частини seed/, на methodology-rc2 9cb658da9)

- Гілка / lane: `lane/integ-seed-split3` (замінює `lane/integ-seed-split2` @ 920c01102 і `lane/integ-seed-split` @ a5a86e29b)
- База: `origin/lane/integ-methodology-rc2` `9cb658da9ab9018517fb52244160346e47430e89` (від 2c237f355 відрізняється лише src/Ecr.Web/e2e/methodologyCategories.spec.ts)
- Хеш tip: `554b4d60f2a55caf930674a87749899b8e3964d6`
- Лінійна: 2 коміти, merge-комітів 0. `49405ca26` — лише вивід tools/split-seed.ps1 + скрипт; дерево seed/ і скрипт ідентичні 013380ec7. `554b4d60f` — cherry-pick 920c01102 без конфліктів
- Sql: 09-seed.sql → seed/00-prelude.sql + seed/features/*.sql (345) + seed/99-epilogue.sql; каркас #UiChanged/#UiRemoved (порожній). EF-міграція, контракт, CLAUDE.md, .github, lock: ні

## ⚠ Перезапуск
if methodology tip moves, re-run split-seed.ps1 (19 s): `git checkout <новий tip> -- src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql` → `powershell -NoProfile -File tools/split-seed.ps1` → `[CHORE]`-коміт лише з виводом скрипта → cherry-pick коду.

## CI
- CI (повний, push lane/integ-*): run 41400161145 — success
- Evidence: run 41400161055 — success

## Докази
- -Verify: реконструкція побайтно = 09-seed.sql бази (sha256 5D7DBE77…); доказ БД (EXCEPT 0/0, повтор — 0 змін) — у integ-seed-split.md, нових секцій немає
- Локально: build ✓; Architecture (Seed*) 30/30; Infrastructure Seed* + DeployScriptsRerun 41/41; свої EcrTest_* видалено; worktree seed-split2 знято

## Хто зводить
- Координатор (RC з SHA 554b4d60f; повний CI уже зелений на цьому SHA)

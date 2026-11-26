# Лист готовності: lane/integ-seed-uien

> ✎ ЗВОДИТИ: **`lane/integ-seed-uien-onmain` tip `f750b01f6`** (= той самий перенос, перебазований на main `ddf5f2fc3` після p118 #688; «Аналіз»). При rebase конфлікт лише в 00-prelude.sql: узято версію гілки, 2 en-рядки p118 `liveStatus.changedBy/changedByOther` перенесено порцією #UiEn у `features/COLL-ui-live-status.sql` (+ шапки двох COLL-файлів виправлено). Локально: Architecture 1181/1181, Infrastructure ~Seed 39/39. CI на f750b01f6: CI 41510001367 **success**, Evidence 41510001366 **success** (перевірено «Аналізом»). Стара `lane/integ-seed-uien` (b0acc1b38) — лише історія.

- [x] Гілка / lane: `lane/integ-seed-uien` (worktree `H:\ecr-wt\seed-uien`)
- [x] Задача: en-рядки сіду — per-feature (`#UiEn` у `seed/features/*.sql`), щоб lane з en-текстом не конфліктували на en-MERGE прологу
- [x] Хеш tip: `b0acc1b38` (2 коміти: `72ca14b7f` [CODE] + `b0acc1b38` [DOCS])
- [x] База: `origin/main` `d20b69916` (перевірено перед пушем — main не рушив)
- [x] Лінійна: merge-комітів 0
- [x] Мутацій у кінчику немає
- [x] Sql: так — лише `Sql/seed/**` (00-prelude, 99-epilogue, 352 файли features, новий `CATALOG-en-base.sql`); схема/міграції — ні
- [x] EF-міграція: ні
- [x] Контракт: ні
- [x] shared/ui: ні
- [x] package*.json / lock / Directory.Packages.props: ні
- [x] CLAUDE.md: так — окремий [DOCS]-коміт, лише речення «en — тимчасово в en-MERGE 00-prelude.sql» (розділ seed split + клітинка таблиці гарячих файлів) → «en також — у seed/features/<AREA>-<slug>.sql (порція #UiEn)»
- [x] Нові ключі UiString: ні (6843 en-рядки до = 6843 після, перенесено дослівно)
- [x] Нові коди помилок: ні
- [x] Тести: SeedSql.EnCatalog/EnCatalogOf (C#) і readSeedEnCatalog/enCatalogOf (TS) — єдина точка розбору en-каталогу; 7 сторожів C# і 7 клієнтських тестів переведено на неї; новий сторож `En_рядки_каталогу_лише_у_файлах_фіч` (з доказом червоного); `FeatureProblems` приймає `#UiEn`, не `#UiEnCounts`
- [x] Доказ еквівалентності даних (локально, EcrUiEnA/B/C, знесено):
  (a) свіжа база зі старого main vs з гілки — EXCEPT в обидва боки 0 по 154 таблицях, Revision 3=3;
  (b) оновлення зіпсованої old-main бази (старе значення #UiChanged-рядка, прибраний ключ, видалені en/ru/kz) — старий і новий сід дають ідентичні бази, Revision 3→5 в обох;
  (c) повторний новий сід — 0 змін (з мітками часу), Revision незмінний.
- [x] CI на lane (push, повний набір `lane/integ-*`, tip b0acc1b38): CI run 41502885103 — **success** (server-part ×10, client, a11y dark/light, windows/msi/msi-install, lane-shape — усі зелені); Evidence run 41502885141 — **success**
- [x] Перевірено локально: build ☑  Architecture 1181/1181 ☑  Infrastructure ~Seed 39/39 ☑  vitest зачепленого 1413/1413 + a11y 8/8 ☑  eslint/tsc ☑
- [x] Ризики: ⚠ **lane з en-рядками в 00-prelude.sql (air-params-ui2, RC p118 тощо) при rebase мусять перенести свої en-рядки у файли фіч** — порцією `INSERT INTO #UiEn ([Key], Lang, Val, Scope) SELECT v.[Key], v.Lang, v.Val, v.Scope FROM (VALUES …) AS v ([Key], Lang, Val, Scope) OPTION (RECOMPILE);` + `GO` у своєму `features/<AREA>-<slug>.sql`; новий сторож червоніє на en-порції в пролозі. **Якщо main зрушив до зведення — перегнати перенос на найновішому main** (скрипт переносу: `.sync-local\ready\integ-seed-uien.move-en.ps1 -Wt <worktree на main> [-DryRun]`; доказ: `.sync-local\ready\integ-seed-uien.proof.ps1 -Wt <worktree> -Step prep|schema|a|cmp-a|c|b|drop`) і повторити доказ (a)–(c).
- [x] Хто зводить: координатор / інтегратор

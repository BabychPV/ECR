# Набір скриптів вікна live (EcrLandUi) — як провести наступне вікно (W15v)

Записано «Аудитом-2» 14.11 після вікна W15u. Мета: щоб будь-яка інша сесія Claude могла провести наступне вікно без
передачі контексту. Самі скрипти лежать **поза git** (у них шляхи цієї машини), тут — де вони і що обов'язково.

## Де лежить
- Набір: `H:\ECR\.sync-local\ready\w15u-drafts\` (`run-w15u.ps1` — оркестратор; `README-W15u.md` — повний runbook
  з порядком контенту; `w15u-common.ps1` — усі ключі вікна).
- Звіти: `.sync-local\reports\W15u-rehearsal.md` (репетиції), `.sync-local\reports\W15u-window.md` (вікно W15u).
- Наступне вікно: скопіювати `w15u-drafts` → `w15v-drafts` із заміною `W15u`→`W15v`, `w15u`→`w15v`, `W15U`→`W15V`
  у вмісті та іменах файлів, потім попередні посилання `W15t/w15t` → `W15u/w15u` (як робилося W15t → W15u).

## Що змінити в `w15v-common.ps1`
- `LiveSha` = `a1ff819738a9283a8072cb898f53694bdf5adc27` (live після W15u), `PrevShort` = `a1ff81973`.
- `PrevBak` = `E:\EcrBackup\land-ui\w15u\bak\EcrLandUi-after-W15u-20261113-2356.bak` (після контенту),
  `PrevBakSha` = `007F52836DC81C46AEA69E2181C8F030150DA4A1C2891CE58944C9A31162E8C0`.
- `PreMigrations` = 117, `PrevLastMigration`/`LastMigration` = `20261113094154_PT01_ProjectTemplates`
  (якщо в діапазоні немає нових міграцій).
- `ExpectedUiStringBefore` = значення з live перед вікном (після W15u було 23223).
- `MustContain` += `a1ff819738a9283a8072cb898f53694bdf5adc27`.
- `K1BaselineFile` — новий замір на live безпосередньо перед GO: `k1-cells-w15v.ps1 -Measure -OutPrefix …`.
- Правила PT01 у блоці «PT01 ALTERS existing tables» прив'язані до наявності міграції PT01 у діапазоні — у W15v
  вони не спрацюють; для нової міграції, що ЗМІНЮЄ наявні таблиці, додати аналогічний блок (MigCols, NewCols/StampCols,
  MigIdxReplaced, MigFks, MigModules).

## ⛔ Обов'язково: прапорець HSE400 (B105)
- Live працює з `ECR_Calculations__Hse400CompositionFromDocument=true` (у збірці `appsettings.json` — `false`).
- Джерело — **постійний** файл `F:\ecr-backup\land-ui\live-env.json` (лише live, поза git і теками застосунку;
  білий список ключів, значення `true`/`false`). `restart-w15u.ps1` читає його при КОЖНОМУ старті live, зокрема при
  відкаті; скретч його не читає.
- Блок «B105: PERSISTENT live launch flags» у `restart-*.ps1` переносити в кожен новий набір. Перезапуск live — лише
  через `restart-*.ps1`; ручний `dotnet` старт прапорець загубить (Land втратить склад газу, повернуться FormulaErrors).
- Перевірка після старту: `launch-flags.json` → `hse400CompositionFromDocument: true` (після B105 від AN — ще й
  `/health/facts`).

## Порядок вікна (перевірено в W15u)
1. Репетиція на свіжому відновленні `PrevBak` у скретч-базу на E: (порти 5196/6196, `W15V_SCRATCH_DB`), спершу
   підняти попередній застосунок на скретчі (`restart -Target w15u`), потім `run-w15v.ps1 -Scratch -SkipRecalc -SkipK1Cells`.
2. GO від координатора → SHA вікна; перевірити повний CI на точному SHA і що діф із репетиційним кандидатом лише tests/docs.
3. Зупинити скретч-стенди; K1 re-baseline на live; збірка на SHA вікна (`build -Replace`).
4. Запуск через CPU-обгортку (2×30 с < 45 %); ⚠ обгортка з `*>` висне після прогону (успадковані дескриптори dotnet) —
   прогін завершено, коли в `runLive.txt` є `RESULT` і `k1-cells exit`; зупиняти лише PID самої обгортки.
5. Контент (якщо є): бекап `-Phase mid` → кроки за README → перерахунок doc 96 → перевірка FormulaError/A37 по клітинках
   (правило ретро 348) → бекап `-Phase after`.
6. Підсумок DONE координатору з таблицею postcheck; журнал — `.sync-local\reports\W15v-window.md`.

## Заборонено без окремого слова людини
RESTORE живої бази, видалення даних, `allowLockedDocuments`, `DBCC DROPCLEANBUFFERS`, відкриття закритого періоду.

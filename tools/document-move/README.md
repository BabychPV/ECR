# tools/document-move — зведення live в LANDUI2026 (b14, D-577, P-1125-ONEPROJ)

Разовий відрепетируваний скрипт (рішення людини 25.11): перенести документи **HSE400 98** і **HSE401 93** у
місячний проєкт **LANDUI2026** (там уже Land 96 і тестовий Land 100), зберегти значення, стани місяців і історію,
B80 (Land читає склад HSE400) — ті самі числа; потім прибрати HSE400_2026, HSE401_2026, TEST_COORD_2026, HSE400_GOLD
через UI. Дизайн: `.sync-local/DESIGN-document-move.md` rev 2 (+ огляд B1–B6, відповіді людини 25.11).

| Файл | Що робить |
|---|---|
| `Move-Document.ps1` | обгортка: `-Mode PreImage | Preview | Apply | Reverse`, запобіжники (бази `ECR`/`FLERT`/`EcrLegacyProd` — відмова; `EcrLandUi` — запис лише з `-AllowLive`; бекап цього вікна; причина ≥ 10 знаків; pre-image перед Apply) |
| `Move-Document.sql` | один документ, одна транзакція: замки → відмови → знімок → переключення `doc.Document` (+ порожній `TemplateId` = шаблон джерела) і ВСІХ `calc.CalculationRun` документа → Q-17 Apply цього документа за календарем ЦІЛІ (`@WriteLocked = 1`) → видалення невласних місячних копій лише за рівності з власником → перевірки → `aud.SecurityEvent DocumentMovedToProject` → звіт. **Preview завжди відкочується (у самому SQL)** |
| `Export-PreImage.sql` | бокова база `EcrMovePre_<CorrelationId>`: документи, екземпляри, рядки, комірки, стилі, усі прогони з результатами/кроками/входами/проблемами, помилки формул, reopen, погодження; `dbo.Manifest` (комірки, контрольна сума, прогони, результати) |
| `Reverse-Move.sql` | повертає документ як у pre-image (прогони з ОРИГІНАЛЬНИМИ Id, без W-5 прогонів і reopen, без нових екземплярів/рядків), звіряє з `Manifest`; лише поки проєкт-джерело існує |
| `Parity-Baseline.sql` | набір паритету R2-4 (лише читання): Land 96/100 результати + контрольна сума, issues, formula errors, HSE400 значення місяців за (рядок, місяць), F_, HSE401 комірки, один робочий HSE400 року, `projectMismatch`, черга, rpt `_m` |
| `DocumentMove.Lib.ps1` | складання тексту SQL (той самий текст ганяють `DocumentMoveSqlTests` і Pester `DocumentMove-Lib.Tests.ps1`) |

Відмови перенесення (`51810`, перелік кодів у повідомленні й результаті `MoveBlocker`): `jobsRunning`, `archived`,
`sameProject`, `otherYear`, `templateNotInTarget`, `versionMismatch`, `singleDocument`, `keyClash`, `periodMissing`,
`workflowKindMismatch`, `columnRenames`, `projectRuns`; після Q-17: `51811` конфлікти Q-17, `51812` невласна копія
≠ власника, `51813` значення місяця не у власнику, `51814` `projectMismatch`.

## Вікно (одне вікно стенда; продуктовий код b14 P1+P2 розгорнуто першим)

Усі кроки — координатор на машині стенда; **рядки з ⚠ потребують окремого слова людини** (запис у live).
`$cid` — один CorrelationId на все вікно, напр. `b14move-20261127`.

| Крок | Дія | Доказ / стоп |
|---|---|---|
| W-0 | RC з P1+P2 (зелений повний CI) → розгортання; міграція `B14TemplateYearEditWindow` вмикає вікно року для HSE401 | `/health` зелений; `SELECT YearEditWindow FROM cfg.Template WHERE Code='HSE401'` = 1 |
| W-1 ⚠ | HSE401 v14 у **HSE401_2026** (до перенесення): `tools/hse401/Update-Hse401Template.ps1 -Target Live -ProjectCode HSE401_2026` (diff `Formula`: 9 формул `[Period].Days` → довжина року; `WorkflowMode = None` уже з B75) → migrate doc 93 → тиша перерахунку → Compare REAL = 0 | `Parity-Baseline` рядки `hse401.cells.*` до/після W-1 рівні (у річному проєкті 365 = 365) |
| W-2 | зупинити `EcrWorker` (як `deploy-ecr.ps1`), оголосити тех. вікно; `jobs.queuedOrRunning = 0`; повний бекап; `sqlcmd … -i Parity-Baseline.sql -v Land=96 Land2=100 Hse400=98 Hse401=93 Year=2026 -o before.txt`; `.\Move-Document.ps1 -Database EcrLandUi -Mode PreImage -Documents 98,93 -CorrelationId $cid` | `before.txt`, `EcrMovePre_$cid.dbo.Manifest` |
| W-3 | `.\Move-Document.ps1 -Database EcrLandUi -Mode Preview -Documents 98,93 -TargetProject LANDUI2026 -CorrelationId $cid` | звіт `MoveReport` за ПРАВИЛАМИ (огляд B4): doc 98 — `Q17Conflicts 0`, `Q17CopiesWritten = NonOwnerCopiesRemoved = 1750` (175 × M02..M11), `NonOwnerCopiesLeft 0`, `OwnerMismatch 0`, `ProjectMismatch 0`, `RunsRekeyed ≥ 1`; doc 93 — `Q17CopiesWritten 0`, `ChecksumBefore = ChecksumAfter`; `RightsDelta` без `lose` (огляд M1) |
| W-4 ⚠ | `.\Move-Document.ps1 -Database EcrLandUi -Mode Apply -Documents 98,93 -TargetProject LANDUI2026 -CorrelationId $cid -Reason 'b14: one project LANDUI2026 (human 25.11)' -ConfirmBackup <bak W-2> -AllowLive` | звіт Apply = звіт Preview |
| W-5 ⚠ | запуск воркера; document Reopen doc 98 для 202601–202609 (причина «b14 move: recalc HSE400 by month»); перерахунок doc 98 **202602…202611, а 202601 ОСТАННІМ** (огляд B2: до цього F_ лишаються з прогону 10181); жодного перерахунку Land, поки 202601 doc 98 не актуальний; закрити reopen | `hse400.currentF` = W-2 (за (рядок, код виходу)) |
| W-6 | UI (SysAdmin): HSE400 березень і листопад — значення й F_; HSE401 відкривається на «Year 2026» (292 437 значень), правка комірки й повернення; `GET /documents/93/tables?periodKey=202611` — рядки якоря (B1); перерахунок doc 93 у LANDUI2026 — `hse401.cells.calculated` = W-1 (B5, N1) | — |
| W-7 | перерахунок Land 96 202610/202611; `Parity-Baseline … -o after.txt`; diff з `before.txt` | усі рядки рівні, крім позначених «changes by design»; `projectMismatch 0`; `hse400.workDocumentsOfYear = 98` |
| W-8 ⚠ | видалення TEST_COORD_2026, HSE400_GOLD через UI | — |
| W-9 | після першого нічного: повтор W-7 | рівні |
| W-10 ⚠ | deletion-preview HSE400_2026 і HSE401_2026 → «дозволено»; видалення через UI (людина 25.11: одразу у вікні, ПІСЛЯ W-7) | після цього відкат — лише RESTORE (окреме слово людини) |

**Стоп (R2-5):** будь-яке число Preview поза правилами W-3 → стоп до Apply. Після Apply будь-яка розбіжність
паритету, `projectMismatch > 0` чи збій W-6 → `Reverse` (до W-10), повторна звірка з `before.txt`.

## Reverse (до W-10)

```powershell
.\Move-Document.ps1 -Database EcrLandUi -Mode Reverse -ReversePreview -Documents 98 -CorrelationId $cid
.\Move-Document.ps1 -Database EcrLandUi -Mode Reverse -Documents 98,93 -CorrelationId $cid -ConfirmBackup <bak> -AllowLive
```

Потім (огляд B3): перерахунок Land 96/100 за 202610/202611 і `Parity-Baseline` = `before.txt` — витіснені після
перенесення прогони Land у pre-image не входять. Записи `aud.*` перенесення лишаються (журнал незмінний).

## Репетиція (НЕ виконано в lane — копія live на іншому ПК)

Двічі на копії live, знятій у день вікна (огляд B4: рівність із live — за правилами, не за числами):
W-1…W-7 повністю, далі один нічний прогін (W-9), далі `Reverse` після W-5-еквівалента (огляд B3) → `Parity-Baseline` =
`before.txt`; плюс повний перерахунок року Land 96 до/після на копії (Reopen 202601–202609) — diff 0 (M4 (3)).

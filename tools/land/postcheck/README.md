# tools/land/postcheck: пакет пост-перевірки стенда Land

Одним запуском перевіряє стенд ПІСЛЯ recalc/міграції/завантаження вікна: черга порожня -> SQL (лише `SELECT ... WITH (NOLOCK)`)
-> API (лише `GET` + вхід) -> необов'язкова ізоляція колонок вікна -> світлофор PASS/FAIL/INFO. Жодних записів у стенд.
Значень прода в пакеті немає: усі пороги — параметри; не задано — рядок `INFO` зі значенням.

| Файл | Призначення |
|---|---|
| `Run-PostCheck.ps1` | оркестратор і світлофор |
| `PostCheck.sql` | зріз бази, машинні рядки `PC\|ключ\|значення` |
| `ApiSmoke.ps1` | S-01..S-10, LP-45, скан журналу API (A-14), бекап (A-15, лише `-DoBackup`); рядки `PCAPI\|...` |
| `WindowExtra.sql` | розділ `-Window7Isolation`: ізоляція колонок документа/таблиці, Current методологій вікна; рядки `WX\|...` |

## Запуск

```powershell
# перевірка на копії (без API, без нових колонок): база обов'язкова, лише EcrLand*
powershell -NoProfile -ExecutionPolicy Bypass -File tools\land\postcheck\Run-PostCheck.ps1 `
  -Database <EcrLand-база> -Since 2026-10-14T05:00:00 -SkipApi -NoExpectNew

# після вікна: doc-scope, post-бекап з тегом, очікувана версія методології
... -Database <база> -Since 2026-10-15T12:00:00 -BaseUrl http://localhost:<порт> `
  -AccountsFile <файл> -User <користувач> -LogDir <каталог логів> `
  -Docs P1-V1-0010 -WindowTag w7 -DoBackup -BackupDir <каталог> `
  -ExpectedMethodologyVersions ECW_C11_10=V2
```

Код виходу: `0` — немає FAIL; `1` — є FAIL; `2` — черга не порожня (нічого не запущено); `3` — `sqlcmd` не відповів.

## Параметри, що замінили зашиті значення

| Було | Тепер |
|---|---|
| прогони 202606 «усі після since» | `-Period` (202606) + `-Docs` (BusinessKey/DocumentId; без параметра — усі документи) |
| літерал версії шаблону | `-ExpectedTemplateVersion`; без нього: проєкт на найновішій Published, усі документи на ній |
| зашите ім'я post-бекапу | `-BackupName` або `-WindowTag` -> `ecr-land-ui-post-<tag>-<yyyy-MM-dd>.bak`; існуючий файл не перезаписується (суфікс часу) |
| лог у зашитому каталозі | найновіший `api*.log` у `-LogDir`; або `-LogPath` |
| диски й акаунти | `-AccountsFile`, `-User`, `-BackupDir`, `-LogDir`, `-OutDir` (або `ECR_POSTCHECK_*`) |
| дефолт на живу базу стенда | `-Database` обов'язковий, лише `EcrLand*` |
| пороги 37862 / 5302 / 379813 / 546 / 91 / 3 / 27 / 16974 | `-BaselineResults`, `-BaselineResultsPeriod`, `-BaselineEntered`, `-BaselineErrorCells`, `-ExpectedDocuments`, `-ExpectedApprovals`, `-BaselineLp40Columns`, `-BaselineLp40NonEmpty`, `-BaselineMethodologies` |
| версії методологій K1 | `-ExpectedMethodologyVersions КОД=Vn` (кілька через кому) |
| `LAND_GENERIC_EQUIPSTATUS:Running`, 44 | `-ExpectedRowFilter`, `-ExpectedRowFilterCount`, `-ExpectedRowFilterNullCodes` |

Облікові дані читаються лише з `-AccountsFile` (рядки `користувач<TAB>пароль`), пароль не друкується. Адресу API перевіряє
`Connect-Ecr` (лише локальна машина). Результати (`-OutDir`, за замовчуванням тека `ecr-postcheck` у TEMP) містять лише
лічильники, коди й стани.

## -Window7Isolation

Необов'язковий розділ для вікна, що змінює один документ/таблицю (приклад — `T9_04`, документ `P1-V1-0010`): групи колонок
`G20`/`KgH`/`Kg`/`OT`/`KEY4`/`OTHER`, правки `aud.CellChange` по `Origin`, Current-результати методологій
`-WindowMethodologies CODE[=поріг]`, `TableInstanceId` у нових Result/Step/Input. Потрібні `-IsolationDocument`,
за потреби `-IsolationTable`, `-IsolationPeriodFrom/To`, `-IsolationBaselineRows`.

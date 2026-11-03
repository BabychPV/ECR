# Типи вузлів дерева НАЕ (lane `integ-nae-node-types`, 03.11)

Серверна частина: домен, міграція, контракт. Клієнт (діалог вузла за типом) — окрема сесія над цим контрактом.

## Рішення людини (03.11, людина, квіз) — дослівно за змістом

Людина відхилила загальний діалог «New node» для `NAE_TREE`. Модель PI AF `ECR_01_Air` типізована шаблонами AF.

- **Тип вузла = рівень реєстру.** Шаблон AF → рівень: `Air_Region` → `REGION`, `Air_OnShoreOffShore` → `AREA`,
  `Air_Area` → `SITE`, `Air_Unit` → `EQUIP`, `Equipment` → `SRC`.
- **Атрибути налаштовуються ПО ТИПУ вузла:** поле належить рівню (`null` — усім рівням); діалог показує лише поля свого типу.
- **Q1:** кожен вузол має назви En/Ru/Kz; **En обов'язкова, Ru/Kz — ні**.
- **Q2:** **`Air_Area` (SITE) може бути вкладений в `Air_Area` (SITE)**.
- **Q3:** номер НАЕ (`NaeCode`) **обов'язковий на SRC і унікальний у межах (предок REGION, предок AREA)**; де регіон не ділиться
  на On/Offshore (немає предка AREA, напр. Mangistau) — **унікальний у межах REGION**. Перевіряє сервер, НОВИЙ код помилки.
- `RegionCode` / `AreaCode` — **похідні від предків** (не ввід користувача, лише читання). `AfPath` — лише читання.
- Імпорт з AF зіставляє **за ІМЕНЕМ ШАБЛОНУ**, а не за глибиною.

## Модель

| Що | Де | Як |
|---|---|---|
| Поле → рівень | `cfg.RegistryFieldDef.LevelId int NULL`, FK `FK_RegField_Level` → `cfg.RegistryLevelDef` (NO ACTION), індекс `IX_RegistryFieldDef_Level` | `null` — поле всіх рівнів. Обов'язковість прив'язаного поля діє лише для записів його рівня; запис без рівня (пакет до розміщення) прив'язаних полів не вимагає. Непорожнє значення поля чужого рівня — `422 fieldLevelMismatch` |
| Вкладення рівня в себе | `cfg.RegistryLevelDef.AllowsSelfNesting bit NOT NULL DEFAULT 0` (`DF_RegLevel_SelfNest`) | Вузол такого рівня може стояти ПРЯМО під вузлом того самого рівня. Інші правила (ординал строго росте, пропущені рівні — необов'язкові) без змін. Для НАЕ прапорець має лише `SITE` |
| Міграція | `20261103084111_NAE2_NodeTypes` | одна; без temporal/`ADD PERIOD` |

Рівень, який тримає прив'язані поля, не прибирається збереженням рівнів: `409 ECR-REG-0409 levelHasFields`.

### Поля дерева НАЕ за типом

| Поле | Рівень | Обов'язкове | Хто пише |
|---|---|---|---|
| `NaeCode` | SRC | так (лише SRC) | користувач / завантаження; нормалізується (`NaeCodeNormalizer`: `36_pod` → `0036_pod`) |
| `RegionCode` | SRC | — | **сервер** з предка REGION (код без `R_`), ввід ігнорується |
| `AreaCode` | SRC | — | **сервер** з предка AREA (код без `A_<REGION>_`), без AREA — `-`; ввід ігнорується |
| `AfPath` | усі | — | лише завантаження з AF; ввід ігнорується |
| `SourceType`, `PointName` | SRC | — | користувач / імпорт дозволів |

Реєстр дерева НАЕ розпізнається за формою (`NaeTreeNodePolicy.IsNaeTree`: ієрархічний, рядкові поля `NaeCode`,
`RegionCode`, `AreaCode`), а не за налаштованим кодом: завантаження з AF може писати будь-який код.

## Правила ручного шляху (`POST /registries/{code}/entries`, `POST …/entries/{id}/move`)

1. **En-назва обов'язкова** на кожному вузлі дерева НАЕ — `422 ECR-REG-0422 naeNameEnRequired` (деталь `levelCode`).
2. **`NaeCode` на SRC** — обов'язковий (`422 requiredFieldsMissing`) і вільний у своїй області — `409 ECR-REG-4094 naeCodeTaken`
   (деталі `naeCode`, `regionCode`, `areaCode`, `entryId`, `entryCode` тримача). Перевірка йде в транзакції запису ПІСЛЯ
   блокування дерева (`LockTreeAsync`), тож два одночасні створення одного номера обидва не пройдуть. Вузли з закритим вікном
   (`ValidTo` ≤ сьогодні) і вимкнені номер не тримають.
3. **Похідні поля** `RegionCode`/`AreaCode` сервер ставить за новими предками на кожен upsert SRC; `AfPath` із запиту
   відкидається.
4. **Перенос** (move або upsert з іншим батьком), що змінив би REGION/AREA будь-якого SRC у піддереві, — `422 ECR-REG-0422
   naeMoveChangesScope` (деталі `entryCode`, `sourceCode`): REGION+AREA — частина бізнес-ідентичності НАЕ, на яку дивляться
   дозволи. Перенос у межах тієї самої області — дозволено.

## Імпорт з AF за шаблоном

`NaeAfTreeBuilder.Build` передає план `NaeAfTemplateTreeBuilder`, щойно хоч один елемент знімка має шаблон
(`NaeAfElement.Template`). Батько типізованого елемента — найближчий типізований предок за шляхом; елементи без шаблону
(теки, корінь) прозорі. Невідомий шаблон — `skipped unknownTemplate`; місце не за правилами рівнів — `skipped levelOrder`;
усе типізоване під пропущеним — `skipped parentSkipped`. REGION/AREA джерела — з предків (як на ручному шляху). Технічні коди —
ті самі, що в завантаженні за шляхом (`R_…`, `A_…`, `S_…`, `G_…`, `N_…`); вкладений SITE — `S_<R>_<A>_<БАТЬКО>_<САЙТ>`.
Знімок без шаблонів іде старим шляхом (атрибути `Region`/`On_Offshore` і сегменти шляху) — без змін.

⚠ [blocks] `PiAfNaeReader` поки НЕ заповнює `Template`: порт `SourceElement` (`IExternalDataSource.DiscoverElementsAsync`)
шаблону елемента не несе, а порт і адаптери PI AF — поза межами цієї lane. Доки адаптер не віддасть `TemplateName`, живе
завантаження йде шляхом за шляхом; логіка за шаблоном покрита тестами з фейковим читачем.

## Оновлення наявної бази

На старті (`AirTypeStartupStep` → `NaeTreeProvisioner.EnsureAsSystemAsync`) наявне дерево НАЕ, створене до типів вузлів,
отримує один раз: прив'язку полів за таблицею вище, `NaeCode` обов'язковий (лише SRC), `SITE.AllowsSelfNesting = 1`, `+1`
до `DefinitionVersion`, автор — система (0). Дерево, де хоч одне поле НАЕ вже прив'язане або хоч один рівень уже вкладається
в себе, не чіпається (оновлене раніше або налаштоване адміністратором) — другий старт нічого не змінює. Нове дерево
провізіонер створює одразу з прив'язками.

## Контракт (для клієнтської сесії)

- `RegistryFieldDto` (+`GET /registries/{code}/definition`): `levelId?`, `levelCode?` (`null` — усі рівні), `isReadOnly`
  (сервер веде значення; у дереві НАЕ — `RegionCode`, `AreaCode`, `AfPath`).
- `RegistryFieldSaveDto` (PUT definition / draft): `levelCode?` — код рівня прив'язує, `""` — усі рівні, `null` — без змін
  (старий клієнт прив'язку не знімає); невідомий код — `422 levelUnknown`.
- `RegistryLevelDto` (`GET …/hierarchy`, `GET …/tree`): `allowsSelfNesting`. `RegistryLevelInput` (PUT hierarchy):
  `allowsSelfNesting?` — `null` без змін (нового рівня — `false`).
- Створення/зміна вузла — без нового ендпоінта: `POST /registries/{code}/entries` (`levelCode`, `parentEntryId`, `display`,
  `values` — лише поля свого рівня; read-only поля можна не слати, їх ігнорують).
- Нові ключі каталогу: `err.ECR-REG-4094`, `err.ECR-REG-4094.naeCodeTaken`, `err.ECR-REG-0422.naeNameEnRequired`,
  `err.ECR-REG-0422.naeMoveChangesScope`, `err.ECR-REG-0422.fieldLevelMismatch`, `err.ECR-REG-0409.levelHasFields`
  (`seed/features/NAE-node-types.sql`, en/ru/kz).

## Припущення

- Похідний `RegionCode` = технічний код предка REGION без префікса `R_` (так його дають усі три завантаження), інакше — сам
  код; так значення збігаються з уже збереженими (`ATYRAU`, `OFFSHORE`, `-`).
- Зміна рівня вузла переносом у SRC/з SRC (`levelCode` у move) номер НАЕ не перевіряє — [debt].

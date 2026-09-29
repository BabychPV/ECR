# FEATURE-REGISTRY-SYNC — синхронізація довідників із PI AF

| | |
|---|---|
| Дата | 2026-09-28 |
| Статус | **Проєкт до виконання; S3 зроблено, S4 — у цій гілці.** Дизайн погоджено координатором сесій 2026-09-28. Рішення «зовнішній ключ чи поле» — `D-202` у `docs/tz/10-decisions.md` |
| Вимоги | `ФВ-8.9` (хто master), `ФВ-8.10` (зовнішні ідентифікатори), `ФВ-8.11` (синхронізація — конфігурація, не код) |
| Спирається на | `D-44` (запису в AF немає), `D-49` (подвійна звірка), `D-118` (ручна правка не перетирається), `D-173` (одиниці на межі), `D-187` (`Missing` лише після повного читання), `D-198` (`LEGACY_ID` — поле й альтернативний ключ) |
| Суміжне | [FEATURE-REGISTRY-TABLES](FEATURE-REGISTRY-TABLES.md) (ключі, `RegistryEntryWriter`); [FEATURE-HSE301-VIEW](FEATURE-HSE301-VIEW.md) §4.7.4 (`SourceEventSyncJob` — та сама політика для подій) |

Позначки: ⛔ — межа або заборона; ⚠ — застереження; `S-n` — крок плану (§5); `RSQ-n` — питання
замовнику (§7).

---

## 1. Коротко

Довідник, у якого master — PI AF (`SourceKind = External` або `Hybrid`), наповнюється окремою
фоновою задачею **`RegistrySyncJob`**. Задача читає елементи AF, зіставляє їх із записами довідника
через **`dic.RegistryExternalKey`**, будує план чистою функцією **`RegistrySyncPlanner`** і пише
лише через спільний **`RegistryEntryWriter`** від імені службового користувача **`svc-integration`**.
Нічого не видаляється й нічого не створюється автоматично: зниклий і новий елементи — події.

⛔ Запису в AF немає (`D-44`): синхронізація однонапрямна.

## 2. Модель

### 2.1 Зв'язок «елемент AF → запис довідника»

`dic.RegistryExternalKey` (`src/Ecr.Domain/Entities/Dictionaries/RegistryExternalKey.cs`):
`RegistryEntryId`, `DataSourceId`, `ExternalId` (WebId/GUID), `ExternalPath` (шлях AF — змінюється
незалежно від GUID), `LastSyncedAt`; `MarkSynced(path, utcNow)` фіксує прогін. Унікальність —
`(DataSourceId, ExternalId)`.

### 2.2 Зовнішній ключ чи поле — `D-202`

| Що це | Куди | Приклад |
|---|---|---|
| **Технічний дескриптор** зовнішньої системи, прив'язаний до `DataSource`: WebId, GUID, шлях AF | `dic.RegistryExternalKey` | GUID елемента факела в AF |
| **Бізнес-ідентифікатор**, який бачать формули, імпорт, люди | поле довідника + альтернативний ключ | `STREAM.LEGACY_ID` (`D-198`) |

Причина: дескриптор належить парі «запис × джерело» і змінюється разом із джерелом (перенесли
елемент, замінили сервер AF), а формулам його знати не треба; бізнес-ідентифікатор — властивість
самого запису, і `REGFIND` мусить знаходити запис за ним без знання про джерело.

### 2.3 Мапінг

`ext.EntityFieldMap` з ціллю «поле довідника» (`FieldTargetKind.RegistryField`,
`IRegistryStore.ListFieldMappingsAsync` → `RegistryFieldMapping`, `IsActive`). Мапінг — конфігурація,
не код (`ФВ-8.11`).

## 3. Політика

| `SourceKind` | Що пише синк | Розбіжність без права запису |
|---|---|---|
| `External` | усі поля з **активним** мапінгом | поле з **вимкненим** мапінгом → `RegistryDiverged` |
| `Hybrid` | лише поля з **активним** мапінгом | решта полів — локальні, синк про них мовчить |
| `Local` | нічого | кожне змаплене поле, що відрізняється → `RegistryDiverged` (звірка, `D-49`) |

⚠ **Судження S4 (у межах погодженого):** координатор сформулював «External — синк володіє
змапленими полями, Hybrid — лише полями з активним мапінгом». Писати в `External` через мапінг, який
адміністратор свідомо вимкнув, означало б перетерти його рішення, тому таке поле не пишеться, але
розбіжність показується подією. Саме це і відрізняє `External` від `Hybrid` у планувальнику.

⚠ **Хто прив'язує** ([`D-202`, доповнення 2026-09-29](../tz/10-decisions.md#119-рішення-від-2026-09-28-синхронізація-довідників-із-pi-af) —
судження розробки, на підтвердження людиною): синк пише лише в довідник, прив'язаний до
`SourceEntity`; прив'язує той, хто має право редагувати дані цього довідника —
`PUT /api/v1/sources/{id}/registry` вимагає `Integration.Manage` і `Registry.EditData` або грант
`Write` на довідник (відв'язка — на поточний), інакше `403`.

⚠ **Хто заводить зовнішній ідентифікатор запису (S2, судження розробки):** право на ДАНІ
довідника — `Registry.EditData` або грант `Write` на довідник зі шляху, **без**
`Integration.Manage`. Зв'язок `RegistryExternalKey` — властивість запису, як значення його
полів; делегування запису синку — це прив'язка `SourceEntity` вище, і там `Integration.Manage`
уже вимагається. Перелік — `Registry.View` або грант `Read`.

Події:

| Подія | Коли | Що з записом |
|---|---|---|
| `RegistryDiverged` | див. таблицю вище | нічого |
| `RegistryConflictKeptManual` | джерело змінило поле, яке останньою правила людина (`D-118`) | лишається людське |
| `RegistrySourceMissing` | прив'язаного елемента немає в **повному** знімку (`D-187`) | не видаляється |
| `RegistryElementUnlinked` | елемент AF без `RegistryExternalKey` | не створюється (прив'язує людина, `S10`) |
| `RegistryValueRejected` | значення не приводиться до типу поля (`ECR-REG-0422`) або не проходить ключ/правило у writer | нічого |

Ідемпотентність: те саме типізоване значення (`12.5` = `"12.50"` після приведення) — нічого не
пишеться, подій немає, план порожній. Атрибута немає у знімку — поле не чіпається (збій читання не
видається за «порожньо»); атрибут є зі значенням `null` — поле очищається.

⚠ Одиниці приводить задача на межі (`BoundaryUnitConversion`, `D-173`; станом на 2026-09-28 у коді
ще немає) **до** планувальника.

## 4. Компоненти

| Компонент | Шар | Відповідальність |
|---|---|---|
| `IExternalDataSource.ReadCurrentAsync` (S1) | порт (`src/Ecr.Application/Ports/IExternalDataSource.cs`) | поточні значення атрибутів елементів шаблону/гілки AF |
| `RegistrySyncPlanner` (S4) | `Ecr.Application/Integration/RegistrySync` | чиста функція: вхід → план |
| `RegistrySyncJob` (S5, S7) | `Ecr.Application` + планувальник `D-09` | читає, планує, пише через writer, фіксує `MarkSynced` |
| `RegistryEntryWriter` (S6) | `Ecr.Application/Registries` | єдина точка запису `dic.RegistryEntry`/`RegistryValue`: типи, ключі, правила, ревізія, аудит |
| Панель зовнішніх ідентифікаторів (S10) | `Ecr.Web` | зв'язки, події, ручна прив'язка |

### 4.1 API планувальника (S4)

```csharp
RegistrySyncPlan RegistrySyncPlanner.Plan(RegistrySyncInput input);

RegistrySyncInput(RegistryDefId, SourceKind, IsCompleteSnapshot,
    Elements: RegistrySyncSourceElement(ExternalId, ExternalPath, Attributes[атрибут → значення]),
    Links:    RegistrySyncLink(ExternalId, RegistryEntryId, ExternalPath),
    Entries:  RegistrySyncEntryState(RegistryEntryId, Values[FieldDefId → (Value, LastWriterIsHuman)]),
    Mappings: RegistrySyncFieldMapping(RegistryFieldDefId, FieldCode, DataType, UnitId, SourceAttribute, IsActive))

RegistrySyncPlan(Updates: (EntryId, FieldDefId, FieldCode, Old, New),
                 PathChanges: (ExternalId, EntryId, OldPath, NewPath),
                 Events: (Kind, ExternalId, EntryId, FieldCode, Current, Source, ErrorCode, MessageKey))
```

Приведення типу — тим самим механізмом, що ручний запис і імпорт: `CellValueReader.Normalize` +
`RegistryValue.Set`. ⚠ Проєкція типізованого значення (6 рядків) повторює приватний
`UpsertRegistryEntryHandler.RawValue`; винести її в `RegistryValue` — разом із S6/S7 (файл — зона
HSE301).

## 5. Кроки

| Крок | Що | Залежить від | Стан |
|---|---|---|---|
| **S0** | цей документ + `D-202` | — | ✓ 2026-09-28 |
| **S1** | порт `ReadCurrentAsync` (поточні значення атрибутів елементів AF) | вікно HSE301 `F4` (той самий файл порту) | ☐ після `F4` |
| **S2** | API зовнішніх ключів: перелік/прив'язка/відв'язка `dic.RegistryExternalKey` | — | ✓ `lane/analiz/registry-sync-s2` (див. §5.3) |
| **S3** | створення `SourceEntity` з вебу | — | ✓ `lane/analiz/sources-create` `56cb4066` |
| **S4** | `RegistrySyncPlanner` — чиста логіка + тести | S0 | ✓ у гілці `lane/analiz/registry-sync-s0-s4` |
| **S5** | `RegistrySyncJob` — **лише звірка**: читає, планує, пише події, нічого в довідник | S1, S4, планувальник `D-09` | ☐ |
| **S6** | `RegistryEntryWriter` — спільна точка запису | — | ◐ HSE301 `lane/hse301/s6-entry-writer` `c1320e94`, ще не в `dev/integration` |
| **S7** | синк пише через writer від `svc-integration`; у writer — режим «лише оновлювати» й адресація за `RegistryEntryId` (адитивно, з попередженням HSE301) | S5, S6 | ◐ S7a `lane/analiz/registry-sync-s7` (див. §5.1); адміністративна дія «прийняти значення джерела» — S7b |
| **S8** | блок ручних правок змаплених полів у `External`/`Hybrid` (явна дія «Виправити вручну», як `D-191`) | S7 | ☐ |
| **S9** | зіставлення неприв'язаних елементів за бізнес-ключем (альтернативний ключ довідника) — пропозиція людині, не автоприв'язка | S2, S5 | ☐ |
| **S10** | панель зовнішніх ідентифікаторів: зв'язки, події, ручна прив'язка | S2, S5 | ☐ |
| **S11** | (опц.) міграція наявних GUID із `Configuration!J3` та подібних у `RegistryExternalKey` | S2 | ☐ опційно |

### 5.1 S7a — синк застосовує оновлення (2026-09-28)

- **Writer (адитивно, HSE301 попереджено):** `RegistryEntryWriteBatch.UpdateOnly` (init, за
  замовчуванням `false`) — немає запису з кодом → помилка рядка `err.ECR-REG-0404.registryEntry`,
  не створення; `RegistryEntryWriter.UpdateAsync(RegistryEntryUpdateBatch(RegistryDefId,
  [RegistryEntryUpdate(RegistryEntryId, Values)]))` — лише оновлення за Id (запису немає, видалений
  або з іншого довідника → та сама помилка рядка). Ядро одне з `WriteAsync`; поведінка за
  замовчуванням незмінна. ⚠ Записи за Id читаються поштучно (`FindEntryAsync`) — пакетного читання
  за Id у `IRegistryStore` немає (борг).
- **Задача:** `External`/`Hybrid` — `plan.Updates` через `UpdateAsync` пакетом на довідник, в
  окремому DI-scope від `svc-integration`; `PathChanges` → `RegistryExternalKey.MarkSynced(newPath)`.
  `Local` — лише звірка, зміна шляху — подія `RegistryPendingUpdate`. Для застосованих оновлень
  `RegistryPendingUpdate` більше не пишеться.
- **«Все або нічого» (рішення S7):** відмова рядків → ці записи подією `RegistryValueRejected`, решта
  пакета — одним повтором; відмова на весь пакет (`ECR-REG-4092`) або невдалий повтор → решта
  поштучно, кожен у власному scope.
- **Невідомий автор (`ChangedByUserId = null`) = людина** (`D-118`, дефолт координатора): значення
  лишається, подія `RegistryConflictKeptManual`. Виправлено в задачі (S5-код, `IsHuman`), не в
  планувальнику; у S5 стояло протилежне.
- **Дедуп подій:** у `Details` — `key=<предмет>:<значення>` (хеші; предмет — статус + елемент + запис
  + поле, значення — значення джерела + код/ключ відмови, без значення в ECR). Подія не пишеться,
  якщо ОСТАННЯ подія того самого предмета сутності має те саме значення; один запит на прогін. У
  матеріалізації комірок (`ConflictKeptManual`) дедупу немає — перевикористати не було чого.
  ⚠ Обмеження: подія без значення (зниклий/неприв'язаний елемент), що зникла й повернулась, повторно
  не пишеться — ознаки «розв'язано» журнал не має.
- Тести: `RegistrySyncApplyTests` (SQL, справжній контейнер), `RegistryEntryWriterModeTests`.

### 5.2 S7-3 — ключ довідника в пакеті синку, межа довідника (2026-09-29)

- **Дубль ключа в пакеті:** `RegistryKeyService.ApplyAsync` записи пакета між собою свідомо не
  звіряє (тримачі з пакета виключено з перевірки проти бази). Доти нетемпоральний дубль (два
  елементи дають те саме значення поля ключа) доходив до `UX_RegistryEntryKey_Live` і
  `ConcurrencyConflictException` валила весь прогін; темпоральний із різним `ValidFrom` і вікнами,
  що перетинаються, записувався як справжній дубль. Тепер `RegistryEntryWriter` (і `WriteAsync`, і
  `UpdateAsync`) після застосування значень звіряє ключі пакета — дубль є помилкою ОБОХ рядків
  `err.ECR-REG-4092.keyDuplicateInBatch` (FEATURE-REGISTRY-TABLES §4.3 крок 3), у синку — подія
  `RegistryValueRejected` на кожен. Звірка — спільна з імпортом CSV
  (`Registries/Keys/RegistryBatchKeys`); вікна, що не перетинаються, законні (та сама умова
  `RegistryKeyService.Overlaps`, вимога HSE301).
- **Відмова індексу — не падіння прогону:** `TryWriteAsync` ловить і
  `ConcurrencyConflictException` (`keyTakenConcurrently`) → поштучний повтор. Без гонки це буває,
  коли записи пакета обмінюються ключами (E1: k1→k2, E2: k2→k1): SQL Server перевіряє індекс на
  кожну інструкцію. Поштучно кожен бачить тримача поза пакетом → `keyTaken`, обидва відхилено.
  ⚠ Решта `EcrException` (немає автора, немає довідника) — збій прогону, не ковтається.
- **Межа довідника (C3):** `LinksAsync` бере лише зв'язки записів ЦЬОГО довідника; запис іншого
  довідника з ключем того самого джерела синк не змінює (значення, ревізія, шлях ключа, аудит) і
  подій на нього не пише — тепер це тримає SQL-тест.
- Мутації (2026-09-29, кожна окремо, відкат і контрольний прогін): прибрати звірку пакета →
  червоні обидва тести writer'а на моках і SQL-тести дубля (нетемпорального й темпорального);
  звіряти без урахування вікон → червоний тест «вікна не перетинаються»; прибрати
  `ConcurrencyConflictException` із `catch` → червоний тест обміну ключами (виняток із прогону);
  прибрати `entry.RegistryDefId == registryDefId` у `LinksAsync` → червоний тест межі A/B (шлях
  ключа XB переписано).

### 5.3 S2 — API зовнішніх ключів (2026-09-29)

- `GET /api/v1/registries/{code}/external-keys?entryId=&dataSourceId=&cursor=&limit=` —
  `PagedResult<RegistryExternalKeyView>` за зростанням `id`, сторінка 1..200 (`0` — 50).
- `POST /api/v1/registries/{code}/external-keys` `{ entryId, dataSourceId, externalId }` → `201`.
  Запис видалений, відсутній або з іншого довідника — `404 err.ECR-REG-0404.registryEntry`;
  джерела немає — `404 err.ECR-INT-0404.dataSource`; `externalId` порожній чи > 200 —
  `422 err.ECR-REQ-0422.externalKeyInvalid`; пара `(DataSourceId, ExternalId)` уже зайнята —
  `409 ECR-REG-0409` (`externalKeyTaken`, гонка — `externalKeyTakenConcurrently` з
  `UQ_RegistryExternalKey`).
- `DELETE /api/v1/registries/{code}/external-keys/{id}` → `204`; зв'язок запису іншого довідника —
  `404 err.ECR-REG-0404.externalKey`.
- Право — §3 («Хто заводить зовнішній ідентифікатор»). Аудит — `aud.StructureChange`
  (`EntityType = dic.RegistryExternalKey`, `Bind`/`Unbind`) у транзакції зі зміною.
- `ExternalPath` через API не задається: його ставить синк (`MarkSynced`).
- Клієнт: панель «External identifiers» у формі наявного запису (`RegistryExternalKeysPanel`).
- Тести: `RegistryExternalKeyHandlersTests` (Application), `RegistryExternalKeysApiTests` (HTTP +
  гонка в сховищі), `RegistryExternalKeysPanel.test.tsx`. Мутації (2026-09-29, кожна окремо,
  відкат і контрольний прогін): прибрати перевірку права → червоні; прибрати звірку «запис
  належить довіднику шляху» → червоний; прибрати перевірку дубля → червоний.

### 5.4 D-212 PR-1 — перелік елементів для знімка (2026-09-29)

- **Дефект:** знімок брав елементи з каталогу конфігуратора (`ISourceCatalogReader.BrowseAsync`) і
  лишав `DataType == "Element"`. Каталог PI SQL Client (`DiscoverAsync`) — рядки-АТРИБУТИ
  (`Code = елемент|атрибут`, `EntityPath` = ім'я елемента, `DataType` = `ValueType`), тож для RTQP
  знімок був порожнім і «повним» → хибний `RegistrySourceMissing` на всі зв'язки. До того ж адреса
  читання будувалась зі шляху, а RTQP шукає елемент за ІМ'ЯМ (`WHERE e.Name = ?`).
- **Порт:** `IExternalDataSource.DiscoverElementsAsync(dataSourceId, root)` →
  `SourceElementsResult(Elements: SourceElement(ExternalId, Name, Path?, ReadAddress), IsComplete)`.
  Типова реалізація — відмова `ECR-INT-0422` `.queryKindNotSupported` (`queryKind = ElementList`).
  Задача читає атрибути за `ReadAddress|атрибут`; `ISourceCatalogReader` у задачі більше немає.
- **PI Web API:** прямі діти кореня (`BrowseAsync`), `ReadAddress` = шлях; повний — менше ніж
  `MaxItemsPerLevel` і кожен має `Id` і `Path`.
- **PI SQL Client — ключ `PiSqlClient:ElementListQuery`, типового тексту НЕМАЄ** (V-3, як
  `CurrentValueQuery`): немає ключа → `ECR-INT-0422` `.queryKindNotConfigured` ДО з'єднання.
  Контракт тексту: один параметр `?` — корінь (`EntityPath` сутності, інакше її код); колонки
  `ElementId` (GUID), `ElementName`, необов'язкова `ElementPath`. `ReadAddress` = `ElementName`.
  Повний — менше ніж `MaxCatalogRows` (20 000) рядків і кожен має `ElementId` та `ElementName`.
  ⚠ Ключа на рівні джерела (`PiSqlClient:{code}:…`) у коді немає — ключ спільний для всіх
  RTQP-джерел, як і решта `PiSqlClient:*`.
- **Запобіжник (обидва транспорти):** 0 елементів при наявних зв'язках → знімок НЕповний, жодного
  `RegistrySourceMissing`: порожню відповідь не відрізнити від хибного кореня чи тексту запиту.
  Відмова переліку — виняток адаптера: прогін `Failed` з його кодом і `messageKey`, подій не пише.
- `Value()` у PI SQL Client: дата — ISO 8601 (`"O"`), не `ToString()` за культурою потоку.

## 6. Журнал покриття

| Вимога | Що покриває | Стан |
|---|---|---|
| `ФВ-8.10` | `RegistryExternalKey` (сутність і тести `RegistryExternalKeyTests`); планувальник: зниклий/неприв'язаний/перенесений елемент (`RegistrySyncPlannerTests`, S4) | API — S2 (`RegistryExternalKeyHandlersTests`, `RegistryExternalKeysApiTests`); повна панель — S10 |
| `ФВ-8.11` | мапінг `ext.EntityFieldMap` на поле довідника (`CreateEntityFieldMapTests`); політика `SourceKind`, ідемпотентність, ручна правка, відмова типу (`RegistrySyncPlannerTests`, S4); звірка (`RegistrySyncJobTests`, S5); запис через writer від `svc-integration`, повтор без змін, невідомий автор, відмова одного запису, `Local` без запису (`RegistrySyncApplyTests`, S7a); дубль ключа в пакеті, обмін ключами, темпоральні вікна, межа довідника (`RegistrySyncApplyTests`, S7-3) | покрито частково: S7b, S8 |
| `ФВ-8.9` | політика `SourceKind` у планувальнику | логіка є; перемикання й подвійна звірка в UI — поза цим планом |

## 7. Питання замовнику

Поставлені людині координатором 2026-09-28 (10 питань). Формулювання тут — з боку дизайну; до
відповіді діють дефолти праворуч, кожен замінюється без переробки.

| # | Питання | Дефолт до відповіді |
|---|---|---|
| RSQ-1 | Які довідники мають master у AF (`External`/`Hybrid`)? | жоден не перемикається автоматично; `Local` + звірка |
| RSQ-2 | Шаблони/гілки AF, з яких беруться елементи кожного довідника | задаються на `SourceEntity` (S3) |
| RSQ-3 | Ідентифікатор елемента: WebId чи GUID; стабільний при перенесенні? | зберігаємо обидва — `ExternalId` + `ExternalPath` |
| RSQ-4 | Частота синхронізації | розклад `ext.CollectionSchedule`, раз на добу |
| RSQ-5 | Ручна правка змапленого поля: дозволена? | людина лишається (`D-118`), подія `RegistryConflictKeptManual` |
| RSQ-6 | Зниклий в AF елемент: що з записом? | лише подія `RegistrySourceMissing`, запис чинний |
| RSQ-7 | Новий елемент в AF: створювати запис автоматично? | ні — подія `RegistryElementUnlinked`, прив'язує людина |
| RSQ-8 | Бізнес-ключ для первинного зіставлення (тег, код, `LEGACY_ID`?) | альтернативний ключ довідника, пропозиція — не автоприв'язка (S9) |
| RSQ-9 | Хто отримує сповіщення про події синку | ті, хто має `Integration.Manage` (як `J-4`) |
| RSQ-10 | Чи переносити наявні GUID із `Configuration!J3` (S11) | ні, доки не підтверджено |

## 8. Ризики

- ⚠ **Тиша замість помилки.** Неповний знімок без позначки `IsCompleteSnapshot = false` дав би
  хибні `RegistrySourceMissing` на весь довідник. Позначку ставить читач (S1/S5) — лише після
  повного прочитання всіх сторінок.
- ⚠ **Ознака «останній автор — людина»** — це `RegistryValue` + автор ревізії (RT-04). Якщо writer
  S7 не проставить `svc-integration` послідовно, перший же ручний запис «приклеїть» поле назавжди.
  Тест S7 має довести обидва напрямки.
- ⚠ **Hybrid ↔ External.** Перемикання `SourceKind` у відкритому періоді заборонене (`ECR-REG-0422`,
  `ФВ-8.9`) — планувальник цього не перевіряє, він лише читає поточне значення.

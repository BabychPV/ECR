# Тестові сценарії нових екранів і можливостей (29.09–01.10.2026)

Доповнення до [TESTER-GUIDE.md](TESTER-GUIDE.md), розділ 6. Охоплює те, що
додано 29.09–01.10.2026 і ще не описано там окремими сценаріями.

**База.** Сценарії складено **за кодом** гілки `origin/dev/integration` на
вершині `6fcd539e` (30.09.2026 20:16 UTC) і за незлитими гілками
`lane/cloud/*`, названими в тексті. Вручну на цій збірці вони не пройдені.
Назви екранів і кнопок — з англомовного каталогу (`en`, файл
`src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql`); у `ru`/`kz` підписи інші.
Якщо крок не збігається з екраном — це або неточність сценарію, або дефект:
опишіть у заявці (TESTER-GUIDE, розділ 8).

**Позначки статусу** — як у TESTER-GUIDE п. 6.1, плюс одна нова:

| Знак | Значення |
|---|---|
| ✅ | код і тести є; очікуваний результат описано за кодом |
| 🟨 | працює з відомим обмеженням — воно назване в сценарії й **не є** новим дефектом |
| ⛔ | неможливо в цій збірці — не витрачайте час |
| 🔀 | **доступно після зведення** незлитої гілки `lane/cloud/…` у `dev/integration`; на поточній збірці крок не виконується |

**Формат сценарію.** Передумова й дані → права → кроки → очікується →
типові помилки й коди → вимоги. Коди помилок наведено як
`HTTP код-помилки messageKey`; повний ключ тексту — `err.<код>.<messageKey>`.

**Права за замовчуванням.** Ролі-шаблони й небезпечні права — див.
TESTER-GUIDE п. 6.1. Нижче в кожному сценарії названо саме **права** (не ролі),
бо небезпечні (`Integration.Manage`, `System.RunJob` тощо) видаються лише явно.

## Зміст

- [Н-А. Події з PI і прив'язки PI за вікном рядка](#н-а-події-з-pi-і-привязки-pi-за-вікном-рядка)
- [Н-Б. Конвеєр даних `/admin/pipeline`](#н-б-конвеєр-даних-adminpipeline)
- [Н-В. Конструктор і дані довідників (8.12, 8.14, 8.16)](#н-в-конструктор-і-дані-довідників-812-814-816)
- [Н-Г. Історія запису й експорт довідника (RT-15, RT-16)](#н-г-історія-запису-й-експорт-довідника-rt-15-rt-16)
- [Н-Д. Вплив правки довідника (RT-25)](#н-д-вплив-правки-довідника-rt-25)
- [Н-Е. Конструктор шаблону: порядок колонок і умовне форматування](#н-е-конструктор-шаблону-порядок-колонок-і-умовне-форматування)
- [Н-Ж. Документ: перехід до комірки, «правка поза вікном», перенос версії](#н-ж-документ-перехід-до-комірки-правка-поза-вікном-перенос-версії)
- [Н-З. Імпорт структури з Excel і пакета методологій](#н-з-імпорт-структури-з-excel-і-пакета-методологій)
- [Н-И. Ефективний доступ користувача](#н-и-ефективний-доступ-користувача)
- [Н-К. Стан перерахунку проєкту](#н-к-стан-перерахунку-проєкту)
- [Що в документації відстає від коду](#що-в-документації-відстає-від-коду)

---

## Н-А. Події з PI і прив'язки PI за вікном рядка

Загальна передумова групи: з'єднання PI налаштоване (TESTER-GUIDE п. 4), на
ньому є сутність джерела, чий **код дорівнює імені шаблону подій PI**
(EventFrame template); у середовищі задано ключі `PiSqlClient:EventQuery` і
`PiSqlClient:EventTemplateQuery` (**типового значення немає** — без них
частина кроків дає «не налаштовано», див. Н-А2). Потрібен проєкт з документом,
у шаблоні якого є **динамічна** таблиця з колонками дат (початок/кінець) і
Decimal.

⚠ Документ `FEATURE-HSE301-VIEW.md` §10.6 описує вкладку в редакторі таблиці
шаблону; у коді вона — в шухляді з'єднання на `/admin/sources`. Правильне —
те, що в коді.

### Н-А1. Вкладка «Events from PI»: видимість і шаблон подій — ✅

- **Права:** `Integration.View` (бачить сторінку), `Integration.Manage` (бачить вкладку).
- **Кроки:**
  1. Увійти користувачем лише з `Integration.View`; `/admin/sources` → клік по рядку з'єднання.
  2. Те саме користувачем з `Integration.Manage`.
  3. Відкрити вкладку **«Events from PI»**, у списку **«Event template»** вибрати сутність.
  4. Повторити на з'єднанні без жодної сутності.
- **Очікується:**
  - п. 1: у шухляді вкладки Connection, Schedule; вкладок **Entities** і **«Events from PI»** немає (не вимкнені — відсутні);
  - п. 2: обидві вкладки є;
  - п. 3: у списку — **усі** сутності з'єднання (не лише шаблони подій), підказка «A source entity of this connection whose code is the name of a PI event template.»;
  - п. 4: «This connection has no source entities yet. Add the event template on the Entities tab first.»
- **Вимоги:** HSE301 A6-UI, D-171/D-172.

### Н-А2. Читання подій не налаштоване — ✅

- **Передумова:** ключі `PiSqlClient:EventQuery` / `EventTemplateQuery` не задані.
- **Права:** `Integration.Manage`.
- **Кроки:** відкрити «Events from PI», вибрати шаблон; натиснути «Test» у розділі проби.
- **Очікується:** інформаційний банер **«Event reading is not configured»** (текст із відповіді сервера); таблиця подій лишається видимою; проба відмовляє з тим самим поясненням, нічого не пише.
- **Помилки:** `422 ECR-INT-0422 eventQueryNotConfigured` / `queryKindNotConfigured` / `queryKindNotSupported`.
- **Вимоги:** HSE301 A6.

### Н-А3. Проба «Test on recent events» — 🟨

- **Права:** `Integration.Manage` (проба — лише з ним: `POST /api/v1/data-sources/{id}/probe-events`).
- **Дані:** у PI є події вибраного шаблону за останні 30 днів.
- **Кроки:**
  1. Розділ «Test on recent events»: «Days back» = 30, «At most events» = 20 → **«Test»**.
  2. «At most events» = 1 при кількох подіях у вікні.
  3. «Days back» = 93 (або «At most events» = 0 / 101).
  4. Вікно без подій (напр. 1 день у тихий період).
- **Очікується:**
  - п. 1: «Window {from} – {to} UTC: {count} events.»; таблиця «Start / end, UTC | Event | Attributes»; **нічого не записано** («Reads real events of the template from PI and writes nothing.»); атрибути — ті, що вже є в мапінгу (крім `$…`), якщо таких нема — усі;
  - п. 2: «There were more events than the limit: only the first ones are shown.»;
  - п. 3: відмова перевірки, запит до PI не йде;
  - п. 4: «No events of the template in this window.»;
  - якщо в мапінгу є поле з «Value map»: блок «Values of {attribute} without a pair:» з кнопками «+ {value}»; кнопка додає пару у форму мапінгу, запис довідника треба обрати вручну.
- **Помилки:** `422 ECR-REQ-0422 probeEventsInvalid` (вікно > 92 днів, ліміт поза 1–100); `503 ECR-INT-0503 probeTimeout` / `probeUnavailable`; частково — «The source answered partially ({code})…».
- **Обмеження:** `FEATURE-HSE301-VIEW` §10.6 обіцяє 7 днів — у коді типово 30; окремої кнопки ▶ «Перевірити на прикладі» немає.
- **Вимоги:** HSE301 A6-UI, §4.7.3.

### Н-А4. Мапінг подій: створення, валідація, пауза, видалення — ✅

- **Права:** `Integration.Manage` **і** ресурсний грант `Manage` на проєкт документа.
- **Кроки:**
  1. Панель «Event mappings» → **«Create mapping»** → «Document», «Dynamic table»; у сітці «Column ↔ attribute» уже є рядки `$start`, `$end` — зіставити їх із колонками-датами; «Map a column» → додати Decimal-колонку ← атрибут, «Units» (джерела/колонки); для Lookup-колонки «How» = «By registry code».
  2. Зберегти.
  3. Спробувати: прибрати `$start`; зіставити `$end` з не-датою; вибрати не динамічну таблицю; дві колонки на один атрибут; «Only events where» без значення.
  4. Створити другий мапінг на ту саму пару документ + таблиця.
  5. «Edit» мапінгу — поля Document і Dynamic table вимкнені; змінити «Volume»; зберегти.
  6. «Pause», потім «Resume».
  7. «Delete» мапінгу, за яким ще **нічого не синхронізовано**; потім — за яким уже є події.
- **Очікується:**
  - п. 1–2: «Event mapping created.»; рядок у панелі з бейджем «Active»; поки є проблеми — список під «Saving is not possible yet:», «Save» вимкнена;
  - п. 3: відповідна проблема у формі (клієнт) або 422 від сервера;
  - п. 4: 409;
  - п. 5: «Event mapping saved.»; документ/таблицю змінити неможливо (PUT — повна заміна решти полів);
  - п. 6: «Event mapping paused: synchronization no longer writes through it.» / «Event mapping resumed.»;
  - п. 7: перше — «Event mapping deleted.»; друге — відмова з підказкою поставити на паузу (діалог: «Only a mapping with no synchronized events can be deleted; otherwise pause it.»);
  - кожна зміна — у журналі `aud.StructureChange` (ФВ-12.10).
- **Помилки:** `409 ECR-INT-0409 eventMapExists` / `eventMapHasLinks` / `eventMapColumnTaken` / `eventMapSourceValueTaken`; `422 ECR-INT-0422 eventMapStartEndRequired` / `eventMapStartEndNotDate` / `eventMapTargetNotDynamic` / `eventMapColumnNotInTable` / `eventMapReservedAttributeInvalid` / `eventMapValueKindMismatch` / `eventMapFilterIncomplete` / `eventMapValueMapNotAllowed` / `eventMapTableNotInDocument`; `404 ECR-INT-0404 eventMap`; `403 ECR-AUTH-0403 noProjectManageGrant`.
- **Примітка:** `POST /api/v1/source-event-maps` повертає **200**, не 201 — не дефект.
- **Вимоги:** HSE301 A6, ФВ-12.10, D-174, D-185…D-189.

### Н-А5. Синхронізація подій і таблиця «Events from PI» — 🟨

- **Права:** `Integration.Manage` для «Get from PI now»; перелік подій бачить користувач, що може читати документ мапінгу.
- **Дані:** активний мапінг (Н-А4); у PI — закриті події, одна відкрита (без кінця), одна в періоді, що вже `Closed`.
- **Кроки:**
  1. Без жодного мапінгу натиснути **«Get from PI now»**.
  2. З активним мапінгом — «Get from PI now»; відкрити `/admin/jobs` за номером задачі з тосту.
  3. Таблиця подій: фільтри «Mapping», «States», «Period», «From (UTC)»/«To (UTC)»; «Show more».
  4. Відкрити документ за посиланням у колонці «Document / period».
  5. Вручну змінити в документі значення з події; повторити п. 2.
  6. Запустити збір кнопкою «Collect» на `/admin/sources` для сутності-шаблону подій без мапінгів точок.
- **Очікується:**
  - п. 1: помилка з підказкою «Create an event mapping first: it tells where the events go.» і кнопкою «Create mapping»;
  - п. 2: «Event synchronization queued: {job}.»; задача «Source event sync», прогрес «Created {created}, updated {updated}, kept manual…»;
  - п. 3: «Events: {count}»; час — у поясі проєкту, під ним UTC; «To» включає обраний день; відкрита подія — стан Open, «still open», **рядка в документі не створює**; подія в закритому періоді — «Period closed»; під назвою події — моноширинний primaryElement; ключ рядка `EF-…`; сторінки по 50;
  - п. 4: у динамічній таблиці рядок з ключем `EF-<id>`, значеннями з атрибутів; після запису — перерахунок періоду;
  - п. 5: ручне значення **не перезаписано**; у «Details» — «Kept manual:» з кодом колонки;
  - п. 6: ставиться лише синхронізація подій (без збору точок); вікно — `LookbackDays` розкладу або 7 днів;
  - подія належить періоду свого **початку** в поясі проєкту (D-179); беруться лише кореневі події (без батька); «Missing in PI» — лише при повному читанні.
- **Помилки:** `422 ECR-INT-0422 eventSyncNoMap`; `422 ECR-REQ-0422 pageSizeOutOfRange` (limit поза 1–500).
- **Обмеження:** на живому PI (RTQP) не перевірялося (TESTER-GUIDE И-9/И-10, п. 7.11).
- **Вимоги:** HSE301 A5b, A6, D-179, ФВ-11.1.

### Н-А6. Повний природний ключ події (M6) — 🟨

- **Права:** `Integration.Manage`; для перевірки в БД — читання `ext.SourceEventLink` (DBA, тимчасова база).
- **Кроки:**
  1. Синхронізувати подію; в PI перестворити її з **новим ID** (той самий початок і елемент); синхронізувати знову.
  2. Дві події з однаковим початком і назвою, але **різними елементами**.
  3. Дві події з однаковим початком **і** елементом.
- **Очікується:** п. 1 — дубля немає, `RowKey` лишається `EF-<перший ID>`; п. 2 — два окремі рядки; п. 3 — неоднозначні, не зіставляються ні з чим (нового злиття нема). У БД `PrimaryElement` (Trim + UPPER), індекс `UX_SEL_NaturalKey (MapId, StartUtc, PrimaryElement)`; старі зв'язки без елемента отримують його під час синхронізації, якщо ключ однозначний.
- **Обмеження:** перестворення події з новим ID залежить від можливостей PI-стенда.
- **Вимоги:** HSE301 M6.

### Н-А7. Прив'язки PI за вікном рядка: CRUD — ✅

Розділ **«PI row-window bindings»** у тій самій вкладці.

- **Права:** перегляд — `Integration.View` або `Manage`; зміни — `Integration.Manage` **і** грант `Manage` на **кожен** проєкт, що використовує колонку-ціль.
- **Дані:** динамічна таблиця з колонками: ціль (Decimal), початок і кінець вікна (Date), за потреби колонка-селектор.
- **Кроки:**
  1. **«Create binding»** → «Document» (лише для читання колонок таблиці), «Dynamic table», «Target column (Decimal)», «Window start column (Date)», «Window end column (Date)», «Summary» = «Total over time», «Unit of the target column»; у блоці «Sources» → «Add source»: «Source entity», «Attribute path» (вільний текст), «Source unit». Зберегти.
  2. Створити другу прив'язку на ту саму колонку-ціль.
  3. Однакові колонки початку й кінця; ціль не Decimal; «Coverage…» = 101; два джерела з селектором `A` і `a`.
  4. «Edit» — у формі поле Document порожнє; змінити «Days to refetch…»; зберегти (Н-А7а).
  5. «Pause» / «Resume».
  6. «Delete» прив'язки без підтягнутих значень; потім — з підтягнутими.
- **Очікується:** п. 1 — «Binding created.», рядок у переліку з колонками Target column / Window / Selector column («one attribute for all rows», якщо без селектора) / Summary / Sources / State; п. 2 — 409; п. 3 — проблеми під «Cannot save yet:» або 422; п. 5 — «Binding paused.» / «Binding resumed.»; п. 6 — перше «Binding deleted.», друге відмова з порадою поставити на паузу. Типові значення: покриття 95 %, повтор 7 днів, поріг розриву порожній.
- **Помилки:** `409 ECR-INT-0409 rowWindowTargetTaken` / `rowWindowMapHasValues` / `rowWindowConcurrency` (чужа `rowVersion`) / `rowWindowSelectorTaken`; `422 ECR-INT-0422 rowWindowTargetNotInTable` / `windowColumnsNotDate` / `targetNotDecimal` / `windowColumnNotInTable` / `selectorNotInTable` / `windowColumnsSame` / `rowWindowPolicyOutOfRange` / `rowWindowSelectorWithoutColumn`; `422 ECR-REQ-0422 rowWindowSummaryUnknown` / `rowWindowSourceInvalid`; `404 rowWindowMap`.
- **Обмеження (Н-А7а):** при редагуванні треба знову вибрати документ («Choose a document to read the table columns.»), інакше «Save» вимкнена — пауза кнопкою в рядку цього не потребує. Прив'язка без джерел або з джерелом іншої сутності **не з'явиться** в розділі цієї сутності. Документи для вибору — перші 500.
- **Вимоги:** HSE301 A1, ФВ-12.10.

### Н-А8. Прив'язка вікна рядка під час роботи — 🟨

- **Права:** автор даних з `Write` на документ; для задач — `System.ViewHealth` (`/admin/jobs`).
- **Дані:** активна прив'язка (Н-А7); документ у періоді `Open` або `Grace`.
- **Кроки:**
  1. У документі ввести в рядку початок і кінець вікна (≤ 32 діб).
  2. Змінити в рядку лише не пов'язану колонку.
  3. Ввести кінець ≤ початку; вікно 40 діб; порожній кінець.
  4. Задати вручну значення цілі; змінити вікно.
  5. Імпортувати книгу Excel у документ, де є колонки вікна.
  6. Документ у періоді `Closed` — змінити вікно.
  7. Дочекатися хвилини :05 наступної години (щогодинний повтор).
- **Очікується:** п. 1 — ставиться задача «PI row-window fetch»; ціль отримує значення (стан Fetched або Partial, якщо покриття < порогу); потім перерахунок; п. 2 — задача **не** ставиться; п. 3 — InvalidWindow, до PI запит не йде, комірка не змінюється; п. 4 — ручне значення лишається (KeptManual); п. 5 — одна задача на таблицю; п. 6 — «Nothing to fetch…»; п. 7 — повторне підтягування для NoData / Partial / SourceError, якщо кінець вікна не старший за «Days to refetch», і для вікон, що ще тривали. Прогрес задачі: «Fetched {fetched}, partial…, invalid window…, no source…». Не більше 500 рядків за прогін — решта продовженням.
- **Обмеження:** кнопки «Підтягнути з PI» в документі, `POST /documents/{id}/row-windows/fetch` і `probe-window` з FEATURE §4.4/§10.6 **у коді немає**; вікно на переході годинника вважається InvalidWindow. Живим PI не перевірено.
- **Вимоги:** HSE301 A1.

---

## Н-Б. Конвеєр даних `/admin/pipeline`

⚠ За рішенням D-235 (дефолт діє, чекає підтвердження людиною) екран показує
**п'ять фіксованих кроків** на наявних ендпоінтах. Декларативних операцій
join / filter / group / compute / script у бекенді **немає**, і в UI їх не
видно взагалі — ні заглушок, ні вимкнених кнопок. Додавати, переставляти чи
видаляти кроки не можна; «зберегти конвеєр» немає. Фіксувати це дефектом не
треба.

### Н-Б1. Відкриття екрана і вибір сутності — ✅

- **Права:** `Integration.Manage` (маршрут). Для кроку 2 додатково `Integration.EditSchedule`.
- **Кроки:**
  1. Меню **«Data pipeline»** → `/admin/pipeline` на стенді без джерел.
  2. На стенді з джерелами — не вибирати сутність.
  3. «Source entity» → вибрати сутність; оновити сторінку.
- **Очікується:** п. 1 — «No collection sources configured» / «Without sources the system works fine: data is entered by hand.»; п. 2 — «Pick a source entity above to see its pipeline…»; п. 3 — вступ «The steps the system runs for this source entity, with real collected rows of the last 7 days after each step…», п'ять карток «1. Source», «2. Collection schedule», «3. Collection», «4. Mapping», «5. Write to documents»; вибір тримається в URL (`?entity=<id>`).
- **Помилки:** `403 ECR-AUTH-0403` без права; `404 ECR-INT-0404 sourceEntity`.
- **Вимоги:** ФВ-14.3 (область 9), D-235.

### Н-Б2. Стани кроків і «звуження до нуля» — ✅

- **Дані:** сутність зі збором, розкладом і мапінгами; друга — без розкладу; третя — з мапінгом, що «stays raw».
- **Кроки:** для кожної сутності прочитати бейджі й лічильники «Rows out: {points}» (кроки 3–5).
- **Очікується:**
  - «Passes data» — дані проходять;
  - «Narrows to zero» — **лише перший** крок, де лічильник став 0; картка з червоною рамкою й alert «The data narrows to zero at this step» з підказкою для кроку; наступні кроки — «No data reaches it»;
  - «Check»: Source — останній прогін Degraded; Schedule — розкладу немає; Write — 0 записано, але є «stays raw»;
  - «Off»: сутність неактивна / розклад вимкнено; «Failed»: останній прогін Failed / розклад з помилкою;
  - лічильники: Collection = точки, що прийшли; Mapping = сума по **активних** мапінгах; Write = активні мапінги з результатом «lands in a cell»;
  - дані — уже зібрані (`ext.RawDataPoint`) за 7 днів, не живе читання; вікно на екрані не змінюється.
- **Обмеження:** користувач з `Integration.Manage`, але без `Integration.EditSchedule`, бачить помилку всередині кроку 2, а бейдж кроку лишається «Passes data» (так задумано: нечитаний розклад не вважається дефектом кроку).
- **Вимоги:** ФВ-14.3, D-235.

### Н-Б3. Редагування розкладу і мапінгів з екрана конвеєра — ✅

- **Права:** `Integration.Manage` + `Integration.EditSchedule`.
- **Кроки:**
  1. Крок 2: «Create schedule» (cron, «Collection window (days)», «Enabled») → «Save»; змінити; «Remove» (підтвердження «Remove this schedule? Collection will no longer run automatically.»).
  2. «Collection window (days)» поза межами.
  3. Змінити розклад у двох вкладках, зберегти в обох.
  4. Крок 1: «Open connections».
  5. Крок 4: «Add mapping» → «Add a source field mapping» → «Save mapping»; «Pause»/«Resume»; «Remove mapping» мапінгу без зібраних даних і з даними.
  6. Змінити одиницю джерела (банер «Source unit changed») → «Yes, accept {unit}» / «No, this is a source error».
- **Очікується:** п. 1 — «Schedule saved.» / «Schedule removed.»; п. 2 — «Enter a whole number of days from {min} to {max}.»; п. 3 — друга вкладка: 409 і кнопка «Reload the current version»; п. 4 — перехід на `/admin/sources`; п. 5 — «The mapping has been created.», «Mapping paused.»/«Mapping resumed.»; мапінг із даними не видаляється — порада поставити на паузу; крок 5 показує «Gaps» («Mappings that will put nothing in the document», «Source fields that land nowhere», «Columns with nothing behind them») або «No gaps…»; п. 6 — прийняття пишеться в `aud.StructureChange`.
- **Помилки:** `422 ECR-REQ-0422 collectionScheduleCron` / `…CronLength` / `…Lookback` / `…IfMatch` / `…NotApplied`; `409 ECR-JOB-0409 collectionScheduleExists` / `collectionScheduleChanged`; `409 ECR-INT-0409 mappingHasCollectedData` / `mappingUnitChangeNotPending`; `422 ECR-INT-0422 pendingUnitNotInCatalog`; `404 ECR-UOM-0404 unitId`.
- **Вимоги:** ФВ-14.3, ФВ-12.10, D-235.

---

## Н-В. Конструктор і дані довідників (8.12, 8.14, 8.16)

Загальна передумова: довідник-чернетка з полями різних типів (Int, Decimal,
Date, Bool, Lookup, Unit), бажано один темпоральний і один External.

### Н-В1. Табличний редактор записів — ✅

- **Права:** перегляд — `Registry.View` (або грант Read); редагування — `Registry.EditData` (або грант Write).
- **Кроки:**
  1. `/admin/registries` → вибрати довідник → **«Open data»** (`/admin/registries/:code/entries`).
  2. «New entry» (або Ctrl+Enter); ввести значення; у Decimal — `1,5`; у Date — `31.12.2026`; порожнє обов'язкове поле.
  3. Ввести код, що вже є в іншому рядку.
  4. Виправити; **«Save {count} changes»** (або Ctrl+S).
  5. Вставити з Excel блок комірок, де Lookup — коди/назви, частина — невідома.
  6. Позначити рядок на видалення (Ctrl+Shift+Delete) → «Keep row» → знову «Delete row» → зберегти.
  7. Відкрити запис у двох вкладках, змінити в обох, зберегти.
  8. Темпоральний довідник: змінити «As of».
  9. Користувачем без `Registry.EditData`; потім на External-довіднику.
  10. Змінити значення й піти зі сторінки.
- **Очікується:** п. 2 — підказки «Use a dot, not a comma, as the decimal separator.», «Enter a date as YYYY-MM-DD.», «A value is required.»; код нового рядка «Assigned on save» (режим Auto); за 600 мс після правки жива перевірка («Check passed» або «{errors} errors, {warnings} warnings»); п. 3 — «Same key {key} as row {row}.», збереження заблоковано; п. 4 — один пакет «усе або нічого», «Saved {time}»; п. 5 — незіставлені комірки не змінені, банер «{count} pasted cells could not be matched and were left unchanged.»; п. 7 — у другій вкладці помилка рядка; п. 8 — дані на дату, `?asOf=` в URL; п. 9 — банер «Read only: editing needs the Registry.EditData permission.» / «Read only: this registry is mastered by an external source.»; п. 10 — перехоплення незбережених змін.
- **Клавіатура:** стрілки, Home/End, Ctrl+Home/End, PgUp/PgDn (10 рядків), Enter/F2/Alt+↓ — редагувати, Esc — скасувати, Delete — очистити, Ctrl+. — шторка запису.
- **Помилки:** `ECR-REG-4093 entryChanged` («Entry {entryCode} was changed after you opened it.»); `ECR-REG-4092 keyTaken` / `keyWindowOverlap`; `ECR-REQ-0422 batchItemInvalid` / `batchItemNewOnly` / `batchTooLarge` (> 2000 рядків); `ECR-REG-4221 ruleViolated`; `422 ECR-REQ-0422 asOfRequired` (темпоральний без дати, API).
- **Вимоги:** ФВ-8.12.

### Н-В2. Шторка запису: «Values as of» — ✅

- **Права:** `Registry.View`.
- **Кроки:** у редакторі записів «Open entry» (або Ctrl+.) → вкладка **«History»** → «Values as of» = дата до зміни значення; потім дата до створення запису.
- **Очікується:** таблиця «Field | {дата} | Now», змінене позначено «· changed»; для дати до створення — «The entry did not exist on {date}.»; шторка тримається в URL (`?panel=entry-{id}`).
- **Вимоги:** ФВ-8.12, RT-15 (частина «станом на дату»). Журнал «хто/що змінив» — Н-Г1 🔀.

### Н-В3. «Де використовується» запису — 🟨

- **Права:** `Registry.EditDefinition` (без нього колонки «Usage» немає).
- **Дані:** запис A довідника X, на який посилаються: поле Lookup іншого довідника, дочірні записи, колонка шаблону з Lookup на X; методологія, де A — речовина.
- **Кроки:** `/admin/registries` → X → колонка **«Usage»** → **«Where used»** у рядку A.
- **Очікується:** діалог «Where entry "{code}" is used» з розділами: «Registry entries that refer to it» (записи, чинні **сьогодні**; до 20 на поле, «Shown {shown} of {total}»), «Child entries», «Methodologies that declare it as a substance», «Template columns that take values from this registry»; унизу — «Not listed here per entry: document cells and headers, methodology constants and cascade links…»; посилання на записи ведуть на `/admin/registries?code=X&q=CODE`. Відмова читання одного поля (напр. через обмеження S18) — помилка лише цієї групи.
- **Обмеження:** комірки документів, константи методологій і каскадні зв'язки поіменно **не** перелічуються — діалог про це каже.
- **Вимоги:** ФВ-8.14.

### Н-В4. Композиція: нове поле «Part of parent» — ✅

- **Права:** `Registry.EditDefinition`.
- **Дані:** довідник-батько P і **порожній** довідник-частина C (без записів, не темпоральний).
- **Кроки:**
  1. `/admin/registries/C/definition` («Registry designer») → нове поле Lookup на P → прапорець **«Part of parent»** → «When the parent is deleted» = «A parent with parts cannot be deleted» (типово) → зберегти.
  2. Друге поле-композиція в C.
  3. Композиція на сам C; C темпоральний; цикл P → C → P.
  4. Для наявного поля змінити зв'язок на композицію.
  5. Додати в C запис, потім спробувати додати нове обов'язкове поле.
- **Очікується:** п. 1 — поле стає обов'язковим; п. 2–3 — попередження клієнта («A registry can be part of only one parent…», «A registry cannot be part of itself…», «A part cannot have its own validity window…») або відмова сервера; п. 4 — для наявних полів прапорця немає; через API — відмова; п. 5 — відмова.
- **Помилки:** `422 ECR-REG-0422 compositionNotLookup` / `compositionMoreThanOne` / `compositionTargetSelf` / `compositionNotRequired` / `compositionChildTemporal` / `compositionCycle` / `relationKindImmutable` / `newFieldRequired`.
- **Вимоги:** ФВ-8.16.

### Н-В5. Редактор master-detail і індикатор Σ — ✅

- **Права:** перегляд — `Registry.View`; правка — `Registry.EditData`.
- **Дані:** композиція з Н-В4; на P — правило `childSum` з допуском (рівень Error або Warning).
- **Кроки:**
  1. У конструкторі P → **«Edit with parts (master-detail)»** (`/admin/registries/P/composition`).
  2. Вибрати рядок батька → «Add part» → заповнити → **«Check»** → **«Save {count} changes»**.
  3. Змінити частину, не зберігати, вибрати іншого батька; спробувати піти зі сторінки.
  4. Порушити суму частин за межі допуску; зберегти.
  5. Відкрити редактор з боку C.
  6. F6 / Shift+F6.
- **Очікується:** п. 1 — «Master-detail editor»; без вибору — «Pick a row on the left to see and edit its parts.»; п. 2 — поле композиції в частині заповнено батьком і приховане; «Saved: {count} rows.»; п. 3 — вибір іншого батька заблоковано; «{count} unsaved changes. Save each panel or discard its changes before leaving.»; п. 4 — «Σ {field} = {sum}», «target {target} ± {tolerance}», «Out of tolerance» (червоний для Error, жовтий для Warning); для Error — 422, правки лишаються на місці; межа допуску включна; п. 5 — «This registry is part of {parent}…» + «Open the parent editor»; п. 6 — фокус між панелями. Без частин — «No parts yet: the rule does not apply.»
- **Помилки:** `422 ECR-REG-4221 ruleViolated`; ті самі, що в Н-В1.
- **Вимоги:** ФВ-8.16, RT-32.

---

## Н-Г. Історія запису й експорт довідника (RT-15, RT-16)

Сервер **злитий** у `dev/integration`. Клієнтський UI журналу й кнопки
«Export» — **лише в гілці** `lane/cloud/registry-history-export-ui-e4lg82`;
експорт із частинами композиції (`includeChildren`) — **лише в гілці**
`lane/cloud/registry-export-children-topbe3`. До зведення відповідні кроки
перевіряються через API (Swagger / `curl` із cookie сесії).

### Н-Г1. Журнал змін запису — ✅ (API) / 🔀 (UI)

- **Права:** `Registry.View` або грант Read.
- **Дані:** запис, змінений різними шляхами: форма, пакет редактора, CSV-імпорт, синк; окремо — видалений запис.
- **Кроки (API, доступно зараз):** `GET /api/v1/registries/{code}/entries/{id}/history?limit=50`; потім `limit=0` і `limit=501`; невідомий `id`.
- **Кроки (UI, 🔀):** шторка запису → «History» → розділ **«Change log»**; «Show earlier changes».
- **Очікується:** записи від найновішого; `kind` ∈ `created` / `value` / `name` / `validity` / `active` / `deleted`; видно всі шляхи запису (джерело — системні версії таблиць); значення, задані при створенні, окремих рядків не дають (їх охоплює `created`); видалений запис має історію. В UI — колонки «When | Author | What changed | Before | After»; автор фонової задачі — «Unknown (background job)»; порожньо — «No changes recorded for this entry.»; «Validity» як «from — to», відкритий кінець «open».
- **Помилки:** `404 ECR-REG-0404`; `422 ECR-REQ-0422 pageSizeOutOfRange`.
- **Не плутати:** `GET /registries/{code}/history` — історія **опису** довідника, не запису.
- **Вимоги:** RT-15, ФВ-8.12.

### Н-Г2. Експорт CSV / XLSX — ✅ (API) / 🔀 (UI)

- **Права:** `Registry.View` або грант Read.
- **Кроки (API):**
  1. `GET /api/v1/registries/{code}/export?format=csv`; потім `format=xlsx`; `asOf=2026-01-01` для темпорального.
  2. Запис, у текстовому полі якого `=1+1`, `+7`, `-5`, `@x`; від'ємне число.
  3. Імпортувати отриманий CSV назад без змін.
  4. `format=pdf`.
  5. Довідник, де записів більше за `Registries:ExportMaxRows` (типово 50000; на стенді можна знизити).
  6. Користувачем без права читання.
- **Кроки (UI, 🔀):** `/admin/registries` (довідник вибрано) або `/entries` → **«Export»** → «CSV (can be imported back)» / «Excel workbook (XLSX)».
- **Очікується:** п. 1 — файл `registry-{CODE}-{yyyyMMdd}.csv|xlsx`; ті самі записи, що в сітці на `asOf` (типово сьогодні, UTC); Lookup — кодом цілі, Unit — кодом одиниці; CSV — UTF-8 з BOM, колонки `code` + коди полів; XLSX — додатково `@name`, `@validFrom`, `@validTo`, аркуш = код довідника; п. 2 — у CSV значення з префіксом `'` (зокрема від'ємні числа), у XLSX — текст, не формула; п. 3 — імпорт проходить, апостроф знято; п. 5 — відмова, **обрізаного файлу немає**; у журналі безпеки подія `RegistryExported` (код, формат, дата, кількість рядків). В UI (🔀) для темпорального — «Entries effective on {date}.»; помилки під заголовком «Export failed»; прапорець «With child parts (composition)» **вимкнений** («Not available yet…»).
- **Помилки:** `422 ECR-REQ-0422 registryExportFormatUnknown` («…use csv or xlsx»); `422 ECR-REQ-0422 registryExportTooLarge`; `403`; `404 ECR-REG-0404`; при CSV-імпорті невідомої одиниці — `422 ECR-REG-0422 unitCodeUnknown`.
- **Конфігурація:** `Registries:ExportMaxRows` < 1 — служба не стартує (перевірка конфігурації).
- **Вимоги:** RT-16, ФВ-8.12.

### Н-Г3. Експорт із частинами композиції — 🔀

Лише в `lane/cloud/registry-export-children-topbe3`; UI-прапорця немає й після
зведення цієї гілки (у гілці UI він жорстко вимкнений до зведення обох).

- **Права:** читання батька **і кожного** дочірнього довідника.
- **Кроки:** `GET …/export?format=csv&includeChildren=true` для P з Н-В4; потім `format=xlsx`; потім користувачем без права на C.
- **Очікується:** CSV → `application/zip` `registry-{CODE}-{yyyyMMdd}.zip` з файлами `01-P.csv`, `02-C.csv`… (номер — порядок імпорту, кожен імпортується окремо); XLSX — аркуш на кожен довідник, однакові назви з суфіксом `~2`; без права на частину — відмова **всього** експорту (403, а для прихованого — 404); стеля рядків рахується на суму; `RegistryExported` містить `includeChildren` і перелік довідників.
- **Вимоги:** RT-16, ФВ-8.16.

---

## Н-Д. Вплив правки довідника (RT-25)

Злито в `dev/integration` (гілка `rt25-impact-client` застаріла — її зміст
уже в `dev/integration`).

### Н-Д1. Сторінка «Registry impact» — 🟨

- **Права:** `Registry.View` + `Calculation.View` у проєкті документа (документи інших проєктів мовчки не показуються).
- **Дані:** методологія, що читає довідник X; документи з поточними результатами у відкритих (`Open`/`Grace`) періодах і один — у закритому.
- **Кроки:** `/admin/registries` → X → **«Affected documents»** (`/admin/registries/X/impact`).
- **Очікується:** заголовок «Registry impact» + код; колонки «Document | Period | State | Methodologies»; «Shown {shown} of {total}»; документа закритого періоду **немає**; якщо список обрізано (стеля 1000 / віддається до 500) — «The list reached the server limit…»; порожньо — «No affected documents».
- **Обмеження:** список **не порожніє** після перерахунку — сервер показує всі документи відкритих періодів, результати яких залежать від довідника, незалежно від застарілості. Успіх перерахунку перевіряйте за банером у документі (Н-Д3).
- **Вимоги:** RT-25, ФВ-9.19.

### Н-Д2. «Recalculate affected» — ✅

- **Права:** `Calculation.Recalculate` у проєкті **кожного** документа + читання довідника; для посилання на задачу — `System.ViewHealth`.
- **Кроки:**
  1. Без вибору → **«Recalculate affected»** → поле «Reason» порожнє → «Queue recalculation».
  2. Причина > 400 символів.
  3. Причина задана → «Queue recalculation».
  4. Позначити два документи → «Recalculate selected (2)».
  5. Через API передати `documentIds` з документом, якого немає у впливі.
  6. Користувачем без `Calculation.Recalculate`.
- **Очікується:** п. 1–2 — відмова; п. 3 — 202, картка «Recalculation job {jobId}» з рядком «Fanned out {total}, done {done} of {total}, errors {failed}.»; задача «Recalculation of documents affected by a registry edit», статус «Queued for recalculation: {queued}; skipped: {skipped}; no longer affected: {gone}»; однакові набори документів зливаються в одну задачу; посилання «Open in the job queue» лише з `System.ViewHealth`, без нього — «The job state cannot be read with your rights.»; після постановки перелік і результати розрахунку перечитуються; п. 6 — чекбоксів і кнопки немає.
- **Помилки:** `422 ECR-REQ-0422 impactReasonRequired` / `impactReasonTooLong`; `422 ECR-REG-0422 impactDocumentNotAffected` (також для документа закритого періоду — сервер навмисно не розрізняє) / `impactNothingToRecalculate`; `403` — документ чужого проєкту.
- **Вимоги:** RT-25.

### Н-Д3. Банер «довідник змінено після розрахунку» — 🟨

- **Права:** `Calculation.View` (панель «Calculation results» документа).
- **Кроки:** 1) перерахувати документ; 2) змінити запис довідника X, який читає методологія; 3) відкрити документ; 4) перерахувати (Н-Д2 або кнопкою в документі); 5) знову відкрити.
- **Очікується:** п. 3 — жовтий alert «These results are out of date» + «Registry "{name}" was changed after the calculation»; **подання не блокується**, автоматичного перерахунку немає; п. 5 — банер зник.
- **Обмеження:** у банері замість назви довідника стоїть його **код** — відомо, не дефект нової функції.
- **Вимоги:** ФВ-9.19, RT-25.

---

## Н-Е. Конструктор шаблону: порядок колонок і умовне форматування

Маршрут: `/admin/templates/:id/versions/:versionId`.

### Н-Е1. Перестановка колонок (DnD і кнопки) — 🟨

- **Права:** `Template.Edit` (без нього колонки «Order» немає); перегляд — `Template.View`.
- **Дані:** таблиця з ≥ 2 колонками; працює і в **опублікованій** версії (порядок — презентаційний шар).
- **Кроки:**
  1. Перетягнути колонку за ручку `⠿` («Drag to reorder»).
  2. Кнопками ↑ / ↓ («Move {name} up/down»), зокрема з першої позиції вгору.
  3. Лише клавіатурою: Tab до кнопки ↓ → Enter.
  4. Оновити сторінку; відкрити документ проєкту на цій версії.
  5. Спробувати переставити **рядки**.
- **Очікується:** п. 1–3 — тост «{name} is now in position {position} of {count}.»; ↑ на першій і ↓ на останній позиції вимкнені; п. 4 — порядок збережено; п. 5 — кнопки й ручка рядків вимкнені, підпис «Rows cannot be reordered here yet… Use the Order field in the row form.»
- **Помилки (API `PATCH /api/v1/template-versions/{id}/presentation`):** `422 ECR-TMPL-0422 emptyPatch` / `ordinalInvalid` (не ціле 0…1000000).
- **Обмеження:** перестановка рядків — лише полем «Order» у формі рядка (D-234).
- **Вимоги:** ФВ-2.6, ФВ-2.7, D-234.

### Н-Е2. Умовне форматування на поточній збірці — 🟨

На поточній збірці панель у конструкторі — **лише пісочниця** (правила не
зберігаються), а сервер уже зберігає й застосовує правила. Тому збереження
перевіряється через API, а застосування — через зріз таблиці й Excel-експорт.

- **Права:** `Template.Edit`; кнопка видна лише в **чернетці**.
- **Кроки (UI):**
  1. У рядку дій таблиці **«Conditional formatting»**.
  2. «Add rule» → «Column», «Condition» = «Between», «Value»/«Up to» (верхня < нижньої), без кольору й жирності.
  3. «Check on a value» → «Sample value».
- **Очікується (UI):** жовтий alert «Rules are not saved yet…»; підказки «The upper bound must not be less than the lower one.», «Choose a fill color, text color or bold, otherwise the rule changes nothing.»; перевірка на значенні показує «Rule {n} applies.» або «No rule applies to this value.»; **«Save rules» завжди вимкнена**.
- **Кроки (API):**
  4. `PUT /api/v1/template-versions/{id}/conditional-formats` з тілом `{rules:[{columnCode, operator:"gt", value:"100", backgroundHex:"#ffcccc", isBold:true}]}` на чернетці; `GET` того самого шляху.
  5. Помилкові правила: невідомий оператор; `value:"abc"`; колір `red`; неіснуюча колонка; > 500 правил.
  6. PUT на опублікованій версії.
  7. Опублікувати версію, в документі ввести значення 150 і 50; `GET /documents/{id}/tables/{tableInstanceId}`; Excel-експорт документа.
- **Очікується (API):** п. 4 — правило повернуто; п. 5 — 422; п. 6 — 409; п. 7 — у зрізі `cellFormats["{rowKey}:{columnCode}"]` лише для 150; у Excel — заливка й жирний у тій комірці. `between` включає межі (порядок меж не важливий), кома = крапка, перше правило за порядком виграє.
- **Помилки:** `422 ECR-CFG-0422 condFormatOperator` / `condFormatOperand` / `condFormatColor` / `condFormatColumn` / `condFormatLimit`; `409 ECR-TMPL-0409` (заморожена версія); `404 ECR-TMPL-0404 templateVersion`.
- **Обмеження:** у пісочниці `eq`/`ne` порівнюють і текст, сервер — лише числа. Сітка документа на цій збірці **не** фарбує комірки.
- **Вимоги:** ФВ-2.6, ФВ-2.7, D-234.

### Н-Е3. Збереження правил з UI і підсвітка в сітці — 🔀

Лише в `lane/cloud/condfmt-client-apply-z4u9rv`.

- **Права:** `Template.Edit`, чернетка.
- **Кроки:**
  1. «Conditional formatting» у таблиці без правил → додати правило → **«Save rules»**.
  2. Відкрити панель у двох вкладках; зберегти в першій, потім у другій.
  3. PUT через API без заголовка `If-Match`.
  4. Опублікувати, відкрити документ, ввести значення, що підпадає під правило, зберегти й перечитати.
- **Очікується:** п. 1 — «This table has no rules yet.», потім «Conditional formatting rules saved.»; неповні правила — «Complete or remove the rules marked above before saving.»; п. 2 — alert «Someone else changed the rules…», кнопки «Save» (замінює чужі) і «Discard my changes»; п. 3 — 422; п. 4 — комірка пофарбована за сервером (`data-conditional-format="true"`); незбережена правка перефарбовується лише після перечитування зрізу.
- **Помилки:** `422 ECR-REQ-0422 condFormatIfMatch`; `409 ECR-TMPL-0409 condFormatChanged` (саме **409**, не 412).
- **Вимоги:** ФВ-2.7.

---

## Н-Ж. Документ: перехід до комірки, «правка поза вікном», перенос версії

### Н-Ж1. Від зауваження перевірки до комірки — ✅

- **Права:** `Document.View`.
- **Дані:** документ із кількома аркушами; правила перевірки, що дають зауваження на іншому аркуші, на рядку без колонки, на цілій таблиці; рядок, якого немає в поточному зрізі.
- **Кроки:**
  1. **«Validate»** → панель «Validation findings».
  2. Клік по тексту «What is wrong» (підказка «Show in the table») для зауваження на іншому аркуші.
  3. Для зауваження без колонки; на всю таблицю; на рядок поза зрізом.
  4. Двічі поспіль по тому самому зауваженню; клік і одразу вихід зі сторінки.
  5. Увімкнути в ОС «зменшити рух».
- **Очікується:** п. 1 — «Validation found {count} error(s).»; п. 2 — перемикання на аркуш, монтування таблиці (навіть непрогорнутої), прокрутка, фокус на комірці, рамка кольору primary пульсує двічі протягом 4 с; п. 3 — перша колонка даних / лише прокрутка до таблиці / таблиця без фокуса; п. 4 — повторний клік працює; після виходу стрибок не виконується; п. 5 — рамка статична. Чистий результат — «Validation passed with no errors.»
- **Вимоги:** ФВ-5.6 (TESTER-GUIDE Г-5).

### Н-Ж2. Значок «правка поза вікном» — 🟨

- **Права:** налаштування — `Template.Edit`; введення — `Write` на документ; журнал — `Security.ViewAudit`.
- **Дані:** у версії шаблону — розділ «Period access rules»: правило «Editable in a period range» (з/по період) з поведінкою «Outside the window» = **«Warn»**; документ у періоді **поза** діапазоном.
- **Кроки:**
  1. Змінити значення в комірці, яку правило забороняє, і зберегти.
  2. Навести на комірку.
  3. `/admin/audit` → знайти зміну.
  4. Оновити сторінку документа.
- **Очікується:** п. 1 — запис проходить **без** підтвердження; внизу комірки лінія 2px, угорі знак `◷` (якщо немає маркера помилки, обов'язковості чи формули); п. 2 — «Saved after the access window closed (Warn policy); the change is marked in the change log.»; п. 3 — бейдж «outside window»; п. 4 — **значок зник** (позначка живе лише у вкладці), у журналі лишається.
- **Обмеження:** позначка не переживає перезавантаження; TESTER-GUIDE (Г-6, п. 7.7, 7.12) ще пише «без позначки» — застаріло.
- **Вимоги:** ФВ-2.16, D-239.

### Н-Ж3. Перенос документів на нову версію шаблону — ✅

⚠ Версія шаблону належить **проєкту**, тому перенос, запущений з одного
документа, переносить **усі документи проєкту** однією транзакцією.

- **Права:** `Template.Edit` (без нього пункту меню немає).
- **Дані:** шаблон з ≥ 2 опублікованими версіями; проєкт на старішій, документ з введеними значеннями. Для відмов: версія з видаленою колонкою, де є дані; зі зміненим типом/одиницею/точністю; аркуш у стані Submitted/Approved; архівований проєкт.
- **Кроки:**
  1. `/documents/:id` → **«More»** (⋯) → «Move to another template version».
  2. «Target version» = новіша, режим «Safe» → **«Preview»**.
  3. Змінити режим — звіт скидається; знову «Preview» → **«Move documents»**.
  4. Повторити на версії з видаленою колонкою з даними; зі зміненим типом; режим «Appearance only» при структурних змінах.
  5. Подати аркуш і спробувати перенос; на архівованому проєкті.
  6. Шаблон з однією опублікованою версією.
- **Очікується:** п. 2 — «The template version belongs to the project, so all its documents move together. Documents: {count}.», «Values moved: {transferred}. Lost: {lost}. Change meaning: {guarded}.», зелений «The move is possible: no entered value is lost.», таблиця «Element | Change | Entered values» (до 50 рядків); «Move documents» активна лише після Preview з `canApply`; п. 3 — «Documents moved to template version {version}: {count}.», подія безпеки `DocumentVersionMigrated`; п. 4 — червоний звіт з причинами («Entered values would be lost…», «…would change meaning…», «The versions differ in structure, not only in appearance.»), перенос неможливий; п. 5 — «Some sheets are submitted or approved: return them to work first.» / «The project is archived.»; п. 6 — «There is no other published version of this template.»
- **Помилки:** `404 ECR-TMPL-0404 templateVersion`; `422 ECR-TMPL-0422 migrateSameVersion` / `migrateOtherTemplate` / `migrateTargetNotPublished`; `409 ECR-DOC-0409 migrateSheetsLocked` / `migrateProjectArchived`; `422 ECR-SCHM-0422 migrateStructural` / `migrateDataLoss`.
- **Вимоги:** ФВ-7.5.

---

## Н-З. Імпорт структури з Excel і пакета методологій

### Н-З1. `tools/Ecr.Bootstrap.Excel`: сухий прогін — 🟨

- **Права:** для сухого прогону — жодних (сервер не потрібен).
- **Дані:** книга з: Excel-таблицею; блоком без меж (розділеним порожніми рядками); прихованим аркушем; 12 колонками місяців; рядком «Всього»; заголовками з одиницями `(т)`, `(mg/Nm3)`, `(нм3/сут)`, `(МВт)`, `(xyz)`; формулами SUM у рядку і посиланням на інший аркуш; форматами `0.000` і `#,##0`.
- **Кроки:**
  1. `dotnet run --project tools/Ecr.Bootstrap.Excel -- --workbook книга.xlsx`
  2. Те саме з `--out-report звіт.md --out-plan план.json --layout MonthsInColumns`.
  3. Без `--workbook`; неіснуючий файл; `--help`; `--apply --dry-run`.
- **Очікується:** п. 1 — код виходу **0** (або **1**, якщо у звіті є помилки); консоль: «{outcome}. Аркушів N, таблиць N, колонок N, рядків N; помилок N, ручних рішень N. Звіт: …»; звіт `книга.xlsx.import-report.md` пишеться завжди; розділи «Помилки…», «Потребує ручного рішення…», «Чек-лист звірки таблиць», «Імпортовано автоматично», «Довідка…»; прихований аркуш — Info; межі блоку — ручне рішення; 12 місяців → MonthsInColumns + ручне рішення; «Всього» → Balance; `mg/Nm3` → `mg_per_Sm3`, `нм3/сут` → `Sm3_per_day`, `т`/`tonne` → `t`; `МВт`, `%`, `год` — неоднозначні; `xyz` — ручне рішення «одиницю не розпізнано… колонку створено без одиниці»; формула на інший аркуш → колонка лише для читання + ручне рішення; Scale: `0.000` → 3, `#,##0` → 0; дані комірок **не** імпортуються; п. 3 — код **2**, повідомлення й usage у stderr.
- **Обмеження:** перевірено лише на синтетичних книгах (TESTER-GUIDE З-7); розбір атрибутів VBA не реалізовано.
- **Вимоги:** ФВ-2.10, ФВ-2.11, ФВ-16.12.

### Н-З2. `tools/Ecr.Bootstrap.Excel`: запис у чернетку — 🟨

- **Права користувача API:** `Template.Edit`; публікація (`Template.Publish`) інструментом **не** виконується.
- **Кроки:**
  1. `$env:ECR_PWD='…'`; `… --workbook книга.xlsx --apply --api https://сервер --version 1.0.0.0 --template-code TPL_X --template-name "Назва" --user tester --password-env ECR_PWD`
  2. Те саме з одиницею, якої немає в каталозі сервера.
  3. Без `--version`; відносний `--api`; одночасно `--template-id` і `--template-code`; користувач без `Template.Edit`; недоступний сервер.
  4. `/admin/templates` → нова версія.
- **Очікується:** п. 1 — код **0**, «записано в шаблон {T}, версія-чернетка {V} ({n} запитів); опублікуйте після звірки»; завжди **нова** порожня чернетка; назви лише мовою `--lang` (типово `ru`); п. 2 — код **1**, «одиниці X немає в каталозі сервера», нічого не записано; п. 3 — код **2** для аргументів, **3** для відмови/недоступності сервера; якщо чернетку вже створено — «частково заповнена чернетка {id} — видаліть її або допишіть вручну»; п. 4 — структура відповідає звіту, версія — чернетка.
- **Вимоги:** ФВ-2.10, ФВ-2.11, ФВ-2.9.

### Н-З3. `tools/Ecr.MethodologyImport`: аналіз і пакет — ✅

- **Дані:** експорт AF (`ECR_01_Air.xml`) і синтетичний XML з циклом формул.
- **Кроки:**
  1. `Ecr.MethodologyImport analyze ECR_01_Air.xml`; потім `--json --out report.json`.
  2. `export ECR_01_Air.xml --out package.json`.
  3. `export … --out package.json --allow-blockers`.
  4. Неіснуючий файл; пошкоджений XML; `export` без `--out`.
- **Очікується:** п. 1 — «=== Аналіз імпорту методологій з AF XML (сухий прогін, без запису) ===»; наприкінці «Блокерів немає.» (код 0) або «БЛОКЕРИ:» (`UNRESOLVED_REFERENCES`, `REFERENCE_CYCLES`, `NO_METHODOLOGY_DATA`; код 2); JSON детермінований; п. 2 — з блокерами пакет **не** записано («Пакет НЕ записано: є блокери…», код 2); на реальному AF очікувано 17 нерозв'язаних посилань; п. 3 — файл записано, код однаково 2; п. 4 — код 1. Інструмент у БД **не пише**.
- **Вимоги:** HSE301 крок V, рішення людини 29.09 (механізм імпорту методологій).

### Н-З4. Імпорт пакета методологій у систему — ✅

- **Права:** `Calculation.EditFormula` **і** `Calculation.EditConstant` (без обох кнопки немає; API — 403); сторінка — `Calculation.View`.
- **Кроки:**
  1. `/admin/methodologies` → **«Import package»** → файл не-JSON.
  2. Пакет з блокерами → **«Check»**.
  3. Чистий пакет → «Check» → **«Import»**.
  4. Той самий пакет ще раз → «Check».
  5. Змінити текст формули в пакеті, не змінюючи номера версії → «Check» → спробувати імпорт через API (`POST /api/v1/methodologies/import?dryRun=false`).
  6. API з `timeZone=Europe/Nowhere`.
- **Очікується:** п. 1 — «The file is not valid JSON.»; п. 2 — «The package has blockers and cannot be imported», таблиця «Blockers» (до 200 рядків), «Import» вимкнена; п. 3 — «Ready to import», підсумок «New methodologies: …; new draft versions: …», після «Import» — «Package imported: draft versions created — {versions}.»; створено лише **чернетки** (публікація — окремо іншою людиною), подія `aud.StructureChange` `ImportPackage`; п. 4 — «Nothing to import: the package is already in the system» (`outcome=unchanged`); п. 5 — «Some versions already exist with different content», API — 409; п. 6 — 422. UI завжди використовує пояс `Asia/Atyrau`. Запис — одна транзакція, при відмові нічого не записано.
- **Помилки:** `422 ECR-CALC-0422 methodologyImportBlocked` / `methodologyImportTimeZone`; `409 ECR-CALC-0409 methodologyImportConflict`; `403 ECR-AUTH-0403 permission`.
- **Обмеження:** `FEATURE-HSE301-VIEW` §11.6 ще каже, що ендпоінта немає — застаріло. На реальному AF 17 посилань чекають методолога (TESTER-GUIDE З-8).
- **Вимоги:** HSE301 крок V, рішення людини 29.09 (механізм імпорту методологій з AF), TESTER-GUIDE З-8.

---

## Н-И. Ефективний доступ користувача

### Н-И1. Розріз «Effective access to a resource» — 🟨

- **Права:** сторінка — `Security.ManageRoles`; розріз — `Security.ManageUsers` (інакше 403).
- **Дані:** користувач U з роллю, що має грант `Read` на проєкт P; друга роль із грантом-забороною (deny) на довідник R; роль, звужена до аркушів/періодів; прострочене призначення.
- **Кроки:**
  1. `/admin/security` → «Users» → **«Access»** у рядку U → **«Show effective access»**.
  2. «Resource type» = Project, «Resource ID» порожній — кнопка **«Explain»**.
  3. Project + id P → «Explain».
  4. Registry + id R → «Explain».
  5. Неіснуючий id; неіснуючий користувач (API).
  6. Переглянути себе й іншого користувача, що має роль лише через групу AD.
- **Очікується:** п. 2 — кнопка вимкнена до id ≥ 1, запит не надсилається; п. 3 — «Resulting level: {level}», таблиця «Given by | Role | Assigned | Level | Scope | Counted»; зараховуються лише «Everywhere» і «Project is in scope»; «Narrowed to sheets or periods…» і «Assignment not in effect» — «No»; п. 4 — alert «Explicitly denied: a deny wins over any grant and over any global right…»; глобальне `Registry.EditData` / `Registry.View` піднімає рівень, якщо немає заборони; п. 5 — 404; п. 6 — для себе групові ролі видно, для іншого — «The groups in this person's sign-in ticket are not known here…». Розріз **нічого не змінює**.
- **Помилки:** `422 ECR-REQ-0422 effectiveAccessResource`; `404 ECR-SEC-0404 userNotFound`; `404 ECR-PRJ-0404 project`; `404 ECR-REG-0404 registryId`; `403 ECR-AUTH-0403`.
- **Обмеження:** лише проєкти й довідники (не аркуші й колонки); рівень показано сирим значенням без перекладу. TESTER-GUIDE п. 7.12 ще пише «розрізу нема» — застаріло.
- **Вимоги:** ФВ-6.16, D-220.

---

## Н-К. Стан перерахунку проєкту

### Н-К1. «Розкладено N, виконано M з N, помилок K» — 🟨

- **Права:** запуск — `Calculation.Recalculate` (сторінка `/admin/periods` — `Period.Configure`); стеження — `System.ViewHealth`.
- **Дані:** активний проєкт з кількома документами; воркер у режимі черги в БД (`Database`/`Worker`) — інакше блоку розкладу немає; один документ, що свідомо падає при розрахунку.
- **Кроки:**
  1. `/admin/periods` → проєкт → **«Recalculate»** → підтвердити «Recalculate the whole project {code}?».
  2. Взяти номер задачі з тосту «Recalculation queued as job {job}.» → `/admin/jobs` → «Job id» → **«Watch»**.
  3. Дочекатися завершення всіх дочірніх задач.
  4. Повторити з документом, що падає.
- **Очікується:** п. 2 — батьківська задача «Succeeded» з повідомленням «Queued document recalculation tasks: {count}; not calculated yet.»; блок «Fanned out, not calculated yet» і рядок «Fanned out {total}, done {done} of {total}, errors {failed}.»; оновлення кожні 1,5 с; п. 3 — «Done»; п. 4 — «Done with errors ({failed})» (скасовані дочірні теж рахуються помилками).
- **Обмеження:** сторінка `/admin/periods` показує тост «Recalculation finished: the figures are up to date.», щойно **батьківська** задача `Succeeded`, хоча дочірні ще в черзі. Правдивий стан — лише на `/admin/jobs`.
- **Помилки:** `422 ECR-CALC-4221 approvalNotUsable` (закритий період без погодження); `404 ECR-PRD-0404 periodForProject`; `403 ECR-AUTH-0403` без `System.ViewHealth`; 404 — невідомий номер задачі.
- **Вимоги:** ФВ-9.8 (P4).

---

## Що в документації відстає від коду

Знайдено під час складання сценаріїв; правити — власникам документів, тут
лише фіксація.

| Документ | Що пише | Що в коді |
|---|---|---|
| `FEATURE-HSE301-VIEW.md` §4.7.4, §11.2 | A6, `source-event-maps`, «Get from PI now» немає; A1, A6 не виконані | є (Н-А) |
| `FEATURE-HSE301-VIEW.md` §10.6 | вкладка в редакторі таблиці шаблону; проба за 7 днів; атрибут вікна з каталогу; кнопка ▶ | шухляда з'єднання; 30 днів; вільний текст; кнопки немає |
| `FEATURE-HSE301-VIEW.md` §11.6 | ендпоінта імпорту методологій немає | `POST /api/v1/methodologies/import` (Н-З4) |
| `TESTER-GUIDE.md` Г-6, п. 7.7, 7.12 | правка поза вікном «без позначки» | значок `◷` (Н-Ж2) |
| `TESTER-GUIDE.md` п. 7.12 | D-220 «розрізу нема» | розріз є (Н-И1) |
| `10-decisions.md` D-234 | застосування умовного форматування в сітці/Excel не перевірено | Excel застосовує (Н-Е2); сітка — 🔀 (Н-Е3) |
| `FEATURE-REGISTRY-TABLES.md` :761, :767 | історія без видів name/active; «422 закритий період» | види є; `impactDocumentNotAffected` |

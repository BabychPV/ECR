# Директива №15 — клієнт: як перенести гібридний макет у застосунок

**Дата:** 2026-09-19 · **Звірено з `main` @ `745d8f9`** · Частина пакета
[`DIRECTIVE-15.md`](DIRECTIVE-15.md) (читати спершу його).

**Еталон вигляду й поведінки** — `docs/design/hybrid/` (відкрити:
`node docs/design/hybrid/serve.js` → <http://127.0.0.1:5197>; карта всіх
переходів — `#/flows`). Контракт компонентів — `docs/design/hybrid/KIT.md`.
Адреси всіх 444 станів — `docs/design/hybrid/routes/*.txt` (`мітка|адреса`).

Стек не змінюється: React 19 · Vite 6 · **Mantine 7.15.2** · TanStack Query 5 ·
react-router 7 · RevoGrid 4.11. ⛔ Прототип написаний на ванільному JS навмисно,
щоб його не спокусилися скопіювати: `kit.js` — **специфікація**, не джерело
коду. Переносимо вигляд і поведінку, реалізуємо засобами Mantine.

---

## 0. Принцип №1 — як перевірювані правила

Вимога людини: «екран має сильно не навантажувати користувача». Щоб це не
лишилося настроєм, кожен перенесений екран перевіряється за переліком:

| # | Правило | Як перевірити |
|---|---|---|
| L1 | Одна `primary`-кнопка на екран | тест екрана: `getAllByRole('button')` з `data-variant="filled"` у межах `main` — рівно 1 (у діалозі — своя 1) |
| L2 | Інспектор/шухляда деталей **закриті** за замовчуванням | тест: після рендера `queryByRole('complementary')` = null |
| L3 | Колір — лише для проблеми або очікуваної дії; «все гаразд» нейтральне | `StatusBadge` — єдине джерело; ESLint-правило забороняє `color="green"` поза ним |
| L4 | `StatStrip` ≤ 4 цифри, лише на сторінках-переліках, кожна цифра — фільтр | тип `StatStripProps.items` — кортеж довжини ≤ 4 |
| L5 | Таблиця ≤ 7 колонок; решта — у шухляді рядка | `DataTable` у dev **кидає виняток** на 8-й колонці (не `console.error`: `src/test/setup.ts` консоль не стереже ✔, тож попередження ніхто б не побачив) |
| L6 | Незворотна дія → `ConfirmDialog` із назвою об'єкта в заголовку, дієсловом на кнопці, **фокусом на Cancel** | тест: `document.activeElement` = Cancel |
| L7 | Після дії — що сталося і що далі (`ResultBanner`/тост з Undo), а не мовчазне оновлення | тест потоку |
| L8 | Довше 2 с → у «My tasks», інтерфейс не блокується | — |
| L9 | Помилки форми — при спробі зберегти, фокус на перше хибне поле | тест форми |
| L10 | Порожній стан пояснює чому порожньо і дає дію; «немає прав» ≠ «порожньо» ≠ «фільтр нічого не знайшов» | три різні стани `AsyncBoundary` |

---

## 1. Токени: макет → `shared/theme/theme.ts`

Чинний стан ✔: `brand` — 10 відтінків (`theme.ts:28-39`), `primaryShade {light:6, dark:5}`,
статусні кортежі `statusError/statusWarning/success`, `cellState`, `fontSizes 11/13/15/17/20`,
`spacing 4/8/12/16/24`, `radius 2/3/4/6/8`, `other.rowHeightCompact/Comfortable 28/36`.
`cssVariablesResolver` **немає** ◐; щільність оголошена, але її ніхто не читає ◐
(`--ecr-row-height` без споживача; RevoGrid `theme="compact"` зашитий —
`DocumentGrid.tsx:1077-1088`).

| Токен макета (`index.html:9-19`) | Світла | Темна | Куди в застосунку |
|---|---|---|---|
| `--brand-50…900` | `#F4F4FC … #1A1C50` | ті самі | `brand[0..9]` — **заміна кортежу**. ⚠ `brand[6]=#3B3FA8` ≈ чинному `#383ca8` — фірмовий відтінок збережено свідомо (колір замовника — факт, якого ми не знаємо; див. Q15-01b) |
| `--ground / --surface / --sunken / --raised` | `#F6F7FA / #FFF / #EEF0F5 / #FFF` | `#12141C / #191C27 / #151822 / #222636` | `cssVariablesResolver` → `--mantine-color-body` = ground; нові `--ecr-surface`, `--ecr-sunken`, `--ecr-raised` |
| `--border / --border-strong / --grid-line` | `#DADDE6 / #B9BECD / #E3E5EC` | `#2E3346 / #4A516B / #272B3B` | `--mantine-color-default-border`; `--ecr-grid-line` (RevoGrid) |
| `--text / --muted / --faint` | `#1B1E2B / #5B6175 / #8B91A5` | `#E6E8F0 / #9AA1B5 / #727A92` | `--mantine-color-text`, `--mantine-color-dimmed`; `--ecr-faint` ⚠ `faint` — лише для неконтентного (роздільники, плейсхолдер): на `surface` це 3.3:1 |
| `--accent / --accent-text / --accent-soft / --select / --focus` | `#3B3FA8 / #3B3FA8 / #E7E8F8 / #EEEFFB / #5558C8` | `#5558C8 / #B4B7EA / #272B55 / #22264A / #8A8DE0` | випливає з `brand` + `primaryShade`; `--ecr-select` (виділення рядка/комірки), `focusRing` |
| `--success / --warning / --danger` (+`-soft`) | `#1E7F4F / #9A6700 / #B42318` | `#4CC38A / #E3B341 / #F97066` | звірити з чинними кортежами: **правий той, що проходить `contrast.test.ts`**. Не міняти чинні значення заради збігу з макетом, якщо макетне не проходить поріг |
| `--calc-bg / --hatch` | `#F3F4F8 / #E6E8EF` | `#1E2230 / #262A3A` | `cellState.calculated`, `cellState.closed` (штриховка) — через `cellStateMeasured`/`gridCellContrast` |
| `--fs-xs/sm/md/h3/h2` | 11/13/15/18/22 | | `fontSizes` xs/sm/md лишаються; `headings.sizes.h3=18`, `h2=22` |
| `--s1…s5`, `--r1/r2` | 4/8/12/16/24; 4/6 | | `spacing` збігається ✔; `defaultRadius: 'sm'` → значення `sm` = 4 (зараз 3 ◐) |
| `--row / --ctl / --rail-item` | 28 / 28 / 32 | comfortable: **36 / 36 / 40** (✎ 2026-09-19: було `36 / 32 / 36` — розбіжність із макетом, див. нижче) | `other.rowHeight*` ✔ + **реальне підключення** (крок UI-03) |
| `--sans / --mono` | IBM Plex Sans/Mono → Segoe UI → system-ui | | крок UI-02, рішення Q15-01a |

**Правило:** жодного літерала кольору в компонентах. Нове значення — спершу в
`theme.ts`, звідти через `cssVariablesResolver` у `--ecr-*`, і лише тоді в CSS.
Окремий `src/shared/theme/tokens.css` — **тільки** для того, чого Mantine не
бачить: змінні RevoGrid, щільність, штриховка закритої комірки.

### ✎ 2026-09-19: три виправлення цієї таблиці за фактом

1. **`comfortable` — `36 / 36 / 40`, не `36 / 32 / 36`.** Джерело —
   `docs/design/hybrid/index.html:22`:
   `:root[data-density="comfortable"]{--row:36px;--ctl:36px;--rail-item:40px}`.
   ⚠ Таблиця розходилася з макетом, на який `D15-01` сама ж і посилається як на
   еталон вигляду. У розбіжність упиралися **двічі** — на `UI-01` і на `UI-03`;
   обидва рази взято макет.
2. **`--faint` світла: «на `surface` це 3.3:1» — ні.** Виміряно **3.14** на
   чистому білому, і це найкращий випадок; на решті світлих поверхонь
   **2.58–2.93**, тобто токен макета провалює навіть м'який поріг 3:1. `UI-01`
   узяв `#7d8496` (3.08–3.49) — за правилом, яке задає сусідній рядок цієї ж
   таблиці: **правий той, що проходить `contrast.test.ts`**.
3. ⚠ **`shared/theme/density.ts` (§3, рядок `UI-03`) — файлу не заводили.**
   Чинний `shared/theme/preferences.ts` уже має `Density`, `density()`,
   `setDensity()`, `applyDensity()`, `rowHeight()`. Другий файл поруч дав би
   два механізми на одну річ.

⛔ Ширший висновок: **імена файлів і числа в цьому пакеті писалися до коду, а не
з коду.** За один день вони розійшлися з дійсністю чотири рази (`density.ts`,
`shared/api/schema.d.ts`, `Sql/09-seed.sql`, `src/Ecr.Api/Dto/`). Перед тим, як
спиратися на будь-який шлях звідси, — один `git grep`.

---

## 2. Набір компонентів: `KIT.md` → `src/shared/ui`

Три шари. Кожен рядок — файл + тест; нічого з цього не лягає у великі чинні файли.

### Шар 1 — без Mantine-залежностей від сторінки

| `KIT.md` | Файл | Нотатка |
|---|---|---|
| `fmt.*` (§6.2) | `shared/format/index.ts` | на `Intl.NumberFormat/DateTimeFormat/PluralRules`; локаль — з мови ПРОДУКТУ (`en/ru/kk`), не браузера. Вузький нерозривний пробіл у тисячах, `−` (U+2212), `—` для `null`. ⛔ `toLocaleString()` без локалі — ESLint `no-restricted-syntax` |
| `StatusBadge` (§6.7) | `shared/ui/StatusBadge.tsx` | таблиця `kind × state → tone` — **один об'єкт**, тест перебирає всі пари й вимагає рядок `status.<kind>.<state>` у каталозі. Іншого способу намалювати статус у застосунку не лишається |
| `CodeText`, `KeyValue`, `twoLine` | `shared/ui/CodeText.tsx`, `KeyValue.tsx`, `TwoLine.tsx` | `CodeText block` — з Copy; копіювання через `navigator.clipboard` у try/catch + тост |
| `Skeleton kind` | уже є в `AsyncBoundary` ✔ | лише вирівняти форму з макетом |

### Шар 2 — тонкі обгортки Mantine

| `KIT.md` | Реалізація | Нотатка |
|---|---|---|
| `PageHeader` (§6.4) | **розширити** чинний `shared/ui/PageHeader.tsx` | ⛔ не зламати: фокус заголовка після навігації й оголошення `RouteAnnouncer`. Додати `badge`, `count`, `meta`, `back`, `primary`, `secondary[≤2]`, `more[]`; >2 secondary — у меню |
| `Banner` / `ResultBanner` | `shared/ui/Banner.tsx` на `Alert` | `role="status"` для success, `role="alert"` для danger |
| `EmptyState` / `ErrorState` / `ForbiddenState` | **експортувати** з `AsyncBoundary.tsx` як окремі компоненти | `ErrorState` показує код + correlation id з Copy (`ErrorAlert` уже це вміє ◐ — переюзати) |
| `ConfirmDialog` | `shared/ui/ConfirmModal.tsx` | `data-autofocus` на Cancel; `consequences[]`; `typeToConfirm` |
| `ReasonDialog` | **розширити** чинний `ReasonModal.tsx` | `minLength`, кнопка вимкнена до валідності |
| `Drawer` | `shared/ui/DetailDrawer.tsx` на Mantine `Drawer` | `position="right"`, `withOverlay={false}` на ≥ 1200 px (сторінка лишається живою), з оверлеєм на вузьких; адреса — `?panel=` через чинний `useUrlState` ✔ |
| `Input/Select/Textarea/Checkbox/Switch/Field` | Mantine напряму + `@mantine/form` ✔ | власних обгорток НЕ робити. ⛔ `<input type="date">` заборонити ESLint-правилом: показує локаль браузера, не продукту. Лише `@mantine/dates` ✔ (вже встановлено) з локаллю продукту |
| `Popover`, `Menu`, `Segmented`, `Tabs`, `Collapsible`, `Progress`, `Stepper` | Mantine напряму | `Tabs` — стан у `?tab=` |
| `Toast` + Undo | розширити `notify.ts` | `notify.undo(message, onUndo, ms=8000)`; дія виконується ПІСЛЯ спливу таймера або одразу з компенсацією — вирішується на екрані, де сервер уміє «назад» (див. §6) |

### Шар 3 — складені

| `KIT.md` | Файл | Нотатка |
|---|---|---|
| `DataTable` (§6.5) | `shared/ui/DataTable/` | на Mantine `Table` + `ScrollArea`; закріплена шапка; сортування; «Showing N of M · Show more» поверх курсорної пагінації сервера; стани через `AsyncBoundary`. ⛔ не RevoGrid — той лише для сітки документа |
| `FilterBar` | `shared/ui/FilterBar.tsx` | значення — в URL (`useUrlState`), «Clear filters» з'являється сам |
| `StatStrip` | `shared/ui/StatStrip.tsx` | `items` ≤ 4; клац = фільтр; `aria-pressed` |
| `ListPage` (шаблон §3 `KIT.md`) | `shared/ui/ListPage.tsx` | `PageHeader + StatStrip? + FilterBar + DataTable + DetailDrawer` — 12 із 20 екранів збираються з нього |
| `Wizard` | `shared/ui/Wizard.tsx` | крок Review додається сам; `validate` → банер у кроці; стан кроків — локальний, НЕ в URL (крім `?dialog=`) |
| `UnsavedGuard` | `shared/ui/UnsavedGuard.tsx` + `useBlocker` | це `D14-12` з №14 (pendingStore рівня документа) — **якщо ще не зроблено, робиться тут, у кроці UI-00** |
| `tasks` («My tasks») | `features/tasks/` | на `GET /jobs?mine=true` (BE-08) + `recalculationJobId` (BE-05); до BE-08 — лише задачі, поставлені в цій вкладці (ідентифікатори з відповідей `202`) |
| `SegmentBar`, `PeriodPicker` | `shared/ui/SegmentBar.tsx`, `PeriodPicker.tsx` | `PeriodPicker` — місяць/квартал/рік за типом періоду шаблону; значення в URL, переживає навігацію (перевірено на макеті) |
| Командна палітра | ~~`features/palette/` на `@mantine/spotlight` | пакет **не встановлено** ◐ → ставить foundation-PR (lock-файл — спільний ресурс). Навігація й дії — клієнтські; пошук даних — BE-19~~ ✎ 2026-09-22: реалізовано як `features/search/DataSearchPalette.tsx`, власна побудова на `Modal`+`TextInput` з `@mantine/core` (не на `@mantine/spotlight` — дві реалізації пошуку зайві, пакет прибрано з `package.json`); дані — `GET /api/v1/search` (BE-19) з обробкою 429. Навігація й дії — клієнтські |

---

## 3. Порядок: кроки UI-00 … UI-10

Формат рядка плану — той, що вимагає CLAUDE.md §1. «Сторожі» — що впаде, якщо
зробити неакуратно; їх ганяти ДО пушу повним набором (`npm test`, не лише своя
тека — пам'ятка `full-suite-before-push`).

| # | Підзадача | Файли для запису | Залежить | DoD | Режим |
|---|---|---|---|---|---|
| UI-00 | Паски безпеки: `MutationCache.onError` → `notify`; `pendingStore` + `useBlocker` + `UnsavedGuard`; знімки-еталони e2e чинних екранів ДО змін | `app/queryClient.ts`, `shared/ui/UnsavedGuard.tsx`, `features/grid/pendingStore.ts`, `e2e/screenshots.spec.ts` | — | перехід із незбереженими правками питає; тест падає без `useBlocker` | послідовно |
| UI-01 | Токени: новий `brand`, поверхні, `cssVariablesResolver`, `tokens.css` | `shared/theme/theme.ts`, `shared/theme/tokens.css`, `shared/theme/cssVariables.ts`, `main.tsx` (1 рядок) | UI-00 | `contrast.test`, `tokens.test`, `cellStateMeasured`, `gridCellContrast`, `cssCascade`, `a11y (dark)`, `a11y (light)` зелені; **вигляд змінюється, розмітка — ні** | послідовно |
| UI-02 | Шрифти (за рішенням Q15-01a). Дефолт: стек лишається системним, у `theme.fontFamily` лише порядок; `@fontsource/ibm-plex-*` НЕ ставити до відповіді | `shared/theme/theme.ts` | UI-01 | `tokens.test.ts:64-71` (вимагає `system-ui` у стеку) зелений | послідовно |
| UI-03 | Щільність насправді: `--ecr-row-height/--ecr-ctl-height` читають `DataTable` і RevoGrid; перемикач у меню Display; значення в `localStorage` (далі — BE-20) | `shared/theme/density.ts`, `tokens.css`, `features/grid/DocumentGrid.tsx` (лише `rowSize`/`theme`) | UI-01 | тест: перемикання змінює обчислену висоту рядка сітки й таблиці; `renderFeedback` лічильники комітів не зросли | послідовно ⚠ `DocumentGrid.tsx` — гарячий файл №14, звір `git worktree list` |
| UI-04 | Набір, шар 1 | нові файли `shared/format/**`, `shared/ui/StatusBadge.tsx`, `CodeText.tsx`, `KeyValue.tsx`, `TwoLine.tsx` | UI-01 | тести кожного; рядки статусів у сіді | паралельно з UI-05 |
| UI-05 | Набір, шар 2 | `shared/ui/PageHeader.tsx`, `Banner.tsx`, `ConfirmModal.tsx`, `ReasonModal.tsx`, `DetailDrawer.tsx`, `AsyncBoundary.tsx`, `notify.ts` | UI-01 | чинні тести `PageHeader`/`AsyncBoundary`/`RouteAnnouncer` зелені без правок; L6 доведено тестом | паралельно з UI-04 |
| UI-06 | Набір, шар 3 (2–3 PR: `DataTable`+`FilterBar`; `StatStrip`+`ListPage`; `Wizard`) | нові теки `shared/ui/DataTable/**` тощо | UI-04, UI-05 | `KitchenSinkPage` показує кожен компонент у всіх станах; a11y обох тем на ній зелений | послідовно всередині, паралельно з UI-07 |
| UI-07 | Оболонка: рейка з групами (нове поле `handle.group` у `routes.ts`), шапка (проєкт · період · пошук · My tasks · користувач), меню Display, палітра | `app/routes.ts`, `app/AppLayout.tsx`, `app/navIcons.tsx`, `features/palette/**`, `features/tasks/**`, `package.json`+lock (**foundation-коміт першим**) | UI-05 | `SinglePageApplicationTests` (regex по відступах `routes.ts`!) зелений; skip-link і `RouteAnnouncer` працюють; `keyboardPath.spec.ts` проходить | послідовно |
| UI-08 | Сітка документа: закріплена колонка назв рядків, одиниця другим рядком шапки, рядок підсумків, рядок формули, статус-рядок (збережено / перераховується / конфлікт) | `features/grid/**` — **новими файлами** (`GridFormulaBar.tsx`, `GridStatusBar.tsx`, `gridColumns.ts`), у `DocumentGrid.tsx` лише підключення | UI-03, BE-05 | `cellStates.spec.ts` (`data-measure*`), `renderFeedback`, `MS-01` — без регресу; лічильник рендерів на PATCH не зріс | послідовно, **після** закриття Е2–Е3 №14 по сітці |
| UI-09 | Екрани — по одному PR (перелік §4) | сторінка + її `features/<x>` | UI-06, UI-07, свої `BE-xx` | §5 «DoD екрана» | паралельно ≤ 3–4, без перетину файлів |
| UI-10 | Прибирання: `KitchenSinkPage` → галерея набору; мертві стилі; оновити `UI-WALKTHROUGH.md` і знімки | — | UI-09 | `git grep` старих класів порожній | послідовно |

⚠ **UI-01 — найризикованіший крок за кількістю сторожів.** Контраст рахувати, а
не дивитися: для кожної нової пари «текст/тло» — рядок у `contrast.test.ts`
(поріг 4.5 для тексту, 3 для кільця фокуса й меж контролів). Макетні значення
пройшли мою перевірку очима на двох темах, але **гейт — тест, не я**.

---

## 4. Екрани: що на що переноситься

`Адреса макета` відкривається як `http://127.0.0.1:5197/#<адреса>`; стани —
у `routes/*.txt`. «Блокує» — без цього `BE` екран не виходить; «прикраса» —
екран виходить без елемента (⛔ не заглушкою з фейковим числом).

| Екран | Файл (рядків) | Адреса макета | Нові маршрути | BE блокує | BE прикраса |
|---|---|---|---|---|---|
| Вхід | `pages/LoginPage.tsx` (221) | `/login` | — | — | BE-07 |
| Зміна пароля | `pages/ChangePasswordPage.tsx` (114) | `/change-password` | — | — | BE-07 (політика) |
| Мої групи й права | `pages/MyGroupsPage.tsx` (112) | `/my-groups` | — | — | — |
| Задачі | `pages/admin/JobsPage.tsx` (196) | `/admin/jobs` | — | BE-02 | BE-08 |
| Джерела | `pages/admin/SourcesPage.tsx` (163) | `/admin/sources` | — | — (лише перегляд) | BE-21 |
| Журнал | `pages/admin/AuditPage.tsx` (169) | `/admin/audit` | — | BE-03 | BE-16 |
| Узгодженість | `pages/admin/ConsistencyIssuesPage.tsx` (181) | `/admin/consistency` | — | — | ~~BE-30 (Q15-03)~~ ✎ **2026-09-21:** відповідь на Q15-03 є (рішення 2: Acknowledge знято); «Run check now» зроблено — `ConsistencyController.cs:86` `POST /consistency/run`, споживач `features/jobs/api.ts:92`, кнопка на `ConsistencyIssuesPage.tsx` (`RunConsistencyPermission`) |
| Стан системи | `pages/admin/HealthPage.tsx` (183) | `/admin/health` | — | — | BE-18 |
| Документи | `pages/DocumentsPage.tsx` (190) | `/` | — | BE-09 (смуга) | швидкий перегляд (`docs-quicklook`) — BE-10 |
| Мапінг | `pages/admin/MappingPreviewPage.tsx` (130) | `/admin/mapping` | — | — | BE-27 |
| Зв'язки таблиць | `pages/admin/TableRelationsPage.tsx` (189) | `/admin/templates/:id/versions/:v/relations` | — | — | — |
| Вирази | `pages/admin/ExpressionsPage.tsx` (242) | `/admin/expressions` | — | — | BE-15 |
| Шаблони | `pages/admin/TemplatesPage.tsx` (251) | `/admin/templates` | **`/admin/templates/:id`** стає сторінкою (зараз синтетичний вузол, `routes.ts:168` ✔) | — | BE-26 |
| Одиниці | `pages/admin/UnitsPage.tsx` (276) | `/admin/units` | — | — | BE-15 |
| Визначення довідника | `pages/admin/RegistryConstructorPage.tsx` (274) | `/admin/registries/:code/definition` | — | — | BE-24 |
| Знімки | `pages/admin/SnapshotsPage.tsx` (306) | `/admin/snapshots` | — | — | BE-17 |
| Рядки інтерфейсу | `pages/admin/UiStringsPage.tsx` (320) | `/admin/ui-strings` | — | — | BE-13 |
| Довідники | `pages/admin/RegistriesPage.tsx` (326) | `/admin/registries` | **`/admin/registries/:code/entries`** | BE-01 | BE-24 |
| **Документ** | `pages/DocumentPage.tsx` (426) + `features/grid/**` | `/documents/DOC-000001` | — | BE-04, BE-05, BE-06 | BE-10, BE-11, BE-03 (History комірки) |
| Методики | `pages/admin/MethodologiesPage.tsx` (469) | `/admin/methodologies` | — | — | BE-25 |
| Безпека | `pages/admin/SecurityPage.tsx` (644) | `/admin/security` | — | — | BE-12, BE-14 |
| Періоди | `pages/admin/PeriodsPage.tsx` (730) | `/admin/periods` | — | — (⚠ Q15-02) | BE-22, BE-29 |
| Версії методики | `pages/admin/MethodologyVersionsPage.tsx` (778) + `MethodologyContentPanels.tsx` (1385) | `/admin/methodologies/:id/versions` | — | — | BE-25 |
| Конструктор версії | `pages/admin/TemplateVersionPage.tsx` (1127) | `/admin/templates/:id/versions/:v` | **`…/compare`**, **`…/access-matrix`**, **`…/period-rules`** (зараз — частини сторінки ◐) | — | ~~BE-23,~~ BE-26 ✎ **2026-09-21:** `BE-23` знято рішенням 4 (`DIRECTIVE-15-DECISIONS.md`), право `Template.Migrate` видалено із сіду (`09-seed.sql:41-42`) |
| 403 / 404 / збій | `app/NotFoundPage.tsx`, `RouteErrorPage.tsx`, `RenderErrorScreen.tsx` | `/403`, `/404`, `/error` | `/403` (зараз немає ◐) | — | — |

**Порядок UI-09** — від дешевих до дорогих, щоб набір обкатався на простому:
Задачі → Джерела → Журнал → Узгодженість → Стан системи → Мої групи → Вхід/пароль →
**Документи** → Мапінг → Вирази → Одиниці → Шаблони → Знімки → Рядки →
Довідники (+записи) → **Документ** → Методики → Безпека → Періоди → Версії
методики → Конструктор версії (останнім; спершу окремим PR-рефакторингом
розрізати 1127 рядків на файли **без зміни поведінки** — CLAUDE.md §4:
рефакторинг ніколи не разом із функціональною зміною).

⚠ Розбіжності макета з бекендом, які інтерфейс **не малює як у макеті** —
таблиця в `DIRECTIVE-15.md` §4. Коротко: гранти редагуються **набором на роль**;
«Create partitions» → «Copy command for DBA»; «Purge jobs», лічильник спроб
входу — не переносяться; ~~ручні кнопки періоду, Acknowledge, Recall, Migrate —
лише після відповіді людини.~~ ✎ **2026-09-21:** відповіді є
(`DIRECTIVE-15-DECISIONS.md` §1): ручні кнопки періоду — лише перевідкриття
(рішення 1; `PeriodsPage.tsx:366` → `POST /periods/{id}/reopen`); Acknowledge
— не переноситься (рішення 2); Recall — зроблено (рішення 3;
`DocumentWorkflowHistoryController.cs:37`, кнопка в
`features/workflow/SheetActions.tsx:232`); Migrate — не переноситься
(рішення 4).

---

## 5. DoD одного екрана (кожен PR UI-09)

1. Зібраний із набору; **нуль** власних кольорів/відступів літералами.
2. Усі стани з `routes/*.txt` для цієї адреси: `data / loading / empty / error /
   no-access / filter-nothing` + кожен діалог/шухляда. Стани діалогів — в URL
   (`?dialog=`, `?panel=`, `?tab=`) — їх можна відкрити посиланням і зняти e2e.
3. Правила L1–L10 (§0) — тестами там, де вказано.
4. Рядки — лише ключами каталогу; нові ключі — у `09-seed.sql` (`en`, без
   кирилиці в значеннях); множина — `formatCount` (див. §6).
5. a11y: файл екрана в `a11y/*.test.tsx` для обох тем, `a11yFixtures.emptyBodyFor`
   доповнено; клавіатурний шлях: Tab-порядок, Esc закриває верхній шар, фокус
   повертається на елемент, що відкрив.
6. `RequirementTraceTests` читає заголовки `it('ФВ-14.x: …')` — **не
   перейменовувати** чинні тести екрана, лише доповнювати.
7. e2e-знімок екрана в `screenshots.spec.ts`, світла й темна; PR містить
   «до/після» поруч з адресою макета.
8. Бюджет бандла — без зростання основного чанка (екран і так lazy-маршрут).
9. `tools/e2e-stand.ps1` локально з окремого worktree — зелений (це `ci-exempt`,
   CI його не жене; див. CLAUDE.md).
10. Мутаційний доказ хоча б для одного нового тесту названо в описі PR.

---

## 6. Рішення по дрібницях, щоб не зупинятися

| Тема | Рішення |
|---|---|
| Множина | `shared/i18n/formatCount(key, n)` → `Intl.PluralRules(мова продукту)` → ключ `key.one / key.few / key.many / key.other`, fallback `key.other`. Для `en` досить `.one/.other`; `ru` отримає `.few/.many`, коли з'явиться переклад. Сід: лише `en` |
| Обсяг нових рядків | ≈ 500–900 ключів. Додаються **разом з екраном**, не одним PR наперед: `09-seed.sql` — спільний файл, додавання лише в кінець секції екрана (append-only, CLAUDE.md §2) |
| Undo | лише там, де сервер уміє «назад» однією дією (зняти грант ↔ видати; закрити дату ↔ відкрити). Де не вміє (видалення запису — soft delete без «restore» ◐) — `ConfirmDialog`, без Undo. ⛔ Undo, який мовчки не спрацьовує, гірший за його відсутність |
| Після Retry задачі | рядок задачі одразу стає `Queued` + тост «Restarted — follow in My tasks» (на макеті результат був малопомітний — зауваження фінальної перевірки) |
| `?state=`, `?as=`, `/flows` | **лише макет**, у застосунок не переносяться. «View as» у застосунку — це чинна симуляція (`/security/simulation` ✔) з банером `sim-banner-*` |
| Вузький екран | оболонка: рейка згортається в іконки < 1100 px, у шухляду < 768 px. Сітка документа на телефоні — читання, не редагування (банер `docs-narrow-note`) |
| Темна тема за замовчуванням | як зараз: слідує системі; вибір у меню Display |
| Логотип | чинний `BrandMark` ✔ не чіпати (логотип замовника — факт, Q15-01b) |

---

## 7. Контракт саб-агента для екрана (шаблон запуску)

```
Задача: перенести екран <назва> (крок UI-09) за docs/build/DIRECTIVE-15-FRONTEND.md §4–5.
Еталон: docs/design/hybrid/ → адреса <…>; стани — рядки з routes/<файл>.txt, що містять цю адресу.
Дозволені файли: src/Ecr.Web/src/pages/<…>Page.tsx, src/Ecr.Web/src/features/<x>/**,
  src/Ecr.Web/src/a11y/<екран>.test.tsx, e2e/screenshots.spec.ts (лише додати блок),
  src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql (лише додати рядки в кінець секції <екран>).
Заборонено: shared/ui/**, shared/theme/**, app/**, package.json, lock, інші екрани, міграції, порти 5080/5099/5197.
Вихід: змінені файли; `npm test` — повний, з числами; `npx tsc --noEmit`; `npm run lint`; припущення; відкриті питання.
Стоп-умова: бракує компонента в наборі або поля в API → зупинись і поверни питання. Не малюй заглушку.
```

⚠ Пам'ятка `subagents-need-write-guard`: «READ-ONLY» у промпті не спрацював —
давай агентові **окремий worktree**, а не `H:\ECR`.

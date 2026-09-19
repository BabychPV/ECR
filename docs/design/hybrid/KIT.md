# ECR Hybrid — KIT

Єдине джерело правди для авторів екранів. **`kit.js` та `index.html` читати не потрібно і змінювати заборонено.**
Твій файл — рівно один із: `screens-work.js`, `screens-templates.js`, `screens-data.js`, `screens-ops.js`. Заміни заглушку повністю.
Зразок «як це виглядає в коді» — `screen-document.js` (екран-редактор) і заглушки (мінімальний екран).

Гібрид = основа A «Workbench» + статистика з B «Control Room». Головна вимога замовника: **екран не має навантажувати користувача**.

---

## 0. Сім речей, які ламають усе (прочитай першими)

1. **Фабрики повертають DOM-вузли, не рядки.** `el.appendChild(ECR.Button({...}))` — так. `el.innerHTML = ECR.Button({...})` — дасть `[object HTMLButtonElement]`. Збирай розмітку через `ECR.h(tag, attrs, ...children)`. Рядок як дитина `h()` — це ТЕКСТ (екранується сам).
2. **`Dialog`, `Drawer`, `ConfirmDialog`, `ReasonDialog`, `Wizard`, `ResultDialog` відкриваються ОДРАЗУ при виклику.** Не викликай їх під час `render` «про запас» — лише всередині функції-відкривача, яку реєструєш через `ctx.dialog(id, fn)` / `ctx.panel(id, fn)`.
3. **Кожен діалог і кожна шторка мають бути адресовані:** `ctx.dialog('new-role', openNewRole)`, а кнопка викликає `ctx.openDialog('new-role')` (НЕ `openNewRole()` напряму) — інакше `?dialog=new-role` в адресі не спрацює і екран не потрапить в автоматичні знімки.
4. **Стан подання бери з `ctx.state`** (`data | empty | error | loading`) і передавай у `DataTable({state: ctx.state})` / `ListPage` / `StateSwitch`. Не вигадуй власних перемикачів.
5. **Жодного кольору-літерала, жодного `px` поза шкалою.** Лише токени `var(--…)`, відступи лише `--s1…--s5` (4/8/12/16/24), радіуси лише `--r1`/`--r2` (4/6). `ECR.css()` друкує попередження в консоль, якщо бачить `#hex`/`rgb(`.
6. **Дані лише з `ECR.data`.** Потрібне нове — додай у СВОЄМУ файлі похідну від `ECR.data` (детерміновано, через `ECR.rng(seed)`), не вигадуй інших імен користувачів, документів, проєктів.
7. **Кожне поле форми — стабільний `id`** з префіксом екрана (`sec-role-name`), підпис через `label` (не placeholder), помилка через `field.setError('…')`.

---

## 1. Принцип №1 — низьке когнітивне навантаження (вимога замовника, дослівно)

1. **Спокій за замовчуванням.** Інспектор ЗАКРИТИЙ, доки користувач не клікне лічильник помилок чи «History». Навігатор таблиць відкритий, але показує лише поточний аркуш; групи, крім поточної, згорнуті. Рейка — лише іконки (підписи в тултіпах), розгортається вручну.
2. **Одна головна дія на екран**, ≤ 2 другорядні поруч, решта — у меню «More». Жодних рядів із 5–6 кнопок.
3. **Колір — лише там, де щось не так.** Нормальний стан нейтральний (сірий текст/крапка), кольором і формою виділяються тільки проблеми й те, що чекає дії. Фірмовий синій — лише головна дія, активний пункт, фокус, виділення.
4. **Прогресивне розкриття.** Рідкісні налаштування — у згорнутих секціях (`Collapsible`); подробиці запису — у правій шторці (`Drawer`), а не на новій сторінці, щоб не губити контекст.
5. **Один шаблон сторінки** для всіх переліків: PageHeader (назад → заголовок → один рядок пояснення людською мовою → головна дія) → StatStrip (за потреби) → рядок фільтрів → таблиця → шторка подробиць. Крихти живуть у верхній смузі (їх малює оболонка), у PageHeader — лише «← Back to …».
6. **StatStrip із B — приглушено:** ОДНА смуга, ≤ 4 показники, дрібний підпис + число моноширинним, без плиток, рамок і кольорових фонів; показник клікабельний і фільтрує перелік під ним; колір лише в показника-проблеми (напр. «7 validation errors»). На екранах-редакторах (документ, конструктор, редактор виразів) StatStrip НЕ ставиться.
7. **Людські підписи замість кодів** (код — приглушено другим рядком через `ECR.twoLine()` або в `title`). Таблиці переліків — ≤ 7 колонок, решта — у шторці.
8. **Кожне подання має чотири стани:** дані / порожньо (пояснення + дія) / помилка (код, correlation id, Retry) / завантаження (скелет). Спільний перемикач — кнопка «Prototype» або `?state=`.
9. **Переходи пропрацьовані** (доповнення замовника): де є потреба — проміжний екран або спливаюче вікно. Багатокрокове — `Wizard` із підсумком перед застосуванням; вихід із незбереженим — `UnsavedGuard`; після дії — `ResultBanner` «що далі»; дрібне підтвердження — `InlineConfirm`; подробиця без зміни екрана — `Popover`; після створення — перехід на об'єкт із тостом «Created»; після видалення — повернення до переліку з тостом і Undo (де це безпечно).

Тони: `info` (синій) = «чекає дії», `warn` = увага, `bad`/`danger` = проблема, решта — нейтрально. **Зелений не вживається для «все гаразд»** — лише в `ResultBanner` успіху одразу після дії.

---

## 2. Як влаштовано файл екранів

```js
/* screens-ops.js */
(function (E) {
  'use strict';
  var h = E.h, D = E.data, F = E.fmt;

  E.css('ops', '\
    .ops-health-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:var(--s3)}\
    @media (max-width:640px){.ops-health-grid{grid-template-columns:1fr}}');

  E.screen('/admin/jobs', { title: 'Jobs', group: 'Operate', icon: 'stack', order: 1,
    render: function (el, ctx) { /* … */ } });

  E.flow('ops-job-retry', { group: 'Operations', title: '…', actor: '…', steps: [{ label: '…', href: '#/admin/jobs?panel=J-10427' }] });
})(window.ECR);
```

### 2.1 `ECR.screen(path, def)`

| поле | значення |
|---|---|
| `path` | `/admin/jobs`; параметри `:id` → `ctx.params.id`. Повторна реєстрація того самого шляху замінює попередню. |
| `title` | назва в рейці, крихтах, палітрі, `document.title` |
| `group` | `'Work' \| 'Configure' \| 'Access' \| 'Operate'` — лише для екранів, що мають бути в рейці. Без `group` екран у рейку не потрапляє. Шляхи з `:param` у рейку не потрапляють ніколи. |
| `icon` | ім'я іконки (§6) |
| `order` | порядок у групі (1, 2, 3…) |
| `navPath` | для вкладених екранів — який пункт рейки підсвітити: `navPath:'/admin/templates'` |
| `example` | для шляхів із параметрами — конкретна адреса для перемикача «Prototype»: `example:'/admin/templates/GEN07131100'` |
| `crumbs` | масив `[{label, href?}]` або `function(ctx)`; останній елемент — поточна сторінка. Без поля: `group › title`. Динамічно: `ctx.setCrumbs([...])`. |
| `chrome` | `false` — без верхньої смуги й рейки (login, change-password) |
| `palette` | `true` — показувати в командній палітрі, навіть якщо немає `group` |
| `render(el, ctx)` | `el` — порожній `<section class="view">` (flex-колонка на всю висоту). Викликається наново при КОЖНІЙ зміні адреси ззовні (клік по посиланню, назад/вперед, перемикач стану). Тому стан, який має пережити перемальовування, тримай у `ctx.saved` або в змінній модуля. |

### 2.2 `ctx`

| | |
|---|---|
| `ctx.path`, `ctx.params`, `ctx.query` | розібрана адреса; `ctx.query` — об'єкт усіх параметрів |
| `ctx.state` | `'data' \| 'empty' \| 'error' \| 'loading'` |
| `ctx.from` | `{label, href}` — звідки прийшли (екран пам'ятає сам), або `null` |
| `ctx.saved` | об'єкт, що живе весь сеанс для цього шляху: фільтри, сортування, прокрутка, вибрана вкладка. `FilterBar`/`DataTable`/`ListPage` пишуть туди самі, якщо передати `memory: ctx.saved`. |
| `ctx.me`, `ctx.role` | поточний користувач (`ECR.me()`), роль (`'DataEntry' \| 'Approver' \| 'SystemAdministrator'`); `ECR.can('Document.Approve')` → bool. Роль міняється в «Prototype» або `?as=Approver`. |
| `ctx.dialog(id, fn)` | зареєструвати діалог під `?dialog=id` |
| `ctx.openDialog(id, arg?)` | відкрити зареєстрований діалог І записати `?dialog=id` в адресу. Закриття прибирає параметр саме. |
| `ctx.panel(id, fn)` / `ctx.panel('*', fn(id))` | зареєструвати шторку під `?panel=id`; `'*'` — будь-який id (ключ рядка). `ctx.openPanel(id)`. |
| `ctx.setQuery({tab:'grants', dialog:null})` | тихо змінити параметри адреси БЕЗ перемальовування (null — прибрати) |
| `ctx.action(label, icon, fn)` | дія екрана для командної палітри Ctrl K |
| `ctx.proto({id, label, options, value, onChange})` або `{id, label, buttons:[{label,onClick}]}` | власний перемикач у панелі «Prototype» |
| `ctx.hasUnsaved(fn, {count, text, onSave, onDiscard})` | екран із незбереженим станом: маршрутизатор сам покаже `UnsavedGuard` при спробі піти. `fn()` → bool. |
| `ctx.onLeave(fn)` | прибирання (таймери) перед виходом з екрана |
| `ctx.setTitle(t)`, `ctx.setCrumbs(list)`, `ctx.refresh()` | |

### 2.3 Параметри адреси (усі працюють з URL — ними знімають кожен екран автоматично)

`#/admin/jobs?theme=dark&density=comfortable&state=empty&dialog=retry-job&panel=J-10427&tab=failed&as=Approver`

| параметр | хто обробляє |
|---|---|
| `theme=light\|dark`, `density=compact\|comfortable`, `as=<Role>` | оболонка |
| `state=data\|empty\|error\|loading` | оболонка → `ctx.state` → ти передаєш у таблицю/StateSwitch |
| `dialog=<id>` | оболонка викликає твою `fn` з `ctx.dialog(id, fn)` після `render` |
| `panel=<id>` | твоя `fn` з `ctx.panel(id, fn)` або `ctx.panel('*', fn)`. Глобальні: `tasks`, `notes`. |
| `tab=<id>` | `ECR.Tabs({ctx: ctx, …})` читає і пише сам |
| глобальні діалоги | `dialog=command-palette`, `dialog=unsaved-guard` |

### 2.4 Навігація і переходи

```js
E.go('/admin/templates/T-9');                                   // перехід
E.go('/admin/templates/T-9', { toast: 'Template created' });    // після створення: перехід на об'єкт + тост
E.go('/admin/templates', { toast: { text: 'Draft deleted', action: { label: 'Undo', onClick: restore } } }); // після видалення
E.go('/login', { force: true, params: { 'return': ctx.path } }); // force — оминути UnsavedGuard
E.back('#/admin/templates');                                    // туди, звідки прийшли; аргумент — запасна адреса
E.href('/admin/jobs', { panel: 'J-10427' })                     // → '#/admin/jobs?panel=J-10427' для href
```

- Звичайні посилання `h('a', {href:'#/admin/jobs'}, 'Jobs')` працюють. «Звідки прийшли» запам'ятовується автоматично; `PageHeader({ctx})` сам покаже «← Back to Jobs». Для вкладених сторінок задай запасний варіант: `back:{label:'Templates', href:'#/admin/templates'}`.
- Повернення до переліку відновлює фільтри/сортування/прокрутку, ЯКЩО ти передав `memory: ctx.saved` (у `ListPage` — автоматично).
- `/login` мусить шанувати `?return=<шлях>`: після входу `E.go(ctx.query['return'] || '/', {force:true})`. На це спирається сценарій «сеанс сплив → вхід → Restore 3 unsaved changes».

### 2.5 `ECR.flow(id, def)` — ОБОВ'ЯЗКОВО для кожного файлу

Екран `/flows` (карта переходів) будується з реєстру. Кожен користувацький сценарій твого розділу — ланцюжок кроків-посилань, яким замовник проходить шлях клік за кліком:

```js
E.flow('ops-period-open-close', { group: 'Access', title: 'Close a period and open the next one', actor: 'Period administrator',
  note: 'необов’язкова примітка',
  steps: [ { label: 'Periods', href: '#/admin/periods' },
           { label: 'Close September (consequences)', href: '#/admin/periods?dialog=close-period' },
           { label: 'Result: closed · what next', href: '#/admin/periods?dialog=result-closed' } ] });
```

Кожен `href` мусить реально відкривати саме той стан (екран + діалог/шторка/вкладка). Проміжні результати, які не є діалогами (банер «Submitted»), теж реєструй як `ctx.dialog('result-…', fn)`, де `fn` просто показує банер. У заглушці твого файлу вже є чернетки сценаріїв з очікуваними `dialog`-ідентифікаторами — збережи ці id або онови сценарій.

---

## 3. Шаблон сторінки-переліку (готовий фрагмент)

`ECR.ListPage` збирає весь шаблон: заголовок, StatStrip, фільтри, таблицю, чотири стани, шторку під `?panel=<ключ рядка>`, пам'ять фільтрів.

```js
E.screen('/admin/jobs', { title: 'Jobs', group: 'Operate', icon: 'stack', order: 1, render: function (el, ctx) {
  var rows = ctx.state === 'empty' ? [] : D.jobs;
  var list = E.ListPage(el, ctx, {
    id: 'jobs',                                     // префікс усіх id на сторінці
    rows: rows,
    header: { title: 'Jobs',
      subtitle: 'Exports, imports and recalculations that run in the background. Failed ones say why and can be retried.',
      primary: null,                                // ОДНА головна дія: {label, icon, onClick}; може не бути
      secondary: [{ label: 'Refresh', icon: 'refresh', onClick: function () { ctx.refresh(); } }],   // ≤ 2
      more: [{ label: 'Purge finished…', icon: 'trash', danger: true, onClick: function () { ctx.openDialog('purge-jobs'); } }] },
    stats: [                                        // ≤ 4; match → показник клікабельний і фільтрує
      { id: 'running', label: 'running', match: function (j) { return j.state === 'Running'; } },
      { id: 'queued',  label: 'queued',  match: function (j) { return j.state === 'Queued'; } },
      { id: 'failed',  label: 'failed today', tone: 'danger', match: function (j) { return j.state === 'Failed'; } } ],
    search: { placeholder: 'Job, document or user', text: function (j) { return j.id + ' ' + j.type + ' ' + j.target + ' ' + j.by; } },
    filters: [ { id: 'type', label: 'Types', options: ['Excel export', 'Excel import', 'Formula recalculation'] } ],  // ≤ 3; збіг за row[id] або власний match(row, value)
    table: { rowKey: 'id', pageSize: 25, tall: true,
      columns: [                                    // ≤ 7
        { key: 'type', label: 'Job', render: function (j) { return E.twoLine(j.type, j.id, { mono: false }); } },
        { key: 'target', label: 'Target' },
        { key: 'state', label: 'State', render: function (j) { return E.StatusBadge('job', j.state); } },
        { key: 'progress', label: 'Progress', sortable: false, render: function (j) { return j.state === 'Running' ? E.Progress({ value: j.progress, tone: 'accent' }) : null; } },
        { key: 'started', label: 'Started', mono: true },
        { key: 'duration', label: 'Duration', num: true, render: function (j) { return F.duration(j.duration); } },
        { key: 'by', label: 'Started by', hideSm: true } ] },
    empty: { icon: 'stack', title: 'No background jobs yet', text: 'Exports, imports and recalculations appear here once someone starts them.', actions: [{ label: 'Open documents', href: '#/' }] },
    error: { code: 'ECR-JOB-0503' },
    drawer: function (j, api) { return {            // подробиці — у шторці; адреса стає ?panel=J-10427
      title: j.type, subtitle: j.id + ' · ' + j.target, badge: E.StatusBadge('job', j.state),
      body: function (b) {
        if (j.error) b.appendChild(E.Banner({ tone: 'danger', title: j.error, text: j.code + ' · ' + j.correlationId }));
        b.appendChild(E.KeyValue([{ label: 'Started by', value: j.by }, { label: 'Started', value: j.started, mono: true }, { label: 'Attempts', value: String(j.attempts) }]));
      },
      footer: j.state === 'Failed' ? [{ label: 'Retry', icon: 'refresh', variant: 'primary', keepOpen: true, onClick: function () { ctx.openDialog('retry-job', j); } }] : [{ label: 'Close' }] }; }
  });
  ctx.dialog('retry-job', function (j) { j = j || D.jobs[1]; /* E.ConfirmDialog({...}) */ });
} });
```

`ListPage` повертає `{page, table, filters, stats, setRows(rows), refresh(), openRow(row)}`. Після зміни даних (створив/видалив запис) — `list.setRows(newRows)`.
Якщо клік по рядку має вести на ІНШУ сторінку, а не у шторку: замість `drawer` дай `onRow: function (row) { E.go('/admin/templates/' + row.id); }`.

Сторінка без таблиці (Health) — той самий каркас вручну:

```js
var page = h('div', { class: 'page' });            // .page — прокручується; .page.page-fill — таблиця забирає висоту
el.appendChild(page);
page.appendChild(E.PageHeader({ ctx: ctx, id: 'health', title: 'Health', subtitle: '…', badge: E.StatusBadge('health', D.health.overall), primary: { label: 'Check now', icon: 'refresh', onClick: recheck } }));
page.appendChild(E.StateSwitch(ctx.state, { loading: { kind: 'form', rows: 6 }, error: { code: 'ECR-SYS-0503' }, empty: { title: '…', text: '…' },
  data: function () { return h('div', { class: 'stack gap-4' }, /* секції */); } }));
```

## 4. Шаблон сторінки-редактора (конструктор, редактор виразів, визначення довідника)

Без StatStrip. Шапка `editor-head` (заголовок + контекстний рядок + дії), під нею `split`: ліворуч список/дерево (`.side`, 280px), по центру робоча область (`.main`), праворуч — ЗАКРИТИЙ за замовчуванням інспектор (`.aside`, 320px, `hidden`).

```js
render: function (el, ctx) {
  var dirty = 0;
  var actions = h('div', { class: 'eh-actions' },
    E.Button({ label: 'Check', icon: 'checkCircle', id: 'tv-check', onClick: check }),
    E.Menu({ label: 'More', id: 'tv-more', items: [{ label: 'Clone version…', icon: 'copy', onClick: function () { ctx.openDialog('clone-version'); } }, { sep: true }, { label: 'Delete draft…', icon: 'trash', danger: true, onClick: function () { ctx.openDialog('delete-draft'); } }] }),
    E.Button({ label: 'Publish…', icon: 'send', variant: 'primary', id: 'tv-publish', onClick: function () { ctx.openDialog('publish-version'); } }));
  var back = ctx.from || { label: 'Template', href: '#/admin/templates/' + ctx.params.id };
  el.appendChild(h('div', { class: 'editor-head' },
    h('div', { class: 'eh-title' }, h('a', { class: 'ph-back', href: back.href, 'data-back': '1' }, E.icon('arrowL', 14), 'Back to ' + back.label),
      h('h1', { tabindex: '-1', 'data-page-title': '1', id: 'tv-h1' }, 'GEN07131100 · v1.1.0'), h('span', { class: 'muted' }, 'General environmental report — CPF')),
    h('div', { class: 'eh-ctx' }, E.StatusBadge('version', 'Draft'), E.Stepper({ steps: ['Draft', 'Published', 'Archived'], current: 0 }), h('span', { class: 'muted', id: 'tv-save', role: 'status' }, 'Saved 14:03'), actions)));
  var side = h('aside', { class: 'side', 'aria-label': 'Sheets and tables' }, h('div', { class: 'pane-h' }, h('h2', 'Structure')), h('div', { class: 'scroll fill' } /* дерево */));
  var main = h('div', { class: 'main' } /* робоча область; сітку бери класами .grid-wrap > table.grid */);
  var aside = h('aside', { class: 'aside', 'aria-label': 'Properties', hidden: true } /* властивості вибраного */);
  el.appendChild(h('div', { class: 'split' }, side, main, aside));
  ctx.hasUnsaved(function () { return dirty > 0; }, { count: function () { return dirty; }, onSave: saveAll, onDiscard: revertAll });
}
```

Стан збереження — СЛОВАМИ (`Saved 14:03` / `Saving…` / `3 unsaved changes · Retry`), не іконкою. Рідкісні налаштування — `E.Collapsible`. На ≤ 1180px `.aside`, на ≤ 900px `.side` стають шарами поверх (це вже в CSS) — дай кнопку-перемикач, що знімає/ставить `hidden`.

---

## 5. Реєстрація діалогу / шторки / вкладки під адресу

```js
// діалог
ctx.dialog('close-period', function (p) { p = p || D.period(D.currentPeriod);
  E.ConfirmDialog({ id: 'per-close', title: 'Close ' + F.period(p.id) + '?',
    consequences: ['12 documents become read-only for data entry.', '7 draft sheets stay unsubmitted and are marked late.', { text: 'A period administrator can reopen it with a reason.', note: true }],
    verb: 'Close period', icon: 'lock', onConfirm: function () { p.state = 'Closed'; ctx.refresh(); E.Toast(F.period(p.id) + ' closed', { tone: 'success', action: { label: 'Undo', onClick: undo } }); } }); });
closeBtn.addEventListener('click', function () { ctx.openDialog('close-period', period); });   // ← через ctx.openDialog

// шторка поза ListPage
ctx.panel('*', function (id) { var u = D.user(id); if (u) E.Drawer({ id: 'sec-user', title: u.fullName, subtitle: u.login, body: function (b) { /* … */ } }); });
row.addEventListener('click', function () { ctx.openPanel(u.id); });

// вкладки: ?tab= читається і пишеться само
page.appendChild(E.Tabs({ id: 'sec-tabs', ctx: ctx, fill: true, tabs: [
  { id: 'roles',  label: 'Roles',  render: function (panel) { /* будуй вміст у panel */ } },
  { id: 'grants', label: 'Grants', count: 3, render: renderGrants },
  { id: 'users',  label: 'Users',  render: renderUsers } ] }));
```

Діалог, що відкриває інший діалог (крок 1 → крок 2): у `onClick` першого — `setTimeout(function(){ ctx.openDialog('step-2'); }, 0)` (перший спершу закриється й прибере свій `?dialog=`). Для справжніх багатокрокових сценаріїв бери `Wizard`.

---

## 6. Довідник фабрик (усі в `window.ECR`, усі повертають DOM-вузол, якщо не сказано інше)

### 6.1 Основа

| | |
|---|---|
| `h(tag, attrs?, ...children)` | `attrs`: `class`, `id`, `text`, `html`, `on:{click:fn}`, `dataset:{}`, `hidden`, `disabled`, `checked`, `value`, будь-який атрибут (`'aria-label'`, `href`, `title`, `for`…). Діти: вузли, рядки (як текст), масиви, `null/false` ігноруються. |
| `icon(name, size=16)` | SVG 24-сітка, stroke 1.5. `iconHtml(name,size)` — рядок, лише для `attrs.html`. |
| `esc(s)` | екранування для `attrs.html` |
| `css(prefix, text)` | додати СВІЙ CSS один раз. Див. §8. |
| `rng(seed)` | детермінований генератор `() => 0..1` |
| `uid(prefix)` | лише для тимчасових вузлів, НЕ для полів форми |
| `store.get(k, def)` / `store.set(k,v)` | localStorage у try/catch. Лише дрібні вподобання глядача. |

Іконки: `file fileX users user layout database sigma code ruler plug swap shield shieldAlert calendar stack camera history clock alert message pulse search chevL chevR chevD chevU arrowR arrowL arrowU lock unlock check x minus plus sun moon monitor rows4 rows3 upload download refresh panelL panelR info checkCircle alertCircle xCircle ban help circle send undo redo windows eye eyeOff menu more sliders inbox return pencil tasks trash copy table route filter key logout globe play stop flag link branch columns grid gear book save external grip fx bolt enter paperclip archive tag bell`. Іншої іконки не малюй — візьми найближчу.

### 6.2 `fmt`

`fmt.number(v, scale=4)` → `1 885.4164` (вузький пробіл, `−` для від'ємних, `—` для null) · `fmt.int(v)` · `fmt.percent(0.62)` → `62 %` · `fmt.date(d)` → `18 Sep 2026` · `fmt.time(d)` → `14:03` · `fmt.dateTime(d)` → `18 Sep 2026, 14:03` · `fmt.period('2026-09')` → `September 2026` · `fmt.duration(sec)` → `1:32` · `fmt.plural(n, {one:'error', other:'errors'})` → `7 errors` · `fmt.bool(v)` → `Yes/No` · `fmt.bytes(n)` · `fmt.initials(name)`.
Дати в `ECR.data` — рядки ISO (`'2026-09-18T14:03'`), передавай їх у `fmt.*` як є.

### 6.3 Кнопки й меню

```js
E.Button({ label, icon?, variant?: 'primary'|'danger'|'ghost', size?: 'sm'|'lg', id?, onClick?(event, btn), href?, disabled?, title?,
           iconOnly?: true /* label стає aria-label і title */, count?, countTone?: 'bad'|'warn'|'run', kbd?, pressed?: bool, submit?: true })
E.Menu({ label: 'More', id, items: [ {label, icon?, onClick?, href?, danger?, disabled?, kbd?, hint?, title?}, {sep:true}, {heading:'…'} ], iconOnly?: true, icon?, align?: 'left'|'right' })
E.openMenu(anchorEl, items)                       // меню на довільній кнопці (напр. «⋯» у рядку таблиці)
E.Segmented({ id, label /* aria */, value, options: ['a','b'] | [{value,label,icon?,title?}], onChange(v) })
```
Одна `variant:'primary'` на екран. `danger` — лише в діалозі підтвердження й у пункті меню.

### 6.4 Сторінка

```js
E.PageHeader({ ctx, id, title, subtitle?, mono?, badge?: node, count?: n, meta?: node,
               back?: {label, href} | false,       // запасний «← Back to»; ctx.from має пріоритет; false — без посилання (для кореневих переліків)
               primary?: {label, icon, onClick|href, id}, secondary?: [≤2 таких самих], more?: [пункти Menu] })
               // >2 secondary самі переїдуть у More (з попередженням у консолі); на ~400px secondary ховаються в More
E.StatStrip({ id, items: [{id, label, value, of?, tone?: 'danger'|'warning', hint?, filter?: false}], active?, onSelect(id|null) })  // ≤4. .setActive(id), .setItems([...])
E.FilterBar({ id, memory: ctx.saved, search?: {placeholder} | false, filters?: [{id, label, options, all?, value?}], right?: node, onChange(values) })
               // values = {q:'…', <filterId>:'…'}. .values(), .set(k,v), .reset(). Кнопка «Clear filters» з'являється сама.
E.Section({ id?, title, hint?, actions?: [Button-опції], content: node })
E.Banner({ tone?: 'info'|'warning'|'danger'|'success', title?, text?, icon?, actions?: [Button-опції], dismissable?, flush?: true /* без рамки, на всю ширину */, id? })
E.ResultBanner({...те саме})                       // = Banner tone:'success' + dismissable. Підсумок дії з «що далі».
E.twoLine('Людський підпис', 'код-другим-рядком', {mono?: true})
```

### 6.5 Таблиця

```js
var t = E.DataTable({ id, columns, rows, rowKey: 'id' | fn(row), state: ctx.state, memory: ctx.saved,
  onRowClick?(row, tr), rowLabel?(row) /* aria-label рядка */, selectedKey?, selectable?, onSelect?(keys),
  pageSize?: 50, total?, tall?: true /* рядок +12px для twoLine */, auto?: true /* висота за вмістом, напр. у шторці */,
  empty: {icon, title, text, actions}, error: {title?, text?, code, correlationId?, onRetry?}, onClearFilters?, caption? });
// колонка: { key, label, num?: true (праворуч, моно), scale?, mono?, width?, sortable?: false, sortValue?(row), render?(row) → вузол | рядок(текст) | null(«—»), hideSm?, title? }
t.setRows(rows, {filtered: true});  t.setState('loading');  t.select(key);  t.selection();  t.clearSelection();
E.Pagination({ id, shown, total, step, onMore })   // окремий «Show more N of M», якщо список не DataTable
```
Таблиця живе у власному `overflow:auto`; шапка закріплена; «Showing 25 of 112 · Show more» додається сам. `filtered:true` + порожньо → стан «Nothing matches these filters» з кнопкою скидання. ≤ 7 колонок (більше — попередження в консолі).

### 6.6 Стани

```js
E.EmptyState({ icon?, title, text, actions?: [Button-опції] })      // пояснення + дія
E.ErrorState({ title?, text?, code?, correlationId?, onRetry?, back?: {label, href} })   // код, correlation id з копіюванням, Retry
E.ForbiddenState({ area: 'Security', permission: 'Security.ViewRoles', permissionLabel: 'View roles and grants' })
E.Skeleton({ kind: 'table'|'form'|'text', rows, cols })
E.StateSwitch(ctx.state, { data: fn → node, empty: {…EmptyState}, error: {…ErrorState}, loading: {…Skeleton} })
```

### 6.7 Статуси й показники

```js
E.StatusBadge(kind, state, { label?, quiet?: true /* без рамки, у щільних таблицях */, title? })
```
| kind | state → вигляд |
|---|---|
| `sheet` | Draft (нейтр.) · **Submitted (info — чекає дії)** · Approved (нейтр. ✓) · **Rejected (bad)** · **Returned (warn)** |
| `period` | Open · **Grace (warn)** · Closed · Archived (бляклий) · NotOpened (бляклий, «Not opened») |
| `job` | Queued · **Running (info, спінер)** · Done · **Failed (bad)** · Cancelled (бляклий) |
| `version` | Draft · Published · Archived (бляклий) |
| `health` | Healthy · **Degraded (warn)** · **Unhealthy (bad)** |
| `severity` | Info · **Warning** · **Error** · **Critical (суцільний червоний)** |
| `user` | Active · **Locked (bad)** · Disabled (бляклий) · **Invited (info)** |

Не роби власних бейджів. Іншого стану немає в наборі — скажи про це у звіті, не вигадуй колір.

```js
E.SegmentBar({ segments: [{label:'Air emissions', state:'Draft', note?}], size?: 'lg', summary?: '…' | false, doneState?: 'Approved', onSelect?(seg, i) })
   // для документа: E.SegmentBar({ segments: doc.sheets.map(function (s) { return { label: s.name, state: s.state }; }) })
E.Progress({ value, max?: 100, label?: '62 %' | false, tone?: 'accent'|'danger'|'warning', ariaLabel })
E.Stepper({ steps: ['Draft','Submitted','Approved'], current: 0, tone?: 'warning'|'danger', labels?: true })
E.KeyValue([{label, value: text|node, mono?, hint?}], {wide?: true})
E.CodeText('Document.Approve')  /  E.CodeText(formulaText, {block: true})      // моно; block — з кнопкою Copy
E.Collapsible({ id, title, summary?, open?: false, content: node | fn(bodyEl), onToggle? })
E.Tabs({ id, ctx, tabs: [{id, label, count?, tone?, render(panelEl)}], active?, fill?, inset?, onChange? })   // .show(id), .current()
E.PeriodPicker({ id, value: '2026-09', onChange(id, period) })                 // .value(), .set(id)
```

### 6.8 Поля форм

```js
var f = E.Input({ id: 'sec-role-name', label: 'Role name', hint: 'Shown in grants and in the audit trail.', required: true, value: '',
                  type?, placeholder?, mono?, suffix?: 't·yr⁻¹', icon?, maxlength?, disabled?, readOnly?, onInput(v), onChange(v) });
f.input        // сам <input>
f.value        // читання/запис
f.setError('A role with this name already exists');   f.setError(null);
E.Select({ id, label, options: ['a'] | [{value,label}], value, onChange, hint?, required? })
E.Textarea({ id, label, rows?, maxlength?, hint?, required?, onInput })
E.Checkbox({ id, label, checked, hint?, onChange(bool) })        // .input
E.Switch({ id, label, checked, hint?, onChange(bool) })          // .input; показує On/Off словом
E.Field({ id, label, hint, control: node })                      // власний контрол у стандартній обгортці (id має стояти на самому контролі)
// без видимого підпису (лише у FilterBar-подібних рядках): { bare: true, ariaLabel: '…' }
```
Помилки показуй при спробі зберегти, а не під час набору; фокус — на перше хибне поле. Двоколонкова форма — `h('div', {class:'form-grid'}, f1, f2, h('div', {class:'span-2'}, f3))`.

### 6.9 Шари

```js
var d = E.Dialog({ id, title, body: node | fn(bodyEl, ctl), wide?, narrow?, alert?, dismissable?: false, onClose?,
  footer: [{ label, variant?, icon?, id?, onClick?(ctl), keepOpen?, autofocus?, disabled?, align?: 'left' }] });
// кнопка футера закриває діалог після onClick, якщо немає keepOpen і onClick не повернув false.   d.close(), d.body, d.setFooter([...])
E.Drawer({ id, title, subtitle?, badge?: node, body: node | fn(bodyEl, ctl), footer?: [...як у Dialog], wide?, flush?, onClose? })
E.ConfirmDialog({ id, title: 'Delete role “Night shift”?' /* НАЗВА ОБ'ЄКТА */, text?, consequences: ['…', {text:'…', note:true}],
                  verb: 'Delete role' /* ДІЄСЛОВО на червоній кнопці */, icon?, danger?: true, typeToConfirm?: 'Night shift', onConfirm })   // фокус на Cancel
E.ReasonDialog({ id, title, text?, label?: 'Reason', placeholder?, hint?, minLength?: 10, verb, icon?, danger?, onSubmit(reason) })   // кнопка вимкнена, доки порожньо
E.Wizard({ id, title, data?: {}, wide?, steps: [{ id, label, hint?, render(bodyEl, data, api), validate?(data) → 'текст помилки' | null, canNext?(data) → bool }],
           summary(data) → node /* крок Review додається сам */, reviewText?, applyLabel: 'Publish version', danger?, onApply(data, api), mount?: el /* сторінковий режим */ })
   // далі з помилкою не пустить: validate повертає текст → червоний банер у кроці. api.close(), api.fail(msg), api.refreshButtons()
E.UnsavedGuard({ count, text?, onSave?, onDiscard, onStay? })       // зазвичай НЕ викликаєш сам — досить ctx.hasUnsaved(...)
E.ResultDialog({ id, title, text, tone?: 'success'|'warning'|'danger', body?: node, actions: [{label, href?|onClick?, primary?}] })   // важкий підсумок; легкий — ResultBanner
E.InlineConfirm(buttonEl, { text: 'Remove this grant?', verb: 'Remove', danger: true, onConfirm })   // дрібна оборотна дія
E.Popover(anchorEl, { title?, content: 'текст' | node | fn(popEl), width?, align?: 'left'|'right' })   // подробиця без зміни екрана
E.Toast('Saved', 'success')   /   E.Toast('Draft deleted', { tone?: 'success'|'warning'|'danger', action: {label:'Undo', onClick}, duration? })
E.tasks.start({ title, icon, detail, duration: 5000, doneDetail, result: {label:'Download', onClick}, onProgress(p), onDone(task) })   // довга операція → «My tasks» із прогресом
E.tasks.open()
```

Яке підтвердження коли:

| ситуація | компонент |
|---|---|
| дрібне й оборотне (зняти грант, прибрати рядок) | `InlineConfirm` або дія + `Toast` з **Undo** |
| незворотне / зачіпає інших (видалити, закрити період, опублікувати) | `ConfirmDialog` з наслідками |
| потрібне пояснення (відхилити, повернути, перевідкрити період) | `ReasonDialog` |
| ≥ 2 кроків або є що перевірити перед застосуванням (створити документ, опублікувати версію, імпорт) | `Wizard` |
| довше 2 с | `E.tasks.start` + тост «continues in My tasks» |
| після дії є «що далі» | `ResultBanner` угорі сторінки (важке — `ResultDialog`) |

---

## 7. Токени і класи

### 7.1 Токени (лише вони; світла/темна тема перемикаються самі)

| група | токени |
|---|---|
| поверхні | `--ground` (тло сторінки) · `--surface` (панелі, таблиці) · `--sunken` (шапки таблиць, втоплене) · `--raised` (діалоги, меню) · `--hover` · `--select` |
| лінії | `--border` · `--border-strong` · `--grid-line` |
| текст | `--text` · `--muted` · `--faint` |
| бренд | `--accent` · `--accent-hover` · `--on-accent` · `--accent-text` · `--accent-soft` · `--focus` |
| стани | `--danger` `--danger-soft` `--danger-solid` `--on-danger` · `--warning` `--warning-soft` · `--success` `--success-soft` |
| сітка | `--calc-bg` · `--hatch` · `--skeleton` |
| тінь/тло шару | `--shadow-float` · `--scrim` · `--scrim-light` |
| шрифти | `--sans` (IBM Plex Sans) · `--mono` (IBM Plex Mono) |
| розміри тексту | `--fs-xs` 11 · `--fs-sm` 13 (основний) · `--fs-md` 15 · `--fs-h3` 18 · `--fs-h2` 22 — інших немає |
| відступи | `--s1` 4 · `--s2` 8 · `--s3` 12 · `--s4` 16 · `--s5` 24 |
| радіуси | `--r1` 4 · `--r2` 6 |
| щільність | `--row` (рядок таблиці/сітки 28/36) · `--ctl` (висота поля й кнопки 28/36) · `--rail-item` — ВИСОТИ рядків і контролів бери лише звідси |
| рух | `--t` 120 мс |

### 7.2 Класи-утиліти

Текст: `mono` `num` `muted` `faint` `danger-text` `warning-text` `t-xs` `t-sm` `t-md` `t-h3` `t-b`(500) `t-sb`(600) `eyebrow` `truncate` `nowrap` `sr` `key` `link-btn`
Розкладка: `row` (+`wrap` `top` `between`) · `stack` · `gap-1…gap-5` · `p-2…p-5` · `grow` · `fill` · `no-shrink` · `scroll` · `divider` · `vsep` · `panel` · `form-grid` (+`span-2`) · `hide-sm`
Сторінка: `page` (+`page-fill`, `narrow`) · `split` > `side` | `main` | `aside` · `pane-h` · `editor-head` > `eh-title` | `eh-ctx` > `eh-actions` · `section` · `tabs`
Дрібне: `chip` (моно-мітка: код, адреса комірки) · `count` (+`bad` `warn` `run`) · `badge` (лише через StatusBadge) · `dot` (+`filled` `partial` `empty` `error` `submitted` `draft` `returned`) · `spin` · `mini-empty` · `conseq` · `sumline` · `was` · `pick`
Сітка даних (прев'ю шаблону тощо): `grid-wrap` > `table.grid`; рядковий заголовок `th.rh`; комірка `td.c` + `is-calc` `is-locked` `is-dirty` `is-rounded` `is-error` `is-warn` `sel` `active`. Таблиця в діалозі: `div.table-wrap > table.data` (колонки чисел — `class="num"`).

---

## 8. Власний CSS

```js
E.css('ops', '.ops-matrix th{…} .ops-matrix td{…}');
```
- ЛИШЕ через `ECR.css('<префікс-файлу>', '…')`. Префікси: `work`, `tpl`, `data`, `ops` (можна кілька викликів: `ops-sec`, `ops-health`).
- Кожен селектор починається з класу з цим префіксом: `.ops-…`. Глобальних селекторів (`table`, `.btn`, `.page`, `h2`) і перевизначення класів набору — НЕ писати.
- Лише токени: кольори `var(--…)`, відступи `var(--s1…s5)`, радіуси `var(--r1|r2)`, розміри тексту `var(--fs-…)`, висоти рядків `var(--row|--ctl)`. Дозволені «голі» числа: `0`, `1px` для ліній, ширини колонок/панелей, `%`, `fr`.
- Без `100vh`, без `position:fixed` (шари робить набір), без анімацій довше `var(--t)`. Сторінка не скролиться горизонтально: широке — у власному `overflow:auto`.
- На ~400px: перевір `@media (max-width:640px)` — усе в одну колонку, бічний відступ ≥ 16px (`.page` дає його сам).
- Inline `style=` — лише для обчислюваних значень (ширина смужки у %).

---

## 9. `ECR.data` — спільні демо-дані (детерміновані)

| ключ | вміст |
|---|---|
| `today` | `'2026-09-18T14:03:00'` — «зараз» прототипу |
| `project`, `projects[12]` | `{id:'P07131100', name:'Central processing facility', company, template:'GEN07131100', templateVersion}` · `{id,name}` |
| `currentPeriod`, `periods[12]`, `period(id)` | `'2026-09'` · `{id:'2026-09', name, state:'Archived'\|'Closed'\|'Open'\|'NotOpened', closes, daysLeft:12, note, closedBy}` — січень–червень Archived, липень–серпень Closed, вересень Open (12 днів до закриття), жовтень–грудень NotOpened |
| `users[12]`, `user(nameOrId)` | `{id:'u-02', name:'D. Akhmetova', fullName, login:'CORP\\d.akhmetova', roles:['DataEntry'], auth:'Windows'\|'Local', state:'Active'\|'Locked'\|'Disabled'\|'Invited', lastSeen}` — A. Iskakov (Approver), D. Akhmetova (DataEntry, поточний користувач за замовчуванням), M. Tulegenov (SystemAdministrator), M. Petrenko, S. Nurlanov, G. Tulegenova (TemplateAdministrator), O. Kovalenko (Auditor), B. Sadykov (PeriodAdministrator), R. Dzhaksybekov (Locked), svc-import, bootstrap (Disabled), K. Abenova (Invited) |
| `roles[8]` | `{id:'Approver', name:'Approver', description, builtIn:true}` — Approver, Auditor, BootstrapAdministrator, DataEntry, PeriodAdministrator, SystemAdministrator, TemplateAdministrator, Viewer |
| `permissions[7]` | `{domain:'Document', items:[{code:'Document.Approve', label:'Approve or reject sheets', dangerous:bool}]}` — домени Document, Calculation, Template, Registry, Period, Security, System |
| `grants`, `granted(role, code)` | `{Approver:['Document.View',…], SystemAdministrator:['*'], PeriodAdministrator:['Period.*',…]}` |
| `groups[4]` | `{id, name, description, members:['u-02'], projects:['P07131100'] \| ['*']}` |
| `sheetNames[5]`, `sheetTables` | Air emissions (91), Water use & discharge (24), Waste (37), GHG inventory (58), Energy (12) |
| `documents[12]`, `doc(id)`, `docState(doc)`, `campaign()` | `{id:'DOC-000001', project, facility, period:'2026-09', owner, approver:'A. Iskakov', updated, late:bool, template, version, sheets:[{name, state, tables, filled, issues}]}` · `docState` → Draft/Submitted/Approved/Rejected/Returned · `campaign()` → `{documents:12, sheets:60, filled, submitted, approved, attention, issues, late}` для StatStrip кампанії |
| `registries[8]`, `registryEntries{EmissionSources[42], Substances[12], FuelTypes[6]}`, `emissionSources[42]` | `{code:'EmissionSources', name:'Emission sources', description, entries, updated, updatedBy, fields}` |
| `units[14]` | `{id, symbol:'t·yr⁻¹', name, dimension, base:bool, baseUnit, factor, usedIn}` |
| `methodologies[6]`, `methodologyVersions(id)` | `{id:'M-04', name, basis:'IPCC 2006, vol. 2', state:'Draft'\|'Published'\|'Archived', version, versions, sheet, updated, owner, constants, formulas}` · версії `{id, version, state, created, author, changes, note}` |
| `expressions[10]` | `{id:'EF_NOX_TURBINE', kind:'Formula'\|'Rule'\|'Script', name, text, methodology, check:'Valid'\|'Warning'\|'Error', usedIn, updated, author}` |
| `templates[6]`, `templateVersions(id)` | `{id:'GEN07131100', name, state, version, versions, sheets, tables, documents, updated, owner}` · версії `{id:'v3', template, version, state, created, author, documents, note}` |
| `sources[4]`, `mappings[14]` | `{id:'SRC-01', name:'PI Web API · CPF historian', kind, health:'Healthy'\|'Degraded'\|'Unhealthy', lastRun, tags, problem, enabled, schedule}` · `{id, tag, source, sheet, table, row, column, unit, value, status:'Info'\|'Warning'\|'Error', message}` |
| `jobs[10]` | `{id:'J-10427', type, target, state:'Queued'\|'Running'\|'Done'\|'Failed'\|'Cancelled', progress, started:'14:01', duration:сек, by, error, code, correlationId, result, attempts}` |
| `snapshots[5]` | `{id, name, period, by, created, size, scope, hash}` |
| `audit[64]` | `{id, at, user, action:'Sheet.Approve', label:'Approved a sheet', severity:'Info'\|'Warning'\|'Error'\|'Critical', object, before, after, origin, ip, correlationId}` |
| `consistency[7]` | `{id, severity, title, document, where, status:'Open'\|'Resolved', found, check}` |
| `uiStrings[12]` | `{key:'sheet.submit', en, ru, kk, missing:0\|1, area}` |
| `health` | `{overall:'Degraded', checked, checks:[{id, name, state, value, note}], database:[[k,v]], application:[[k,v]]}` |

Дані можна МУТУВАТИ в межах сеансу (змінив стан аркуша — перелік документів покаже новий). Це навмисно: так екрани узгоджені між собою. Екран документа пише `doc.sheets[i].state` та `.issues`.

---

## 10. Розподіл маршрутів

| файл | маршрути |
|---|---|
| `screens-work.js` | `/login` (chrome:false, шанує `?return=`), `/change-password` (chrome:false), `/` (Documents — StatStrip кампанії з `D.campaign()` і SegmentBar аркушів у колонці; клік по рядку → `E.go('/documents/'+id)`), `/my-groups`, `/404`, `/403`, `/error` + діалог `create-document` (Wizard; після Apply → `E.go('/documents/<id>', {toast:'Document created'})`) |
| `screens-templates.js` | `/admin/templates`, `/admin/templates/:id`, `/admin/templates/:id/versions/:versionId`, `…/versions/:versionId/relations`, `…/compare`, `…/access-matrix`, `…/period-rules` |
| `screens-data.js` | `/admin/registries`, `/admin/registries/:code/definition`, `/admin/registries/:code/entries`, `/admin/methodologies`, `/admin/methodologies/:id/versions`, `/admin/expressions`, `/admin/units`, `/admin/sources`, `/admin/mapping` |
| `screens-ops.js` | `/admin/security` (вкладки roles/grants/users), `/admin/periods`, `/admin/jobs` (шторка `?panel=<J-id>` — на неї веде «See why» з «My tasks»), `/admin/snapshots`, `/admin/audit`, `/admin/consistency`, `/admin/ui-strings`, `/admin/health` |
| уже є | `/documents/:id` (`screen-document.js`), `/flows` і вбудований 404 (`kit.js`) |

`title`, `group`, `icon`, `order` для кожного маршруту записані в шапці твоєї заглушки — збережи їх, інакше рейка перемішається.

---

## 11. Чекліст «перед тим як здати екран»

**Навантаження**
- [ ] Одна кнопка `primary` на екрані; поруч ≤ 2 другорядні; решта в «More».
- [ ] StatStrip ≤ 4 показники і лише на переліку; на редакторі його немає. Колір — лише в показника-проблеми.
- [ ] Таблиця ≤ 7 колонок; подробиці — у шторці. Коди — другим рядком або в `title`.
- [ ] Бічні панелі подробиць закриті за замовчуванням; рідкісні налаштування — у `Collapsible`.
- [ ] Жодного «зеленого все гаразд»: нормальні стани нейтральні, бейджі — лише `StatusBadge`.

**Стани й адреса**
- [ ] `?state=empty|error|loading` дає осмислений вигляд: порожньо — пояснення + дія; помилка — код, correlation id, Retry; завантаження — скелет.
- [ ] Кожен діалог відкривається з адреси `?dialog=<id>`, кожна шторка — `?panel=<id>`, кожна вкладка — `?tab=<id>`; кнопки викликають `ctx.openDialog` / `ctx.openPanel`.
- [ ] `?theme=dark` і `?theme=light`, `?density=comfortable` — нічого не зламано, жодного літерала кольору.

**Переходи**
- [ ] Кожна дія має завершення: тост, `ResultBanner` «що далі» або перехід. Після створення — перехід на об'єкт із тостом; після видалення — назад до переліку з тостом і Undo (де безпечно).
- [ ] Незворотне — `ConfirmDialog` з назвою об'єкта, наслідками й дієсловом; з поясненням — `ReasonDialog`; багатокрокове — `Wizard` із підсумком.
- [ ] Екран із формою/редактором реєструє `ctx.hasUnsaved(...)`.
- [ ] Вкладені сторінки мають `navPath`, `example` і `PageHeader({ctx, back:{…}})`; повернення до переліку зберігає фільтри (`memory: ctx.saved`).
- [ ] **Зареєстровано `ECR.flow(...)` для КОЖНОГО сценарію розділу, і кожен крок-посилання відкриває саме той стан.** Перевір на `#/flows`, клікаючи всі кроки.

**Доступність і вузький екран**
- [ ] Кожне поле має стабільний `id` з префіксом екрана і `label`; помилка — `setError`.
- [ ] Усе досяжне з клавіатури; після закриття шару фокус повертається (набір робить це сам, якщо шар відкрито з кнопки).
- [ ] 400px: без горизонтальної прокрутки сторінки, дії в «More», другорядні колонки мають `hideSm: true`.
- [ ] У консолі немає попереджень `[PageHeader]`, `[StatStrip]`, `[DataTable]`, `[ECR.css]`, `[Input]`.

**Межі**
- [ ] Змінено лише свій файл. Дані — з `ECR.data`. Зовнішніх ресурсів, бібліотек, `fetch` немає. Працює з `file://`.

# 06 — Якість UI / UX / доступність (клієнт `src/Ecr.Web/src`)

**Область:** стани екранів, узгодженість, токени теми, i18n, a11y, верстка, форми.
**SHA `main`:** `f446e9b29` (2026-11-11 13:47 +0200). **Дата аудиту:** 2026-11-11. Лише читання; застосунок не запускався — усе нижче доведено читанням коду. Що потребує стенда, позначено окремо.

**Важливе про сід.** Бриф згадує `09-seed.sql` — у main його вже немає (`CLAUDE.md`, «✎ 2026-11-02 #674 seed split»): каталог складається з `Sql/seed/00-prelude.sql` → `Sql/seed/features/<AREA>-<slug>.sql` → `Sql/seed/99-epilogue.sql`. Нові рядки нижче оформлено як **новий файл фічі**: порція `#I18N` для ru/kz і порція `#UiEn` для en. Ім'я файлу має відповідати шаблону `^[A-Z0-9]+-[a-z0-9-]+\.sql$` (`tests/Ecr.Architecture.Tests/SeedSplitLayoutTests.cs:263`). Scope: `0` — публічна область (екран входу), `1` — приватна.

**Охоплено повністю:**
- повнота ru/kz для всіх en-ключів сіду. Розібрано всі файли `seed/**`: 7634 en-ключі, 7615 ru, 7615 kz. Усі 19 «прогалин» — ключі з секції «Прибрані ключі» (`00-prelude.sql:1324-1380`), тобто прогалин немає;
- літерали в JSX, `aria-label`, `title`, `placeholder`, тостах;
- множина через конкатенацію;
- сирі hex у CSS поза `shared/theme`;
- `prefers-reduced-motion`;
- кнопки руйнівних дій (варіант і колір);
- `useQuery` без шляху помилки (сканер на 119 файлів, вибірково перечитано ~15);
- екран дозволу (`PermitPage`, `HeaderForm`, `TableTab`, `RowDrawer`, `LifecycleDialogs`, `AirLimitsTab`);
- панелі покриття методології;
- канали сповіщень;
- екран входу.

**Охоплено поверхово:** `DocumentsPage`, `DocumentActionBar`, `SheetCopyBar`, «Мої задачі», `piData`, `CollectionRunsPanel`, `UnitDetailBody`, `PermitUsageTab`.

**Не охоплено:**
- 4067-рядкова `DocumentGrid.tsx` (лише вибірково);
- переповнення таблиць і ширина 375 px. Мінімум продукту — 1024 px (`DIRECTIVE-14-UIUX.md` UX-15), а `scrollWidth` без браузера не довести. 44 файли з `<Table>` без `ScrollContainer`/`overflow` у самому файлі перелічено в розділі «Відкинуто» як кандидатів на перевірку на стенді;
- темна тема «на око».

Еталонні правила, на які посилаються знахідки:
- `docs/build/DIRECTIVE-14-UIUX.md`: принципи §2.3–2.4, UX-07, UX-10, UX-11, UX-16;
- `docs/design/ui-conventions.md`: «Варіанти — чотири ролі»;
- `eslint.config.js`: ФВ-14.11, D15-09;
- `features/common/DisabledReason.tsx`;
- `shared/ui/unsavedSources.ts`;
- `shared/format/*`.

---

## Зведена таблиця

| ID | Важливість | Категорія | Назва | Зусилля | Ризик зміни | Рішення PO |
|---|---|---|---|---|---|---|
| U-1 | Medium | forms | Шапка дозволу: незбережена чернетка мовчки губиться при перемиканні вкладки чи виході; немає `maxLength` 200/500 | S | низький | ні |
| U-2 | Medium | ui | Покриття «виходи → колонки» показує сирі `tableDefId.columnDefId` («193.6017») | S | низький | ні |
| U-3 | Medium | ui / data-safety | «Remove URL» вебхука — незворотне видалення одним натиском, без підтвердження | S | низький | ні |
| U-4 | Low | ui | Тип дозволу — сирим кодом у шапці «лише читання» і в діалозі «Activate» (у переліку — назвою) | S | низький | ні |
| U-5 | Low | ui-consistency | 6 руйнівних кнопок `variant="default" color="statusError"`: Mantine ігнорує колір, кнопка виглядає нейтральною | S | низький | ні |
| U-6 | Low | a11y | Причина недоступності дії лише в `title` / на нефокусовній кнопці (permits): клавіатура її не дістає | S | низький | ні |
| U-7 | Low | i18n | Тривалість прогону збору — `toFixed(1)`: «12.3 s» з крапкою в ru/kz | S | низький | ні |
| U-8 | Low | theme | `air.css`: сирі hex і палітра Mantine замість токенів `statusError`/`statusWarning` | S | низький | ні |
| U-9 | Low | ux / a11y | Екран входу без підказки Caps Lock (UX-16 не виконано) | S | низький | ні |

---

## Знахідки

### U-1 — Medium — Шапка дозволу: незбережена чернетка мовчки губиться; немає `maxLength`

**Категорія:** forms

**Де:**
- `src/Ecr.Web/src/pages/admin/PermitPage.tsx:170` — `<Tabs value={tab} onChange={selectTab} keepMounted={false}>`;
- `src/Ecr.Web/src/pages/admin/PermitPage.tsx:189-191` — панель Overview;
- `src/Ecr.Web/src/features/permits/permit/HeaderForm.tsx:99-124` — чернетка в локальному `useState`, немає реєстрації в `unsavedSources`;
- `HeaderForm.tsx:167-178` — `TextInput` «Number» і «Location» без `maxLength`;
- `src/Ecr.Web/src/features/permits/list/NewPermitWizard.tsx:150-166` — ті самі поля без `maxLength`;
- серверні межі: `src/Ecr.Application/Permits/PermitInput.cs:10` (`MaxNumber = 200`) і `:13` (`MaxLocation = 500`).

**Проблема.** `EditableHeader` тримає чернетку в локальному стані:

```tsx
const [draft, setDraft] = useState<HeaderDraft>(original);
...
const dirty = !sameDraft(draft, original);
```

Панель Overview монтується лише поки активна (`Tabs … keepMounted={false}`). Перемикання на вкладку таблиці, «Air limits» чи «Used in» розмонтовує форму, і чернетка зникає без жодного питання.

Глобальний `UnsavedGuard` (`shared/ui/UnsavedGuard.tsx`) питає лише зареєстровані джерела. `HeaderForm` джерелом не реєструється, на відміну від `NodeAttributesForm.tsx:150-153`:

```tsx
registerUnsavedSource('nae-node-form', { hasUnsaved: () => dirtyRef.current && !readOnly })
```

Тож вихід зі сторінки чи закриття вкладки браузера теж нічого не питає.

Окремо: поля «Number» і «Location» не мають `maxLength`, хоча сервер приймає ≤ 200 і ≤ 500 символів. Довший номер дає `422 ECR-PRM-0422 numberTooLong` тостом через `showApiError` (`HeaderForm.tsx:113-114`), а не помилкою біля поля.

**Сценарій відтворення:**
1. Відкрити `/admin/permits/{id}` чернетки з правом запису (`caps.editHeader`).
2. Змінити «Location».
3. Натиснути вкладку таблиці «Limits», потім повернутися на «Overview».

Фактично поле має старе значення, і жодного попередження не було. Очікувано правка лишається або є питання. Правило: принцип 4 `DIRECTIVE-14-UIUX.md` §2 («Збережено чи ні… Мовчання — дефект») і `D14-12`, крок 3 («незбережені… не губляться мовчки»), реалізований через `unsavedSources`.

**Вплив:** кожен автор дозволу, щоразу, коли між правкою шапки і «Save changes» перейде на іншу вкладку чи сторінку. Втрачається введене.

**Зміна — покроково:**

1. Файл `src/Ecr.Web/src/pages/admin/PermitPage.tsx`: тримати Overview змонтованою, щоб чернетка переживала перемикання вкладок. `Tabs.Panel` у Mantine 7.15 має власний `keepMounted` (`node_modules/@mantine/core/lib/components/Tabs/TabsPanel/TabsPanel.d.ts:6-7`).
   ```diff
   -          <Tabs.Panel value={OverviewTabId}>
   +          {/* U-1: чернетка шапки дозволу живе в OverviewTab — панель не розмонтовується при перемиканні вкладок. */}
   +          <Tabs.Panel value={OverviewTabId} keepMounted>
               <OverviewTab permit={permit} caps={caps} onActivate={() => open('activate')} onRenew={() => open('renew')} />
             </Tabs.Panel>
   ```

2. Файл `src/Ecr.Web/src/features/permits/permit/HeaderForm.tsx`: зареєструвати джерело незбережених змін і додати `maxLength`.
   ```diff
   -import { useState, type JSX } from 'react';
   +import { useEffect, useRef, useState, type JSX } from 'react';
   ...
   +import { registerUnsavedSource } from '@/shared/ui/unsavedSources';
   ```
   ```diff
      const errors = shownErrors ? headerErrors(draft) : {};
      const dirty = !sameDraft(draft, original);
   +  const dirtyRef = useRef(dirty);
   +  dirtyRef.current = dirty;
   +
   +  // U-1: незбережена шапка не губиться мовчки при виході зі сторінки чи закритті вкладки (UnsavedGuard).
   +  useEffect(() => registerUnsavedSource(`permit-header-${String(permit.id)}`, { hasUnsaved: () => dirtyRef.current }), [permit.id]);
   ```
   ```diff
               <TextInput
                 label={t('permits.page.header.number')}
                 required
   +             maxLength={PermitNumberMax}
                 value={draft.number}
   ...
               <TextInput
                 label={t('permits.page.header.location')}
   +             maxLength={PermitLocationMax}
                 value={draft.location}
   ```
   Константи винести в `src/Ecr.Web/src/features/permits/permit/model.ts`, щоб їх брав і майстер:
   ```ts
   /** Межі сервера (`PermitInput.MaxNumber` / `MaxLocation`, `src/Ecr.Application/Permits/PermitInput.cs:10,13`). */
   export const PermitNumberMax = 200;
   export const PermitLocationMax = 500;
   ```
   ⚠ `model.ts` тягне розбір вставки (коментар у `names.ts:4-7`, бюджет маршруту D-132). Тому покласти константи в `names.ts`, а не в `model.ts`: його вже імпортує шапка.

3. Файл `src/Ecr.Web/src/features/permits/list/NewPermitWizard.tsx:150-166`: додати `maxLength={PermitNumberMax}` до поля номера й `maxLength={PermitLocationMax}` до поля місця, з імпортом з `../permit/names`.

**Тест-доказ:** `src/Ecr.Web/src/features/permits/permit/__tests__/PermitPage.test.tsx`, блок `describe('шапка-форма')`, два нові кейси.
- `'чернетка шапки переживає перемикання вкладок'`: змінити «Location» на `X` → `fireEvent.click` на вкладку `Limits2` → назад на `Overview` → `textbox /^Location/` має значення `X`. Без кроку 1 (прибрати `keepMounted` у панелі) кейс падає: значення `Central facility`.
- `'змінена шапка — джерело незбережених змін'`: імпортувати `hasUnsavedChanges` з `@/shared/ui/unsavedSources`. Після `fireEvent.change` номера має бути `true`, після «Discard changes» — `false`. Без `registerUnsavedSource` перше твердження падає.
- `'поля номера й місця обмежені межами сервера'`: `textbox /^Number/` має `maxLength` 200, `textbox /^Location/` — 500.

**Перевірка після впровадження:**
- `npx vitest run src/features/permits/permit/__tests__/PermitPage.test.tsx src/features/permits/permit/__tests__/PermitPage.a11y.test.tsx`;
- `npx tsc --noEmit`;
- `npm run lint`;
- на стенді: правка «Location» → вкладка «Limits» → «Overview» (значення на місці) → клік «Permits» у меню (діалог `UnsavedGuard`).

**Ризики і побічні ефекти:**
- Прихована панель Overview лишається в DOM (`display: none`). Запити `getByText` в інших кейсах `PermitPage.test.tsx` можуть знайти дубль тексту з Overview. `getByRole` приховане ігнорує. Прогнати весь файл і за потреби звузити пошук `within(panel)`.
- Бюджет маршруту: `unsavedSources` уже в головному чанку (його імпортує `AppLayout` → `UnsavedGuard`), тож приросту немає.
- Ключ форми `key={permit.rowVersion}` (`HeaderForm.tsx:250`) перезбирає форму після збереження, і відписка спрацьовує через cleanup ефекту.

**Обґрунтування.** Альтернатива — підняти чернетку в URL чи модульне сховище. Це складніше і суперечить `D-132`. Питати при кожному перемиканні вкладки — шлях, який директива відкидає (`UnsavedGuard.tsx`: «карає користувача за швидкість»). `keepMounted` на одній панелі зберігає чернетку без питань, а реєстрація джерела закриває вихід зі сторінки.

**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням; поведінку `display:none` Mantine перевірити прогоном тесту.
**Залежності:** U-4 (той самий файл `HeaderForm.tsx` — робити одним PR).

---

### U-2 — Medium — Покриття «виходи → колонки» показує сирі ідентифікатори «193.6017»

**Категорія:** ui

**Де:**
- `src/Ecr.Web/src/features/methodologies/MethodologyCoveragePanel.tsx:37-49` (`BindingList`);
- `MethodologyCoveragePanel.tsx:140-152` (таблиця `waitingBindings`);
- серверна причина: `src/Ecr.Application/Calculations/MethodologyCoverageHandler.cs:71-86` повертає `MethodologyAuthoringMap.Binding` без `TableCode`/`ColumnCode`. Для порівняння, `ListCalculationBindingsHandler` у `MethodologyAuthoringHandlers.cs:1000-1023` їх заповнює.

**Проблема:**
```tsx
<Code key={binding.id}>{`${String(binding.tableDefId)}.${String(binding.columnDefId)}`}</Code>
...
<Table.Td>{binding.tableDefId}</Table.Td>
<Table.Td>{binding.columnDefId}</Table.Td>
```

Адміністратор бачить внутрішні ключі БД замість кодів таблиці й колонки. Той самий дефект уже виправлено в сусідньому обробнику з коментарем `MethodologyAuthoringHandlers.cs:1005`: «⚠ F-21: колонка — кодом, а не голим `ColumnDefId` („193 / 6017“)». Заголовки колонок таблиці навіть звуться «Table» / «Column» (ключі `methodologies.tableDefId` / `columnDefId`), а під ними стоять числа.

**Сценарій відтворення:** `/admin/methodologies/{id}/versions/{vid}`, секція «Output coverage». Вихід із прив'язкою показує `7.101`, а прив'язка, що чекає, — `3 | 55`. Очікувано: `EMIS.CO2_T`, тобто код таблиці й колонки, як у панелі прив'язок на тій самій сторінці. Правило: `D15 §0 L10` і `F-21` («ідентифікатор не показується як назва»), `DIRECTIVE-14-UIUX.md` §2.1.

**Вплив:** кожен методолог, що читає покриття. Він не може зіставити число з колонкою без БД, а саме ця панель має сказати, «куди лягає вихід».

**Зміна — покроково** (лише клієнт: DTO вже має `tableCode?`/`columnCode?`, `api/schema.d.ts` `CalculationBindingDto`; контракт не змінюється):

1. Файл `src/Ecr.Web/src/features/methodologies/MethodologyCoveragePanel.tsx`.
   ```diff
   -import { useQuery } from '@tanstack/react-query';
   +import { useQuery } from '@tanstack/react-query';
   +import { queryKeys } from '@/api/queryKeys';
    import type { CalculationBindingDto } from '@/api/types';
   ...
   -import { methodologyCoverage, type MethodologyCoverageDto } from './api';
   +import { calculationBindings, methodologyCoverage, type MethodologyCoverageDto } from './api';
   ```
   Додати допоміжну функцію над `BindingList`:
   ```ts
   /** Підпис прив'язки `таблиця.колонка` кодами з переліку прив'язок методології (F-21); ідентифікатори — лише запасом. */
   type BindingCodes = ReadonlyMap<number, { readonly table: string | null; readonly column: string | null }>;

   function codesOf(list: readonly CalculationBindingDto[] | undefined): BindingCodes {
     return new Map((list ?? []).map((b) => [b.id, { table: b.tableCode ?? null, column: b.columnCode ?? null }]));
   }

   function bindingLabel(binding: CalculationBindingDto, codes: BindingCodes): string {
     const known = codes.get(binding.id);
     const table = binding.tableCode ?? known?.table ?? null;
     const column = binding.columnCode ?? known?.column ?? null;
     return table !== null && column !== null ? `${table}.${column}` : `${String(binding.tableDefId)}.${String(binding.columnDefId)}`;
   }
   ```
   ```diff
   -function BindingList({
   -  bindings,
   -}: {
   -  readonly bindings: readonly CalculationBindingDto[];
   -}): JSX.Element {
   +function BindingList({
   +  bindings,
   +  codes,
   +}: {
   +  readonly bindings: readonly CalculationBindingDto[];
   +  readonly codes: BindingCodes;
   +}): JSX.Element {
      return (
        <Group gap="xs">
          {bindings.map((binding) => (
   -        <Code key={binding.id}>{`${String(binding.tableDefId)}.${String(binding.columnDefId)}`}</Code>
   +        <Code key={binding.id}>{bindingLabel(binding, codes)}</Code>
          ))}
   ```
   У `MethodologyCoveragePanel` після `const coverage = useQuery({…});` додати таке. Ключ той самий, що в `MethodologyBindingsPanel` на цій сторінці, тож запит іде з кешу:
   ```ts
   // F-21: коди таблиці й колонки — з переліку прив'язок методології (той самий ключ, що в панелі прив'язок цієї сторінки).
   const bindingList = useQuery({
     queryKey: queryKeys.methodologies.bindings(methodologyId),
     queryFn: () => calculationBindings(methodologyId),
   });
   const codes = codesOf(bindingList.data);
   ```
   ```diff
   -                          <BindingList bindings={output.bindings} />
   +                          <BindingList bindings={output.bindings} codes={codes} />
   ```
   ```diff
   -                        <Table.Td>{binding.tableDefId}</Table.Td>
   -                        <Table.Td>{binding.columnDefId}</Table.Td>
   +                        <Table.Td>{binding.tableCode ?? codes.get(binding.id)?.table ?? <Code>{binding.tableDefId}</Code>}</Table.Td>
   +                        <Table.Td>{binding.columnCode ?? codes.get(binding.id)?.column ?? <Code>{binding.columnDefId}</Code>}</Table.Td>
   ```
   Ключі заголовків не змінюються: тексти вже «Table» / «Column». Запасний вигляд ідентифікатора — `<Code>`, як у `DocumentsPage.tsx:263-266` (форма «це ідентифікатор»).

**Тест-доказ:** `src/Ecr.Web/src/features/methodologies/__tests__/MethodologyCoveragePanel.test.tsx`.
- У `mockApi` додати маршрут `/\/methodologies\/\d+\/bindings$/`, що повертає прив'язки `{ id: 1, tableCode: 'EMIS', columnCode: 'NOX_T', … }` і `{ id: 9, tableCode: 'EMIS', columnCode: 'OLD_T', … }`.
- Кейс у `:111`: замінити `toContain('7.101')` на `toContain('EMIS.NOX_T')` і додати `not.toContain('7.101')`.
- Кейс у `:151`: `toContain('OLD_T')` і `not.toContain('55')`.
- Новий кейс `'перелік прив'язок не прочитався — ідентифікатор у <Code>'`: маршрут `/bindings` повертає 500, тож показано `7.101` усередині `code`.
- Без зміни в `BindingList` перші два кейси падають: у тексті `7.101`.

**Перевірка після впровадження:**
- `npx vitest run src/features/methodologies/__tests__/MethodologyCoveragePanel.test.tsx`;
- `npx tsc --noEmit`;
- a11y-фікстура покриття: `src/Ecr.Web/src/test/__tests__/a11yFixtures.tsx`, пошук `RuleCoverageFixture` / `coverage`. Додати мок `/bindings`, якщо фікстура рендерить цю панель (`npx vitest run --config vitest.a11y.config.ts`);
- стенд: секція «Output coverage» показує коди.

**Ризики і побічні ефекти:**
- Додатковий запит лише коли панель відкрита окремо від панелі прив'язок; на сторінці версії він із кешу.
- Сервер, тести фіксованої кількості запитів і бюджет не зачеплено (`calculationBindings` уже в чанку `methodologies/api`).

**Обґрунтування.** Альтернатива — збагатити `MethodologyCoverageHandler` кодами на сервері, як `ListCalculationBindingsHandler`. Це правильніше для інших споживачів API, але додає N запитів `FindColumnAsync` і зачіпає серверні тести. Обрано клієнтський шлях як найдешевший. Серверне збагачення можна зробити окремо, і клієнт тоді візьме `binding.tableCode` першим.

**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням.
**Залежності:** —

---

### U-3 — Medium — «Remove URL» вебхука: незворотне видалення одним натиском, без підтвердження

**Категорія:** ui / data-safety

**Де:** `src/Ecr.Web/src/features/notifications/ChannelsPanel.tsx:441-452` (кнопка) і `:113-123` (`saveSecret`).

**Проблема:**
```tsx
<Button
  variant="default"
  color="statusError"
  loading={saveSecretLoading}
  disabled={!secretFor.hasSecret}
  onClick={() => {
    if (saveSecret.isPending) return;
    saveSecret.mutate({ id: secretFor.id, secret: null });
  }}
>
  {t('notifications.clearWebhook')}
```

Один клік стирає адресу вебхука Teams. Вона секретна: сервер її не віддає (коментар `:419-421`), тож відновити можна лише знову знайти URL у Teams. Канал лишається ввімкненим і далі нічого не доставляє.

Той самий файл у `:56-59` пояснює, чому видалення КАНАЛУ йде через `ConfirmModal`: «⛔ R-06/X-01: канал видалявся одним натисканням… сповіщення перестають доходити, і людина дізнається про це… з тиші». Для видалення адреси аргумент той самий, а підтвердження немає.

До того ж `variant="default"` гасить `color="statusError"` (див. U-5), тож кнопка виглядає так само, як нейтральна.

**Сценарій відтворення:** `/admin/notifications` → канал Teams з адресою → «Webhook URL» → «Remove URL». Адресу стерто одразу, з тостом «saved». Очікувано: діалог з назвою каналу й наслідком. Правило: `DIRECTIVE-14-UIUX.md` UX-07 («Руйнівна дія — через одне підтвердження з назвою»).

**Вплив:** адміністратор сповіщень. Випадковий клік (кнопка стоїть поруч із «Save») мовчки вимикає доставку в канал.

**Зміна — покроково:**

1. Файл `src/Ecr.Web/src/features/notifications/ChannelsPanel.tsx`.
   ```diff
      const [removing, setRemoving] = useState<NotificationChannel | null>(null);
   +  // U-3: стерти адресу вебхука — незворотно (сервер її не віддає), тож через підтвердження, як і видалення каналу.
   +  const [clearingSecret, setClearingSecret] = useState(false);
   ```
   ```diff
   -    onSuccess: async () => {
   +    onSuccess: async () => {
          await invalidate();
          setSecretFor(null);
          setSecret('');
   +      setClearingSecret(false);
          showDone(t('notifications.secretSaved'));
        },
   ```
   ```diff
                <Button
   -                variant="default"
   +                variant="outline"
                    color="statusError"
   -                loading={saveSecretLoading}
                    disabled={!secretFor.hasSecret}
   -                onClick={() => {
   -                  if (saveSecret.isPending) return;
   -                  saveSecret.mutate({ id: secretFor.id, secret: null });
   -                }}
   +                onClick={() => setClearingSecret(true)}
                  >
                    {t('notifications.clearWebhook')}
                  </Button>
   ```
   Після закриття `</Modal>` секрету (перед `<ConfirmModal opened={removing !== null}`) додати:
   ```tsx
   <ConfirmModal
     opened={clearingSecret && secretFor !== null}
     title={t('notifications.clearWebhookTitle', { name: secretFor?.name ?? '' })}
     text={t('notifications.clearWebhookText')}
     verb={t('notifications.clearWebhook')}
     isPending={saveSecret.isPending}
     onConfirm={() => {
       if (secretFor !== null && !saveSecret.isPending) saveSecret.mutate({ id: secretFor.id, secret: null });
     }}
     onClose={() => setClearingSecret(false)}
   />
   ```
   Пропи `ConfirmModal` (`opened`, `title`, `text`, `verb`, `isPending`, `onConfirm`, `onClose`) — `shared/ui/ConfirmModal.tsx:49-79`. `danger` за замовчуванням червоний, як у виклику `:468-477`.

2. Новий файл `src/Ecr.Infrastructure/Persistence/Sql/seed/features/NOTIF-clear-webhook-confirm.sql`:
   ```sql
   -- NOTIF:clear-webhook-confirm ── ru/kz підтвердження «Remove URL» (U-3); власна порція ──
   INSERT INTO #I18N ([Key], Lang, Val)
   SELECT v.[Key], v.Lang, v.Val
     FROM (VALUES
       (N'notifications.clearWebhookTitle', N'ru', N'Удалить URL веб-хука канала «{name}»?'),
       (N'notifications.clearWebhookTitle', N'kz', N'«{name}» арнасының веб-хук URL мекенжайын жою керек пе?'),
       (N'notifications.clearWebhookText', N'ru', N'Адрес нельзя будет посмотреть снова — его придётся заново скопировать из Teams. Пока адреса нет, уведомления в этот канал не доставляются.'),
       (N'notifications.clearWebhookText', N'kz', N'Мекенжайды қайта көру мүмкін болмайды — оны Teams-тен қайта көшіру керек. Мекенжай жоқ кезде бұл арнаға хабарландырулар жеткізілмейді.')
          ) AS v ([Key], Lang, Val)
   OPTION (RECOMPILE);
   GO
   -- NOTIF:clear-webhook-confirm ── кінець секції ──

   -- NOTIF:clear-webhook-confirm ── en; ru/kz — порцією вище ──
   INSERT INTO #UiEn ([Key], Lang, Val, Scope)
   SELECT v.[Key], v.Lang, v.Val, v.Scope
     FROM (VALUES
       (N'notifications.clearWebhookTitle', N'en', N'Remove the webhook URL of channel "{name}"?', 1),
       (N'notifications.clearWebhookText', N'en', N'The address cannot be viewed again — you will have to copy it from Teams anew. Until then notifications to this channel are not delivered.', 1)
          ) AS v ([Key], Lang, Val, Scope)
   OPTION (RECOMPILE);
   GO
   -- NOTIF:clear-webhook-confirm ── кінець секції ──
   ```

**Тест-доказ:** `src/Ecr.Web/src/features/notifications/__tests__/ChannelsPanel.test.tsx`, новий кейс `'Remove URL питає підтвердження і лише після нього шле secret: null'`.
- Фікстура: канал `{ id: 3, kind: 'TeamsWebhook', hasSecret: true, name: 'Teams duty', … }`.
- `mockServer` записує `PUT`/`POST` на `/channels/3/secret`.
- Кроки: клік «notifications.setWebhook» → клік «notifications.clearWebhook» → запиту ще немає, видно діалог з `notifications.clearWebhookTitle` → клік дієслова в діалозі → запит із тілом `secret: null`.
- Без зміни кроку 1 кейс падає на «запиту ще немає».

**Перевірка після впровадження:**
- `npx vitest run src/features/notifications/__tests__/ChannelsPanel.test.tsx src/features/notifications/__tests__/NotificationPanels.a11y.test.tsx`;
- `dotnet test tests/Ecr.Architecture.Tests --filter "FullyQualifiedName~Seed|FullyQualifiedName~EndpointCoverage"`. Це перевіряє, що ключі `t('notifications.clearWebhookTitle')`/`Text` є в сіді, а підстановки ru/kz збігаються з en (`SeedTranslationTests.Підстановки_перекладу_збігаються_з_оригіналом`).

**Ризики і побічні ефекти:**
- Вкладений `ConfirmModal` поверх `Modal` — такий прийом уже є (`AddValueDialog` у `RowDrawer`).
- Перевірити повернення фокуса після Esc на кнопку «Remove URL»; за потреби — `returnFocusTo`, як `LifecycleDialogs.tsx:129`.

**Обґрунтування.** Undo-тост тут неможливий: сервер секрет не повертає, тож компенсувати нічим. Лишається підтвердження.

**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням.
**Залежності:** U-5 (варіант кнопки змінюється тут же, в U-5 цей рядок не дублювати).

---

### U-4 — Low — Тип дозволу показано сирим кодом у шапці «лише читання» і в діалозі «Activate»

**Категорія:** ui

**Де:**
- `src/Ecr.Web/src/features/permits/permit/HeaderForm.tsx:88`;
- `src/Ecr.Web/src/features/permits/permit/LifecycleDialogs.tsx:122`.

Для порівняння: `src/Ecr.Web/src/pages/admin/PermitPage.tsx:152` (`localName(permit.typeNameL10n) || permit.typeCode`) і `src/Ecr.Web/src/features/permits/list/PermitLifecycleDialogs.tsx:96-97` (`permitTypeName(item, lang)`).

**Проблема:**
```tsx
{ label: t('permits.page.header.type'), value: permit.typeCode },
...
t('permits.page.activate.reads', { type: permit.typeCode, from, to: toEnd }),
```

`PermitDto` несе `typeNameL10n` (`api/schema.d.ts`, `PermitDto.typeNameL10n`). Підзаголовок сторінки показує назву, а шапка «лише читання» і діалог активації на тій самій сторінці — код (`WaterDischarge`). Діалог активації в переліку для того самого речення бере назву.

**Сценарій відтворення:**
- Закінчений дозвіл → Overview: «Type: WaterDischarge», тоді як підзаголовок каже «Water discharge».
- Чернетка → «Activate…»: «Sheets bound to type WaterDischarge…».

Очікувано назва мовою інтерфейсу. Правило: `DIRECTIVE-14-UIUX.md` §2.3 «Один зразок на один випадок» і UX-10 («код аркуша, не назва» — той самий клас).

**Вплив:** глядачі закінчених дозволів і всі, хто активує дозвіл. У ru/kz код латиницею посеред речення.

**Зміна — покроково:**

1. `HeaderForm.tsx`: додати `import { localName } from './names';`.
   ```diff
   -        { label: t('permits.page.header.type'), value: permit.typeCode },
   +        { label: t('permits.page.header.type'), value: localName(permit.typeNameL10n) || permit.typeCode },
   ```

2. `LifecycleDialogs.tsx`: додати `import { localName } from './names';`.
   ```diff
   -            t('permits.page.activate.reads', { type: permit.typeCode, from, to: toEnd }),
   +            t('permits.page.activate.reads', { type: localName(permit.typeNameL10n) || permit.typeCode, from, to: toEnd }),
   ```

**Тест-доказ:**
- `PermitPage.test.tsx` → кейс `'закінчений дозвіл і глядач бачать перелік пар, а не форму'` (`:211`): додати `expect(screen.getByText('Water discharge', { selector: 'dd' })).toBeTruthy()`. Фікстура `permitFixtures.ts:89-90` має `typeNameL10n: { en: 'Water discharge' }`.
- Кейс `'Activate: підтвердження викликає activatePermit(5)'` (`:335`): `dialog.textContent` містить `Water discharge`, а не `WaterDischarge`.
- Без зміни обидва падають.

**Перевірка після впровадження:** `npx vitest run src/features/permits/permit/__tests__/PermitPage.test.tsx src/features/permits/permit/__tests__/LifecycleDialogs.p3.test.tsx`.

**Ризики і побічні ефекти:** `names.ts` уже в чанку шапки (`names.ts:4-7`), тож бюджет не зростає.
**Обґрунтування.** Те саме правило «назва → код запасом», що вже діє в `PermitPage.tsx:152`.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням.
**Залежності:** робити разом з U-1 (той самий `HeaderForm.tsx`).

---

### U-5 — Low — Руйнівні кнопки `variant="default" color="statusError"` виглядають нейтральними

**Категорія:** ui-consistency

**Де** (6 місць):
- `src/Ecr.Web/src/features/documents/sheetCopies/SheetCopyBar.tsx:136-140` — «Delete copy»;
- `src/Ecr.Web/src/features/notifications/ChannelsPanel.tsx:441-443` — «Remove URL»; виправляється в U-3;
- `src/Ecr.Web/src/features/permits/groups/GroupDrawer.tsx:274` — «Delete group»;
- `src/Ecr.Web/src/features/permits/nae/NodeLifecycleActions.tsx:102-105` — «Delete» вузла НАЕ;
- `src/Ecr.Web/src/features/permits/permit/RowDrawer.tsx:254` — «Delete row»;
- `src/Ecr.Web/src/features/templates/PeriodAccessRuleEditor.tsx:405-407` — «Delete» правила доступу.

**Проблема.** У Mantine 7.15 варіант `default` ігнорує `color`. Див. `node_modules/@mantine/core/esm/core/MantineProvider/color-functions/default-variant-colors-resolver/default-variant-colors-resolver.mjs:175-181`:

```js
if (variant === "default") { return { background: "var(--mantine-color-default)", … color: "var(--mantine-color-default-color)", … } }
```

У `theme.ts` власного `variantColorResolver` немає (grep порожній). Отже руйнівна кнопка малюється так само, як «Cancel» поруч. Конвенція `docs/design/ui-conventions.md`, «Варіанти — чотири ролі»: руйнівна дія — `color="statusError" variant="outline"` у панелі. Решта 53 кнопки з `statusError` у коді вже `filled`/`outline`/`subtle`/`light` (сканування `<Button … statusError>`: filled 15, outline 5, subtle 32, light 1).

**Сценарій відтворення:** шторка групи дозволів → «Delete group» сіра, як «Cancel». Очікувано червона обвідна. Правило — конвенція вище й UX-07 («червона кнопка з дієсловом»).

**Вплив:** 6 екранів. Руйнівна дія не виділена, тож її легше натиснути помилково. Підтвердження є всюди, крім U-3 і `RowDrawer` (там undo-тост).

**Зміна — покроково:**
1. У кожному з 6 місць `variant="default"` → `variant="outline"` (атрибут `color="statusError"` лишається). Приклад `GroupDrawer.tsx:274`:
   ```diff
   -              <Button variant="default" color="statusError" data-group-delete="" onClick={() => setConfirmingDelete(true)}>
   +              <Button variant="outline" color="statusError" data-group-delete="" onClick={() => setConfirmingDelete(true)}>
   ```
   Так само `SheetCopyBar.tsx:138`, `NodeLifecycleActions.tsx:104`, `RowDrawer.tsx:254`, `PeriodAccessRuleEditor.tsx:406`. `ChannelsPanel.tsx:442` — у U-3.

2. Сторож: файл `src/Ecr.Web/src/shared/theme/__tests__/buttonScale.test.tsx`, функція `violations` (рядки 144-164). Перед `return found;` додати:
   ```ts
   // U-5: `variant="default"` ігнорує `color` (Mantine defaultVariantColorsResolver) — руйнівна кнопка малювалася б нейтральною.
   if (tag.component === 'Button' && props.get('variant') === '"default"' && props.get('color') === '"statusError"') {
     found.push(`${where}: variant="default" гасить color="statusError" — руйнівна дія: variant="outline" (ui-conventions.md)`);
   }
   ```
   І рядок у `it.each` (поруч із `:178-189`): `['variant="default" color="statusError"', true]`.
   ⚠ Перевірити, як `it.each` перетворює рядок на тег. Якщо він рендерить лише `size`/`style`, додати окремий `it` з `violations({ file: 'x', line: 1, component: 'Button', attrs: ' variant="default" color="statusError"' })`.

**Тест-доказ:** `buttonScale.test.tsx` → `'жодна кнопка… не виходить зі шкали'`. До зміни кроку 1 сторож червоний з 6 порушеннями, після — зелений.
**Перевірка після впровадження:** `npx vitest run src/shared/theme/__tests__/buttonScale.test.tsx`; a11y обох тем: `npx vitest run --config vitest.a11y.config.ts`, бо контраст `statusError` outline уже в `contrast.test.ts`.
**Ризики і побічні ефекти:** лише вигляд, без зміни поведінки.
**Обґрунтування.** `outline` — варіант конвенції для панелі; `filled` — лише дієслово всередині діалогу підтвердження.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням коду Mantine.
**Залежності:** U-3 (місце в `ChannelsPanel`).

---

### U-6 — Low — Причина недоступності дії лише в `title` / на нефокусовній кнопці (permits)

**Категорія:** a11y

**Де:**
- `src/Ecr.Web/src/features/permits/typeEditor/TypeEditorHeader.tsx:92-98` — «Publish…» з `disabled` + `title`;
- `src/Ecr.Web/src/features/permits/bindings/PermitRefreshStrip.tsx:76-82` — «Refresh» з `disabled={readOnly}` + `title`;
- `src/Ecr.Web/src/features/permits/permit/TableTab.tsx:58-73` — три кнопки з `disabled` + `title` + `aria-describedby` на `VisuallyHidden`.

**Проблема:**
```tsx
<Button onClick={actions.openPublish} disabled={!mayPublish}
  title={mayPublish ? undefined : t('permits.editor.actions.publishNoRight')}>
```

Нативна `disabled` кнопка не потрапляє в порядок табуляції. Користувач клавіатури без миші не дізнається причини ніде: `title` показується лише при наведенні, а `aria-describedby` читається лише в режимі перегляду читалки.

У проєкті для цього є готовий зразок `features/common/DisabledReason.tsx:20-30`:

> «⛔ `data-disabled` + `aria-disabled`, а не `disabled`… вимкнена кнопка не отримує ні фокуса, ні наведення, і підказку не прочитав би ніхто»

Він уже вживаний (`DocumentsPage` recalc, `ReportDefinitionsModal.tsx:388`).

**Сценарій відтворення:** користувач лише з правом `Permit.EditType` → редактор типу → Tab по шапці. «Publish…» пропускається, причини не видно. Очікувано: фокус на кнопці, підказка «потрібне право публікації». Правило: `DIRECTIVE-14-UIUX.md` §2.4 («Чому не можна»), ФВ-14.16 (уся робота з клавіатури).

**Вплив:** користувачі клавіатури; три екрани permits.

**Зміна — покроково:**

1. `TypeEditorHeader.tsx`: `import { DisabledReason } from '@/features/common/DisabledReason';`.
   ```diff
   -              <Button
   -                onClick={actions.openPublish}
   -                disabled={!mayPublish}
   -                title={mayPublish ? undefined : t('permits.editor.actions.publishNoRight')}
   -              >
   -                {t('permits.editor.actions.publish')}
   -              </Button>
   +              <DisabledReason reason={mayPublish ? null : t('permits.editor.actions.publishNoRight')}>
   +                <Button onClick={actions.openPublish}>{t('permits.editor.actions.publish')}</Button>
   +              </DisabledReason>
   ```

2. `PermitRefreshStrip.tsx` — те саме для кнопки «Refresh»: `reason={readOnly ? t('permits.sheetBinding.doc.readOnly') : null}`, без `disabled`/`title`, з тим самим імпортом.

3. `TableTab.tsx`:
   - прибрати `reasonId`, `describedBy` і `VisuallyHidden` (рядки 58-60, 64), а з імпортів — `useId` і `VisuallyHidden`;
   - кожну з трьох кнопок обгорнути: `<DisabledReason reason={readOnlyReason ?? null}><Button …>…</Button></DisabledReason>`, без `disabled`, `title`, `aria-describedby`.

4. Тести, що закріплюють старий вигляд (оновити, не видаляти):
   - `features/permits/typeEditor/__tests__/EditorRights.test.tsx:145-157` — замість `.disabled` і `title` перевіряти `aria-disabled="true"` і текст причини через `aria-describedby` (як у тестах `DisabledReason`);
   - `features/permits/bindings/__tests__/PermitRefreshStrip.test.tsx:155-159`;
   - `features/permits/permit/__tests__/PermitPage.test.tsx:657` і `:665-675`.

**Тест-доказ:** у кожному оновленому кейсі ще й перевірити, що клік по недоступній кнопці НЕ викликає дію: `openPublish` / `refreshPermitBinding` / відкриття шторки. Без `DisabledReason` (лише `aria-disabled`) клік пройшов би — це мутаційний доказ того, що `DisabledReason` гасить `onClick`.

**Перевірка після впровадження:**
- `npx vitest run src/features/permits/typeEditor/__tests__/EditorRights.test.tsx src/features/permits/bindings/__tests__/PermitRefreshStrip.test.tsx src/features/permits/permit/__tests__/PermitPage.test.tsx`;
- a11y: `npx vitest run --config vitest.a11y.config.ts`.

**Ризики і побічні ефекти:** `Hint` у `DisabledReason` — `shared/ui`, уже в чанках цих екранів.
**Обґрунтування.** Один зразок на випадок (§2.3); зразок уже доведений тестами.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням.
**Залежності:** —

---

### U-7 — Low — Тривалість прогону збору форматується `toFixed(1)` — крапка в ru/kz

**Категорія:** i18n

**Де:**
- `src/Ecr.Web/src/features/integration/CollectionRunsPanel.tsx:69-73`;
- `src/Ecr.Web/src/features/integration/CollectionRunDetailDrawer.tsx:130-134`.

**Проблема:**
```ts
return t('collectionRuns.durationSeconds', { value: (durationMs / 1000).toFixed(1) });
```

`toFixed` дає `12.3` незалежно від мови. Російська й казахська пишуть `12,3`. Сусідні колонки тієї самої таблиці (точки, дати) йдуть через `shared/format`. Лінтер ловить лише `toLocaleString()` без локалі (`eslint.config.js`, D15-09), а `toFixed` — ні.

**Сценарій відтворення:** мова ru → `/admin/sources` → «Прогоны сбора»: тривалість «12.3 с» поруч із «1 234» у колонці точок. Очікувано «12,3 с». Правило: `DIRECTIVE-14-UIUX.md` UX-11 («числа — через одну функцію»), D15-09.

**Вплив:** адміністратори джерел у ru/kz; косметика.

**Зміна — покроково:**

1. `CollectionRunsPanel.tsx`:
   ```diff
   -import { formatDateOnly, parseDateOnly } from '@/shared/format';
   +import { formatDateOnly, formatNumber, parseDateOnly } from '@/shared/format';
   ...
   -  return t('collectionRuns.durationSeconds', { value: (durationMs / 1000).toFixed(1) });
   +  return t('collectionRuns.durationSeconds', {
   +    value: formatNumber(durationMs / 1000, { minimumFractionDigits: 1, maximumFractionDigits: 1 }),
   +  });
   ```

2. `CollectionRunDetailDrawer.tsx:133` — та сама заміна, з імпортом `formatNumber` з `@/shared/format`. Сигнатура: `shared/format/number.ts:48` `formatNumber(value, options?, lang?)`.

**Тест-доказ:** новий кейс у `src/Ecr.Web/src/features/integration/__tests__/CollectionRunsPanel.render.test.tsx` (і аналогічний у `CollectionRunDetailDrawer.test.tsx`): каталог мовою `ru` з рядком `collectionRuns.durationSeconds = '{value} с'`, прогін з `durationMs: 12345`. У комірці має бути `12,3 с`. З `toFixed` кейс падає (`12.3`).
**Перевірка після впровадження:** `npx vitest run src/features/integration/__tests__`.
**Ризики і побічні ефекти:** немає.
**Обґрунтування.** Єдина функція форматування — рішення UX-11.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням.
**Залежності:** —

---

### U-8 — Low — `air.css`: сирі hex і палітра Mantine замість статусних токенів

**Категорія:** theme

**Де:** `src/Ecr.Web/src/features/permits/air/air.css:343-358`.

**Проблема:**
```css
.pi-sev[data-severity='Error'] { color: #a61e1e; }
.pi-sev[data-severity='Warning'] { color: #9c3a0a; }
:root[data-mantine-color-scheme='dark'] .pi-sev[data-severity='Error'] { color: var(--mantine-color-red-4); }
:root[data-mantine-color-scheme='dark'] .pi-sev[data-severity='Warning'] { color: var(--mantine-color-orange-4); }
```

Статус передано літералом і голими `red`/`orange` (ФВ-14.11, W4.2 — `eslint.config.js` забороняє це в TSX, але CSS не лінтиться). Для цього є токени `statusError`/`statusWarning` (`shared/theme/theme.ts:77-100`) з перевіреним контрастом у `contrast.test.ts`. Інші CSS ними вже користуються (`features/documents/inspector/inspector.css:148,152`, `features/permits/history/permitMarks.css:30`). `#9c3a0a` до того ж не збігається з жодним відтінком `statusWarning` (`#a83c0b` / `#8a3008`).

**Сценарій відтворення:** майстер імпорту лімітів «Air» → таблиця проблем: «Warning» іншого відтінку, ніж попередження в інспекторі документа. Зміна палітри в `theme.ts` цей екран не зачепить. Правило: ФВ-14.11, `DIRECTIVE-14-UIUX.md` §2.5.

**Вплив:** один екран; узгодженість і стійкість до зміни теми.

**Зміна — покроково.** Файл `air.css`:
```diff
-/* Реальний axe: `red-text`/`orange-text` у цій темі дають #fa5252/#fd7e14 на сірому тлі таблиці - 3.06 і 2.39. Темніші відтінки. */
-.pi-sev[data-severity='Error'] {
-  color: #a61e1e;
-}
-
-.pi-sev[data-severity='Warning'] {
-  color: #9c3a0a;
-}
-
-:root[data-mantine-color-scheme='dark'] .pi-sev[data-severity='Error'] {
-  color: var(--mantine-color-red-4);
-}
-
-:root[data-mantine-color-scheme='dark'] .pi-sev[data-severity='Warning'] {
-  color: var(--mantine-color-orange-4);
-}
+/* U-8: статус — токенами теми (ФВ-14.11); `-text` сам перемикається між світлою й темною схемою, контраст — contrast.test.ts. */
+.pi-sev[data-severity='Error'] {
+  color: var(--mantine-color-statusError-text);
+}
+
+.pi-sev[data-severity='Warning'] {
+  color: var(--mantine-color-statusWarning-text);
+}
```

**Тест-доказ:** `src/Ecr.Web/src/features/permits/air/__tests__/` — новий `airCss.tokens.test.ts` за зразком `features/permits/typeEditor/__tests__/typeEditorCss.test.ts` (читає CSS як текст). Твердження: в `air.css` немає `#[0-9a-f]{3,8}` поза коментарями і немає `--mantine-color-(red|orange)-`. До зміни падає.

**Перевірка після впровадження:** `npx vitest run src/features/permits/air`; a11y обох тем (`npx vitest run --config vitest.a11y.config.ts`). На стенді — axe на майстрі імпорту «Air», крок попереднього перегляду (контраст на сірому тлі таблиці).

**Ризики і побічні ефекти:** контраст `statusError-text` (світла схема = відтінок 6, `#c92a2a`) на сірому тлі `.pi-…` таблиці слід заміряти axe. Коментар у файлі каже, що `red-6` (`#fa5252`) давав 3.06, але `#c92a2a` значно темніший. Якщо axe покаже < 4.5 — лишити `#a61e1e`/`#8a3008` як `--ecr-*` токени в `shared/theme/tokens.css`, а не в компонентному CSS.

**Обґрунтування.** Токен — єдине джерело. Альтернатива «новий токен» — лише якщо вимір покаже недостатній контраст.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням; контраст — потребує перевірки на стенді (axe).
**Залежності:** —

---

### U-9 — Low — Екран входу без підказки Caps Lock

**Категорія:** ux / a11y

**Де:** `src/Ecr.Web/src/pages/LoginPage.tsx:425-431` (`PasswordInput`). Вимога: `docs/build/DIRECTIVE-14-UIUX.md` UX-16 («Додати: підказку Caps Lock»). `git grep -i "caps lock\|capslock" -- src/Ecr.Web/src` знаходить лише гарячі клавіші сітки.

**Проблема.** Невдала спроба з увімкненим Caps Lock дає загальне «невірний логін або пароль» (`ErrorAlert`, `:442`), без підказки причини. Після кількох невдалих спроб обліковий запис блокується (`src/Ecr.Application/Ports/IUserStore.cs:363-378`, `maxFailedAttempts`/`lockoutMinutes`).

**Сценарій відтворення:** увімкнути Caps Lock → ввести пароль → «Sign in» → відмова без натяку. Очікувано: під полем «Caps Lock увімкнено», поки фокус у полі пароля.

**Вплив:** усі локальні входи; дрібно, але щодня.

**Зміна — покроково:**

1. `LoginPage.tsx`:
   ```diff
   -import { useState, type JSX } from 'react';
   +import { useState, type JSX, type KeyboardEvent } from 'react';
   ...
      const [password, setPassword] = useState('');
   +  // U-9 (UX-16): підказка Caps Lock — лише поки фокус у полі пароля.
   +  const [capsLock, setCapsLock] = useState(false);
   +  const trackCapsLock = (event: KeyboardEvent<HTMLInputElement>): void => setCapsLock(event.getModifierState('CapsLock'));
   ...
                   <PasswordInput
                     label={t('login.password')}
                     value={password}
                     onChange={(event) => setPassword(event.currentTarget.value)}
   +                 onKeyDown={trackCapsLock}
   +                 onKeyUp={trackCapsLock}
   +                 onBlur={() => setCapsLock(false)}
   +                 description={capsLock ? t('login.capsLockOn') : undefined}
                     autoComplete="current-password"
                     visibilityToggleButtonProps={passwordToggleProps()}
                   />
   ```
   `description` Mantine зв'язує з полем через `aria-describedby`, тож читалка чує підказку разом із полем.

2. Новий файл `src/Ecr.Infrastructure/Persistence/Sql/seed/features/LOGIN-caps-lock.sql`, з публічною областю `0`, бо екран входу вантажить `?scope=public`:
   ```sql
   -- LOGIN:caps-lock ── ru/kz підказки Caps Lock на екрані входу (U-9, UX-16); власна порція ──
   INSERT INTO #I18N ([Key], Lang, Val)
   SELECT v.[Key], v.Lang, v.Val
     FROM (VALUES
       (N'login.capsLockOn', N'ru', N'Включён Caps Lock'),
       (N'login.capsLockOn', N'kz', N'Caps Lock қосулы')
          ) AS v ([Key], Lang, Val)
   OPTION (RECOMPILE);
   GO
   -- LOGIN:caps-lock ── кінець секції ──

   -- LOGIN:caps-lock ── en (публічна область); ru/kz — порцією вище ──
   INSERT INTO #UiEn ([Key], Lang, Val, Scope)
   SELECT v.[Key], v.Lang, v.Val, v.Scope
     FROM (VALUES
       (N'login.capsLockOn', N'en', N'Caps Lock is on', 0)
          ) AS v ([Key], Lang, Val, Scope)
   OPTION (RECOMPILE);
   GO
   -- LOGIN:caps-lock ── кінець секції ──
   ```

**Тест-доказ:** новий `src/Ecr.Web/src/pages/__tests__/LoginPage.capsLock.test.tsx` за зразком `LoginPage.passwordToggle.test.tsx` (`Shell`, `workingCatalog` із додатковим `'login.capsLockOn': 'Caps Lock is on'`).
- `fireEvent.keyUp(passwordField, { key: 'A', getModifierState: (k: string) => k === 'CapsLock' })` → `screen.getByText('Caps Lock is on')`. jsdom не підтримує `getModifierState` у `init`, тому створити подію через `new KeyboardEvent('keyup', { modifierCapsLock: true })` — jsdom читає `modifierCapsLock`.
- `fireEvent.blur` → тексту немає.
- Без кроку 1 перше твердження падає.

**Перевірка після впровадження:**
- `npx vitest run src/pages/__tests__/LoginPage.capsLock.test.tsx src/pages/__tests__/LoginPage.test.tsx`;
- `dotnet test tests/Ecr.Architecture.Tests --filter "FullyQualifiedName~Seed|FullyQualifiedName~EndpointCoverage"`.

**Ризики і побічні ефекти:** екран входу — у бюджеті маршруту, приріст кілька десятків байт.
**Обґрунтування.** Пряма вимога UX-16; рішень проти неї в `docs/tz/10-decisions.md` не знайдено.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням (відсутність); підтримку `modifierCapsLock` у jsdom уточнити при написанні тесту.
**Залежності:** —

---

## Порядок впровадження

Групи без спільних файлів, їх можна робити паралельно:
- **A (permits):** U-1 + U-4 одним PR (`HeaderForm.tsx`, `PermitPage.tsx`, `NewPermitWizard.tsx`, `names.ts`, `LifecycleDialogs.tsx`, `PermitPage.test.tsx`), далі U-6 (`TableTab.tsx`, `TypeEditorHeader.tsx`, `PermitRefreshStrip.tsx` + їхні тести; `PermitPage.test.tsx` спільний з A, тому строго ПІСЛЯ A).
- **B:** U-3, потім U-5. Спільний `ChannelsPanel.tsx`: U-5 після U-3. `buttonScale.test.tsx` дасть червоне, поки обидва не влиті, тож сторож U-5 — останнім комітом.
- **C:** U-2 (`MethodologyCoveragePanel.tsx` + тест).
- **D:** U-7 (integration).
- **E:** U-8 (`air.css`).
- **F:** U-9 (`LoginPage.tsx` + сід).

Нові файли сіду (U-3, U-9) — окремі `features/*.sql`, тож конфліктів немає. U-5 (рядок `RowDrawer.tsx:254`) і U-1 зачіпають різні файли, але обидва в permits, тож тримати їх в одній лінії, щоб не було паралельних правок одного тестового файлу.

## Відкинуто

- **Англійські літерали** в `app/RenderErrorScreen.tsx:60-66,111,126-129`, `app/staleVersion.tsx:203-223`, `pages/LoginPage.tsx:62`. Свідомо: каталог на цих екранах може бути недоступним (коментарі `staleVersion.tsx:171-178`, `RenderErrorScreen.tsx:29-35`, D14-13).
- **Повнота ru/kz:** 0 справжніх прогалин; 19 кандидатів — ключі з «Прибраних» (`00-prelude.sql:1324-1380`).
- **Множина `count === 1 ?`** у `features/permits/lists/listsModel.ts:50,77`: ru/kz тексти сформульовано без узгодження («Столбцов: {count}», `seed/features/PERMITS-ui-lists.sql:33,88`), тож хибної форми немає.
- **`UnmatchedRowsBanner.tsx:236`** — власний `Intl.PluralRules` замість `formatCount`. Працює коректно (ключі one/few/other); лише дубль механізму, не дефект.
- **`<Code>{projectId}</Code>`** (`DocumentsPage.tsx:263-266`), **`<Code>{channelId}</Code>`** (`DeliveriesPanel.tsx:218-224`), **`<Code>{result.unitId}</Code>`** (`CalculationResultsPanel.tsx:283-285`) — свідомий запасний вигляд «це ідентифікатор», коли довідник не прочитався (коментарі на місці).
- **`RowDrawer` «Delete row» без підтвердження** — свідомий undo-тост (`RowDrawer.tsx:150-156`, прийом «компенсація» з `shared/ui/notify.ts:145-148`). Ризик нового `id` при відновленні залежить від того, чи посилається щось на `id` рядка дозволу. Читанням не встановлено, потребує перевірки на стенді.
- **`Timestamp` з ISO у `title`** (`shared/ui/Timestamp.tsx`) — вимога UX-11 («у підказці — ISO»).
- **`CtorTree.tsx:190` `div onClick`** — це `role="treeitem"` з `tabIndex` і `onKeyDown` (roving tabindex), доступно з клавіатури.
- **`DataSourceDrawer.tsx:178`** `disabled` + `aria-describedby` — причина видима текстом поруч (`:186-190`), тож для U-6 не кандидат. **`DeleteRowControl.tsx:57-62`** — той самий клас, що U-6, але прийом закріплено окремим тестом (`DeleteRowControl.test.tsx:82-88`), а рядок сітки має сусідню підказку. Лишено поза U-6, щоб не розширювати зміну; можна додати тим самим кроком.
- **`api/client.ts` `from` при 401 без `search`** — уже в чернетці `05-client.md` (W-?, Low); не дублюю.
- **`CollectionRunsPanel.tsx:292` «Triggered by» числом `userId`** — справжня прогалина. Але потрібна зміна контракту `CollectionRunView` (`src/Ecr.Application/Ports/ICollectionRunReader.cs:82-98`, читач `CollectionRunReader.cs:158-187`) з перегенерацією `openapi.snapshot.json`/`schema.d.ts`. Лишено кандидатом наступного проходу, щоб не роздувати цей звіт неперевіреною серверною карткою.
- **Таблиці без `ScrollContainer` у самому файлі** (44 файли, напр. `features/import/ImportPanel.tsx:362`, `features/templates/ctor/ColumnsTable.tsx:100`). Обгортку може давати батьківський компонент чи CSS-клас. Без вимірювання `scrollWidth` дефект не доведено; потребує перевірки на стенді на 1024 px.

## Перевірено й у порядку

- Літерали в JSX, атрибутах і тостах: лише свідомі запасні тексти (вище).
- `aria-label` у кожного `ActionIcon`/`CloseButton` (сканер атрибутів: 0 без назви).
- У кожного `Modal`/`Drawer` є `title` або `aria-label` (0 без).
- Глобальне вимкнення руху `prefers-reduced-motion` (`shared/theme/motion.css:8-17`) покриває 7 CSS-файлів з `transition`/`animation`.
- Сирі hex у CSS поза темою: лише маски (`#000` у `mask-image`), коментарі, `dateInput.css` (власні змінні з заміряним контрастом) і U-8.
- Подвійні сповіщення про відмову стереже `handledMutation.ratchet.test.ts`; миттєвий спінер — `pendingSpinner.ratchet.test.ts`; шкалу кнопок — `buttonScale.test.tsx`; сирі дати й стани в JSX — `eslint.config.js`.
- Повнота ru/kz для ключів, доданих після 2026-10-09: прогалин немає.
- `AirLimitsTab`: таблиці з `scope`, `th scope="row"`, кнопки «Edit» з контекстною назвою, що містить видимий текст (WCAG 2.5.3), прокрутка в `role="region" tabIndex=0`.
- `SeriesChart` (`role="img"` + підсумок + `aria-live` для показника під курсором).
- `PermitUsageTab`, `RawPointsTable` — `DataTable` з порожнім станом, станом помилки й «Show more».
- «Мої задачі»: секції з видимою відмовою й «Retry» (`ApprovalQueueSection.tsx:29-37` та ін.).

## Самоперевірка

**U-1:**
- Рядки перечитано: `PermitPage.tsx:170` (`<Tabs value={tab} onChange={selectTab} keepMounted={false}>`) і `:189-191`, `HeaderForm.tsx:99-124`, `:167-178`, `NewPermitWizard.tsx:150-166`, `PermitInput.cs:10,13`.
- Diff «було» дослівний.
- Імена існують: `registerUnsavedSource` (`shared/ui/unsavedSources.ts:27`); `keepMounted` у `TabsPanel.d.ts:6-7`; `hasUnsavedChanges` (`unsavedSources.ts:36`).
- Тест падає без фіксу: панель розмонтовується (`keepMounted={false}`), а реєстрації джерела немає — `hasUnsavedChanges()` = false.
- Не дублює: перевірено `KNOWN-ISSUES.md`, `TODO-REMAINING.md` (grep «permit», «unsaved»), чернетки `05-client.md`.

**U-2:**
- Рядки перечитано: `MethodologyCoveragePanel.tsx:37-49`, `:140-152`, `MethodologyCoverageHandler.cs:71-86`, `MethodologyAuthoringHandlers.cs:1000-1023`.
- `queryKeys.methodologies.bindings` (`api/queryKeys.ts:184`) і `calculationBindings` (`features/methodologies/api.ts:471`) існують.
- Поля `tableCode?`/`columnCode?` є в `CalculationBindingDto`.
- Тест: наявний `toContain('7.101')` змінюється на код — падає без фіксу.
- F-21 закрив лише обробник переліку, не покриття.

**U-3:**
- Рядки перечитано: `ChannelsPanel.tsx:441-452`, `:113-123`, `:56-60`.
- `ConfirmModal` імпортовано в файлі (`:19`).
- Ключі `notifications.clearWebhookTitle`/`Text` у сіді відсутні (grep), конфлікту немає.
- Тест падає: зараз запит іде одразу на клік.

**U-4:**
- `HeaderForm.tsx:88`, `LifecycleDialogs.tsx:122` перечитано.
- `localName` — `features/permits/permit/names.ts`; `typeNameL10n` — у `PermitDto` (`api/schema.d.ts`, awk по `PermitDto`).
- Фікстура `permitFixtures.ts:89-90` має назву — тест падає без фіксу.

**U-5:**
- Шість місць підтверджено сканером тегів (`variant` при `statusError`) і `sed`.
- Резолвер Mantine перевірено у файлі `default-variant-colors-resolver.mjs:175-181`.
- `violations`/`propsOf` у `buttonScale.test.tsx:98-164` — `props.get` повертає значення з лапками (`"default"`), тож порівняння з `'"default"'` коректне.
- Сторож червоний до фіксу.

**U-6:**
- Рядки перечитано: `TypeEditorHeader.tsx:92-98`, `PermitRefreshStrip.tsx:76-82`, `TableTab.tsx:58-73`.
- `DisabledReason` — `features/common/DisabledReason.tsx:30`.
- Тести, що закріплюють старе, названо: `EditorRights.test.tsx:145-157`, `PermitRefreshStrip.test.tsx:155-159`, `PermitPage.test.tsx:657,665-680`.
- Новий доказ «клік не викликає дію» падає, якщо прибрати `DisabledReason`.

**U-7:**
- `CollectionRunsPanel.tsx:69-73`, `CollectionRunDetailDrawer.tsx:130-134` перечитано.
- `formatNumber` експортується з `@/shared/format` (`shared/format/index.ts:19`).
- Тестові файли `CollectionRunsPanel.render.test.tsx` і `CollectionRunDetailDrawer.test.tsx` існують (`ls`).

**U-8:**
- `air.css:343-358` перечитано.
- Змінні `--mantine-color-statusError-text`/`statusWarning-text` уже вживані (`inspector.css:148,152`).
- Контраст на сірому тлі — позначено «потребує перевірки на стенді».

**U-9:**
- `LoginPage.tsx:1`, `:159-160`, `:425-431` перечитано.
- Scope `0` для публічних ключів — за зразком `seed/features/IDLE-signout.sql:50`.
- Ім'я файлу `LOGIN-caps-lock.sql` відповідає `SeedSplitLayoutTests.cs:263`.

**Вилучено під час перевірки:**
- кандидат «множина в listsModel» — ru/kz без узгодження;
- «англійські літерали аварійних екранів» — свідоме рішення;
- «RowDrawer без підтвердження» — свідомий undo;
- «Triggered by userId» — перенесено в «Відкинуто» як кандидат наступного проходу (потрібен контракт);
- «таблиці без ScrollContainer» — не доведено без браузера.

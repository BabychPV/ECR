import { readFileSync } from 'node:fs';
import path from 'node:path';
import { vi, beforeEach, afterEach } from 'vitest';
import type { JSX, ReactNode } from 'react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { act } from '@testing-library/react';
import { mantineProviderProps } from '@/shared/theme/provider';

/**
 * Спільні прилади для розбитого набору перевірки доступності (`ФВ-14.16`,
 * `D-127`, `Q-270`).
 *
 * ⛔ Один файл `accessibility.a11y.test.tsx` (24 маршрути × 2 схеми) виносив
 * усі перевірки в один послідовний прогін Vitest — Vitest не розпаралелює
 * `it.each` усередині ОДНОГО файлу, лише файли між собою. Замір 2026-09-11
 * (baseline) показав ХХ с на повний послідовний прогін; після розбиття на
 * чотири файли (по шість маршрутів) із `maxWorkers: 2` — ХХ с (див.
 * `vitest.a11y.config.ts` і Q-270 для точних чисел).
 *
 * ⛔ Це не рерайт перевірки: тіла тестів (рендер, `findViolations`,
 * `findKeyLikeText`, пороги) лишаються дослівно тими самими в кожному з
 * чотирьох файлів — сюди винесено лише спільні приладдя (обгортка,
 * заглушка мережі, каталог рядків, профіль прав), щоб чотири копії не
 * розійшлися одна з одною з часом. Перелік маршрутів і власне тіло `it.each`
 * лишаються в кожному файлі — це і є межа «спільне приладдя» / «що саме
 * перевіряється».
 *
 * ⛔ **Провайдер береться з `mantineProviderProps`, а не збирається тут.** До
 * цього він збирався: `<MantineProvider theme={theme}>`. Коли `UI-01` додав
 * `cssVariablesResolver`, той дійшов лише до `App.tsx` — і два з семи гейтів
 * почали проганяти застосунок на дефолтних змінних Mantine, тобто перевіряти
 * те, чого на екрані немає. Розбіжність прибрано за побудовою: новий проп
 * провайдера фізично потрапляє в обидва місця.
 */
export function Shell({
  children,
  colorScheme,
  client = createScanClient(),
}: {
  children: ReactNode;
  colorScheme: 'light' | 'dark';

  /** Переданий ззовні — щоб тест міг дочекатися запитів ({@link settleQueries}). */
  client?: QueryClient;
}): JSX.Element {
  return (
    <MantineProvider {...mantineProviderProps} forceColorScheme={colorScheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter>{children}</MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>
  );
}

/**
 * Та сама оболонка для сторінки, яка живе на маршруті З ПАРАМЕТРОМ (`UI-09`).
 *
 * ⛔ Доповнення, а не заміна {@link Shell}: двадцять три сторінки набору
 * читають лише `useSearchParams`/нічого і чудово скануються під голим
 * `MemoryRouter` на `/`. Картка шаблону — перша, чий зміст ЦІЛКОМ залежить від
 * `useParams().id`: без збігу маршруту `id` там `undefined`, запит вимкнено
 * (`useTemplateCard`), і axe сканував би вічний скелет — тобто сторінку без
 * того, заради чого вона існує. Це той самий відмовний режим, що вже описаний
 * тут для порожніх фікстур (`DocumentSliceFixture`), лише з боку адреси.
 *
 * ⚠ Вкласти `MemoryRouter` у `Shell` НЕ можна: React Router кидає «You cannot
 * render a <Router> inside another <Router>». Тому це окрема оболонка з
 * власним маршрутизатором, а не обгортка поверх наявної.
 */
export function RouteShell({
  children,
  colorScheme,
  path,
  entry,
  client = createScanClient(),
}: {
  children: ReactNode;
  colorScheme: 'light' | 'dark';

  /** Шаблон маршруту, як він оголошений у реєстрі (`/admin/templates/:id`). */
  path: string;

  /** Конкретна адреса, на якій сторінка рендериться. */
  entry: string;

  /** Див. той самий проп у {@link Shell}. */
  client?: QueryClient;
}): JSX.Element {
  return (
    <MantineProvider {...mantineProviderProps} forceColorScheme={colorScheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[entry]}>
          <Routes>
            <Route path={path} element={children} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>
  );
}

/**
 * Маршрут із параметром: шаблон, конкретна адреса й ознака, що сторінка
 * показує ДАНІ, а не скелет.
 *
 * ⛔ `content` обов'язковий, а не «за бажанням»: саме його відсутність і
 * дозволила сліпій плямі прожити. Під голим `Shell` сторінки з параметром
 * сканувалися як скелет або `EmptyState`, і жоден тест цього не помічав, бо
 * «axe не знайшов порушень» однаково правдиве і для порожнього екрана.
 */
export interface ParamRoute {
  /** Шаблон, як у `app/routes.ts` (`/admin/registries/:code/definition`). */
  readonly path: string;

  /** Конкретна адреса (`/admin/registries/FUEL/definition`). */
  readonly entry: string;

  /** Чекає характерний елемент, який малюється лише з даними; кидає, якщо його немає. */
  readonly content: () => Promise<unknown>;
}

/**
 * Оболонка для рядка набору: {@link RouteShell} для маршруту з параметром,
 * {@link Shell} — для решти.
 */
export function ScanShell({
  children,
  colorScheme,
  client,
  route,
}: {
  children: ReactNode;
  colorScheme: 'light' | 'dark';
  client: QueryClient;
  route: ParamRoute | undefined;
}): JSX.Element {
  return route === undefined ? (
    <Shell colorScheme={colorScheme} client={client}>
      {children}
    </Shell>
  ) : (
    <RouteShell colorScheme={colorScheme} client={client} path={route.path} entry={route.entry}>
      {children}
    </RouteShell>
  );
}

/**
 * Чекає, доки сторінка покаже дані: спершу характерний елемент маршруту з
 * параметром (якщо є), потім тишу запитів ({@link settleQueries}).
 */
export async function settlePage(client: QueryClient, route: ParamRoute | undefined): Promise<void> {
  if (route !== undefined) await route.content();

  await settleQueries(client);
}

/** `QueryClient` для сканування — той самий, що раніше створювався в оболонці. */
export function createScanClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } });
}

/** Скільки поспіль «тихих» макрозадач вважати сталим станом. */
const QuietTicks = 3;

/**
 * Чекає, доки сторінка перестане вантажити дані: нуль запитів і мутацій у
 * польоті {@link QuietTicks} макрозадачі поспіль (`ФВ-14.9`).
 *
 * ⛔ Без цього сторож ключів сканував DOM СИНХРОННО одразу після `render`, коли
 * жоден запит ще не повернувся. Сліпою плямою було все, що малюється після
 * відповіді: елементи під правом (`can(session.data, …)` — `/api/v1/me` ще
 * летить, тож `false`) і весь вміст під `AsyncBoundary` (скелет замість даних).
 * Виміряно на конструкторі довідника: з чотирьох неіснуючих ключів сторож
 * бачив лише той, що стояв у шапці БЕЗ умови; ключ під `mayEdit` у шапці і
 * обидва у переліку вкладок (з умовою і без) проходили.
 *
 * ⚠ «Поспіль», а не «один раз нуль»: запит, увімкнений відповіддю іншого
 * (`enabled: a.data !== undefined`), стартує в ефекті ПІСЛЯ рендера — між
 * ними є мить, коли в польоті нічого немає.
 *
 * ⚠ За межею часу — падіння з переліком завислих ключів, а не тихе
 * сканування: перевірка напівзавантаженої сторінки виглядала б повною і не
 * була б нею.
 */
export async function settleQueries(client: QueryClient, timeoutMs = 10_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  let quiet = 0;

  while (quiet < QuietTicks) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    quiet = client.isFetching() === 0 && client.isMutating() === 0 ? quiet + 1 : 0;

    if (quiet < QuietTicks && Date.now() > deadline) {
      const pending = client
        .getQueryCache()
        .findAll({ fetchStatus: 'fetching' })
        .map((query) => JSON.stringify(query.queryKey));
      throw new Error(`Запити не вщухли за ${timeoutMs} мс: ${pending.join(', ')}`);
    }
  }
}

/**
 * Яку(і) схему(и) проганяти (`W4.3`).
 *
 * ⛔ `ECR_A11Y_THEME` — ЄДИНЕ джерело: без нього (локальний прогін)
 * проганяються ОБИДВІ схеми в одному виклику Vitest, з ним (CI-матриця,
 * `.github/workflows/ci.yml`, джоба `a11y`) — рівно ОДНА.
 */
const RequestedTheme = process.env['ECR_A11Y_THEME'];
export const Themes: readonly ('light' | 'dark')[] =
  RequestedTheme === 'light' || RequestedTheme === 'dark' ? [RequestedTheme] : ['light', 'dark'];

/**
 * СПРАВЖНІЙ каталог рядків із `09-seed.sql` (незмінно з попереднього єдиного
 * файлу — див. коментар там-таки в git-історії).
 */
function seededStrings(): Record<string, string> {
  const seed = readFileSync(
    path.resolve(process.cwd(), '../../src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql'),
    'utf8',
  );

  // Лише блок MERGE: секції «змінені тексти»/«прибрані ключі» над ним мають
  // той самий вигляд рядка і несуть старі значення та видалені ключі.
  const start = seed.indexOf('MERGE sys_ecr.UiString AS t');
  const end = seed.indexOf(') AS s ([Key], Lang, Val, Scope)', start);
  if (start < 0 || end < 0) throw new Error('09-seed.sql: не знайдено блоку MERGE sys_ecr.UiString');

  const strings: Record<string, string> = {};
  for (const row of seed.slice(start, end).matchAll(/\(N'([^']+)',\s*N'[a-z]{2}',\s*N'([^']*)'/g)) {
    strings[row[1] ?? ''] = row[2] ?? '';
  }

  return strings;
}

export const Catalog = seededStrings();

/** Профіль `/me` для сканування — ПОВНИЙ каталог прав, а не порожній масив. */
export const FullAccessPermissions: readonly string[] = [
  'Template.View',
  'Template.Edit',
  'Template.Publish',
  'Registry.View',
  'Registry.EditData',
  'Registry.EditDefinition',
  'Registry.Publish',
  'Document.View',
  'Document.Create',
  'Document.Delete',
  'Document.Import',
  'Document.Export',
  'Document.Reopen',
  'Project.Manage',
  'Period.Configure',
  'Period.Reopen',
  'Calculation.View',
  'Calculation.EditFormula',
  'Calculation.EditConstant',
  'Calculation.EditRule',
  'Calculation.Publish',
  'Calculation.Recalculate',
  'Report.ViewRegulatory',
  'Report.BuildSnapshot',
  'Report.Export',
  'Report.EditDefinition',
  'Integration.View',
  'Integration.Manage',
  'Integration.EditSchedule',
  'Security.ManageUsers',
  'Security.ManageRoles',
  'Security.ViewAudit',
  'Security.Simulate',
  'System.ViewHealth',
  'System.RunJob',
  'System.ManageLocalization',
];

/**
 * Непорожній зріз таблиці — КОМІРКИ, а не порожня сітка (`ФВ-14.16`).
 *
 * ⛔ До цього тут повертався `{ columns: [], rows: [] }`, і це була сліпа
 * пляма гейта, а не економія: `DocumentGrid` на порожньому зрізі показує
 * порожній стан (`emptiness.ts`) і НЕ рендерить жодної комірки. Тобто гейт
 * доступності проганявся над сторінкою документа, де самого документа немає —
 * і клас дефектів «комірка виглядає не так» був для нього невидимий за
 * побудовою. Виміряний дефект темної теми (текст `rgba(0,0,0,0.87)` на фоні
 * `#242424`, 1.35:1) проїхав саме тут.
 *
 * ⚠ Зріз навмисно містить ЧОТИРИ різні стани комірки одночасно: звичайну
 * редаговану (`C1`/`r1`), обчислену (`C2` — `CalculatedCell` у
 * `cellPermissions`), readonly-колонку (`C3`) і осиротілий рядок (`r2`). Один
 * рядок «щоб було» перевіряв би лише найпростіший випадок, а дефект жив саме
 * у різниці між станами.
 *
 * ⚠ Контраст axe в jsdom НЕ рахує (`test/a11y.ts`: правило `color-contrast`
 * потребує `<canvas>`), тому гейтом саме на контраст лишається
 * `features/grid/__tests__/gridCellContrast.test.ts`. Цей зріз закриває іншу
 * половину: усе, що axe таки вміє — імена, ролі, `aria-*`, заголовки
 * таблиці, — тепер має на чому спрацювати.
 */
export const DocumentSliceFixture = {
  cellConfirmations: {} as Record<string, string>,
  cellPermissions: { 'r1:C2': 'CalculatedCell', 'r2:C2': 'CalculatedCell' },
  periodKey: 202601,
  tableInstanceId: 1,
  columns: [
    {
      code: 'C1',
      dataType: 'Decimal',
      defaultValue: null,
      displayFormat: null,
      header: 'Fuel burned',
      id: 1,
      isReadOnly: false,
      isRequired: false,
      isRequiredByMethodology: false,
      lookupRegistryDefId: null,
      ordinal: 0,
      unitId: null,
      unitSymbol: 't',
    },
    {
      code: 'C2',
      dataType: 'Decimal',
      defaultValue: null,
      displayFormat: null,
      header: 'Emissions',
      id: 2,
      isReadOnly: false,
      isRequired: false,
      isRequiredByMethodology: false,
      lookupRegistryDefId: null,
      ordinal: 1,
      unitId: null,
      unitSymbol: 't',
    },
    {
      code: 'C3',
      dataType: 'String',
      defaultValue: null,
      displayFormat: null,
      header: 'Source',
      id: 3,
      isReadOnly: true,
      isRequired: false,
      isRequiredByMethodology: false,
      lookupRegistryDefId: null,
      ordinal: 2,
      unitId: null,
      unitSymbol: null,
    },
  ],
  rows: [
    {
      cells: { C1: 182.5, C2: 365, C3: 'Boiler A' },
      isOrphaned: false,
      label: null,
      ordinal: 0,
      rowKey: 'r1',
      rowKind: 'Item',
      rowVersion: 'v1',
    },
    {
      cells: { C1: 12, C2: 24, C3: 'Boiler B' },
      isOrphaned: true,
      label: null,
      ordinal: 1,
      rowKey: 'r2',
      rowKind: 'Item',
      rowVersion: 'v1',
    },
  ],
};

/** Один непорожній документ для `DocumentPage` (`ФВ-14.16`). */
export const DocumentTableFixture = {
  allowsDynamicRows: false,
  maxDynamicRows: null as number | null,
  sheetCode: 'GEN',
  sheetDefId: 1,
  sheetNameL10n: { values: { en: 'General' } },
  sheetOrdinal: 0,
  tableCode: 'T1',
  tableDefId: 1,
  tableInstanceId: 1,
  tableNameL10n: { values: { en: 'Table 1' } },
  tableOrdinal: 0,
};

/**
 * Заповненість таблиць документа (`BE-10`) — НЕ порожня і НЕ однорідна.
 *
 * ⛔ Порожній масив дав би на екрані «0 / 0» і жодної крапки помилки: гейт
 * доступності сканував би панель без того, заради чого вона існує. Два рядки
 * навмисно різні — одна таблиця заповнена без помилок, друга заповнена
 * частково й має дві, тобто `summarize()` дає «1 / 2» РАЗОМ із крапкою.
 *
 * ⚠ `errorCount` не `null` у жодному рядку саме тому, що `null` означає «не
 * перевіряли», і тоді `hasErrors` теж `null` — крапка не малюється зовсім.
 */
export const DocumentTableStatusFixture = [
  { errorCount: 0, filledCells: 4, inputCells: 4, sheetCode: 'GEN', tableDefId: 1, warningCount: 0 },
  { errorCount: 2, filledCells: 1, inputCells: 6, sheetCode: 'GEN', tableDefId: 2, warningCount: 1 },
];

/**
 * Огляд кампанії (`BE-22`) — ОБРІЗАНИЙ стелею і НЕоднорідний.
 *
 * ⛔ `totalProjects` більший за довжину переліку навмисно: інакше банер
 * усічення не малюється, і гейт сканував би екран без його головного
 * попередження. Рядки різні: один проєкт завершений (у перелік відстаючих не
 * потрапляє), один прострочений із відхиленими (бейдж `danger`, тон проблеми у
 * смузі), один під загрозою без пораханого строку («строк не визначено»).
 * `totals` — по всіх п'яти проєктах, як їх рахує сервер: два невидимі за
 * стелею завершені.
 */
export const CampaignSummaryFixture = {
  periodKey: 202601,
  totalProjects: 5,
  projects: [
    {
      projectId: 1,
      projectCode: '10000',
      nameL10n: { values: { en: 'North plant' } },
      documents: 4,
      draft: 0,
      submitted: 0,
      approved: 4,
      rejected: 0,
      snapshots: 1,
      progress: 'Done',
      submissionDeadline: '2026-02-16T00:00:00+05:00',
    },
    {
      projectId: 2,
      projectCode: 'P-02',
      nameL10n: { values: { en: 'South plant' } },
      documents: 5,
      draft: 1,
      submitted: 2,
      approved: 1,
      rejected: 1,
      snapshots: 0,
      progress: 'Overdue',
      submissionDeadline: '2026-02-16T00:00:00+05:00',
    },
    {
      projectId: 3,
      projectCode: 'P-03',
      nameL10n: { values: { en: 'East plant' } },
      documents: 0,
      draft: 0,
      submitted: 0,
      approved: 0,
      rejected: 0,
      snapshots: 0,
      progress: 'AtRisk',
      submissionDeadline: null,
    },
  ],
  totals: {
    projects: 5,
    documents: 17,
    draft: 1,
    submitted: 2,
    approved: 13,
    rejected: 1,
    snapshots: 3,
    done: 3,
    overdue: 1,
    atRisk: 1,
    inProgress: 0,
  },
};

/**
 * Перелік зрізів (`/admin/snapshots`) — НЕ порожній і з усіма трьома форматами.
 *
 * ⛔ Порожній перелік показав би `EmptyState`, і axe не бачив би ні таблиці, ні
 * позначки формату (2026-09-21). `legacy` і `unknown` — дві різні позначки,
 * `current` — рядок без позначки.
 */
export const SnapshotListFixture = (['legacy', 'current', 'unknown'] as const).map(
  (hashFormat, index) => ({
    builtAt: '2026-01-15T10:00:00Z',
    contentHash: `a11y${String(index + 1)}`,
    hashFormat,
    id: index + 1,
    isCurrent: index === 1,
    periodKey: 202601,
    projectId: 1,
    reportVersionId: 1,
    rowCount: 10,
    status: 'Submitted',
  }),
);

/** Назва довідника в описі (`/registries/<code>/definition`) — те, на що чекає тест. */
export const RegistryDefinitionName = 'Fuel types';

/**
 * Чернетка опису довідника (`BE-24` крок 2) — НЕ `draft: null`.
 *
 * ⛔ Порожня відповідь сховала б від сканера рівно те, що крок 2 додав на цей
 * екран: смугу «є незбережена чернетка» (жива область `role="alert"`), кнопки
 * публікації й скасування та поле причини. Гейт `a11y` сканував би сторінку в
 * стані, у якому ці вузли не малюються взагалі, — і мовчав би про них назавжди
 * (той самий відмовний режим, що вже описаний тут для `TemplateStructureFixture`).
 *
 * ⚠ Одне НОВЕ поле (`id: null`) і порожні правила: цього досить, щоб форма
 * вважалася повною (`isFieldComplete`), а кнопка збереження — доступною.
 */
export const RegistryDefinitionDraftFixture = {
  definitionVersion: 1,
  draft: {
    baseDefinitionVersion: 1,
    fields: [
      {
        id: null,
        code: 'GRADE',
        nameL10n: { values: { en: 'Grade' } },
        dataType: 'String',
        ordinal: 1,
        isRequired: false,
        isKey: false,
        lookupRegistryDefId: null,
        unitId: null,
      },
    ],
    rules: [],
    reason: 'grade added for the 2026 reporting form',
    updatedAt: '2026-09-21T08:30:00Z',
    updatedByUserId: 0,
    rowVersion: 'AQID+f/9Ng==',
  },
};

/**
 * Структура версії шаблону: один аркуш з однією таблицею (`/admin/templates/:id/versions/:versionId`).
 *
 * ⛔ Не порожня — див. коментар біля відповіді в {@link emptyBodyFor}. Таблиця
 * без колонок і рядків навмисно: аркуш уже дає перелік (акордеон), а повна
 * таблиця потягла б редактори колонок, яких цей скан не стосується.
 */
export const TemplateStructureFixture = {
  groupRules: [],
  isEditable: true,
  presentationRevision: 3,
  templateVersionId: 1,
  sheets: [
    {
      code: 'GEN',
      id: 1,
      isMandatory: true,
      isVisible: true,
      nameL10n: { values: { en: 'General sheet' } },
      ordinal: 0,
      sheetGroup: null,
      tables: [
        {
          code: 'T1',
          columns: [],
          id: 1,
          layoutKind: 'Static',
          maxDynamicRows: null,
          nameL10n: { values: { en: 'Fuel balance' } },
          ordinal: 0,
          rowMode: 'Fixed',
          rows: [],
        },
      ],
    },
  ],
};

/** Перелік версій шаблону (`/templates/<id>/versions`) — одна чернетка з `id` 1. */
export const TemplateVersionListFixture = {
  items: [
    {
      clonedFromVersionId: null,
      id: 1,
      presentationRevision: 3,
      publishedAt: null,
      status: 'Draft',
      version: '1.0',
    },
  ],
  nextCursor: null,
};

/** Зв'язки таблиць версії (`/template-versions/<id>/relations`) — один активний. */
export const TableRelationsFixture = {
  isEditable: true,
  relations: [
    {
      code: 'ROLLUP-T1',
      id: 1,
      isActive: true,
      mapJson: null,
      matchJson: '{}',
      onSourceChange: 1,
      relationKind: 'Rollup',
      sourceTableCode: 'T1',
      sourceTableDefId: 1,
      targetTableCode: 'T2',
      targetTableDefId: 2,
    },
  ],
};

/** Версії методології (`/methodologies/<id>/versions`) — одна чернетка. */
export const MethodologyVersionsFixture = [
  {
    calendarMode: 'Actual',
    createdByUserId: 1,
    effectiveFrom: null,
    id: 1,
    isEditable: true,
    level: 'Configuration',
    numericMode: 'Strict',
    status: 'Draft',
    traceLevel: 'ErrorsOnly',
    versionNumber: '2026.1',
  },
];

/**
 * Порожня відповідь ПОТРІБНОЇ форми для кожного маршруту (незмінно з
 * попереднього єдиного файлу).
 */
/**
 * Матриця покриття «рядки × правила» версії методології (`ФВ-13.9`).
 *
 * ⛔ Усі ТРИ стани, а не один: колір позначки бере тон із `toneFills`
 * (`danger`/`warning`/`muted`), і фікстура з самими покритими комбінаціями
 * лишила б два тони з трьох поза перевіркою контрасту взагалі.
 *
 * ⚠ `truncated: false` — звичайний випадок; смуга усічення малюється тим самим
 * `Banner tone="warning"`, який гейт уже сканує на огляді кампанії.
 *
 * ⚠ `null` у `values` — комірки немає; саме на ньому перевіряється, що приглушений
 * підпис «немає комірки» читається в обох темах.
 */
export const RuleCoverageFixture = {
  methodologyVersionId: 1,
  periodFrom: 202501,
  periodTo: 202699,
  tableDefIds: [1],
  columnDefIds: [101],
  rules: [
    { code: 'CO2_A', priority: 10 },
    { code: 'NOX', priority: 20 },
  ],
  combinations: [
    {
      values: ['SO2'],
      state: 'Gap',
      winnerRuleCode: null,
      matchedRuleCodes: [],
      rows: 1,
      documents: 1,
    },
    {
      values: ['CO2'],
      state: 'Conflict',
      winnerRuleCode: 'CO2_A',
      matchedRuleCodes: ['CO2_A', 'CO2_B'],
      rows: 3,
      documents: 2,
    },
    {
      values: [null],
      state: 'Covered',
      winnerRuleCode: 'NOX',
      matchedRuleCodes: ['NOX'],
      rows: 1,
      documents: 1,
    },
  ],
  truncated: false,
};

/**
 * Покриття «виходи → колонки» версії методології (`BE-25`).
 *
 * ⛔ Той самий привід, що в `RuleCoverageFixture` вище: ОБ'ЄКТ, а не `[]` за
 * замовчуванням — `MethodologyCoveragePanel` читає `outputs`/`waitingBindings`,
 * і на масиві маршрут упав би в екран помилки замість того, щоб намалювати
 * панель (та сама пастка, що з `/tables/status`).
 *
 * ⚠ Один вихід БЕЗ прив'язки (ґап-бейдж) і одна прив'язка, що чекає на вихід,
 * якого версія не оголошує (`waitingBindings`), — навмисно НЕПОРОЖНІ: інакше
 * гейт сканував би екран, де ані бейдж «нікуди не пише», ані блок «Waiting
 * bindings» жодного разу не з'явилися б, і контраст обох лишився б поза
 * перевіркою.
 */
export const MethodologyCoverageFixture = {
  methodologyVersionId: 1,
  outputs: [{ code: 'CO2', bindings: [] }],
  waitingBindings: [
    {
      id: 9,
      methodologyId: 1,
      tableDefId: 3,
      columnDefId: 55,
      outputCode: 'RETIRED',
      matchJson: '{}',
      isActive: true,
    },
  ],
};

export function emptyBodyFor(url: string): unknown {
  if (url.includes('/campaign/summary')) return CampaignSummaryFixture;

  if (url.includes('/ui-strings/')) {
    return { languageCode: 'en', revision: 1, strings: Catalog };
  }
  if (url.includes('/health/')) return { status: 'Healthy', totalDurationMs: 1, checks: [] };

  if (url.includes('/security/my-groups') || url.endsWith('/groups')) {
    return {
      userId: 0,
      userName: 'test',
      provider: 'Windows',
      principalSid: null,
      groupsFromTicket: true,
      groups: [],
      unmatchedSids: [],
      personalRoleCodes: [],
      effectiveRoleCodes: [],
      expiredRoleCodes: [],
      groupAssignmentsInSystem: [],
    };
  }
  if (url.includes('/periods')) return { projectId: 0, periods: [] };

  if (url.includes('/mapping/preview')) {
    return {
      sourceEntityId: 0,
      code: 'test',
      displayName: null,
      fromUtc: '2026-09-01T00:00:00Z',
      toUtc: '2026-09-08T00:00:00Z',
      pointsSeen: 0,
      isTruncated: false,
      fields: [],
      rows: [],
      unmappedSourceFields: [],
      uncoveredColumns: [],
    };
  }
  /*
   * ⛔ Структура КОНКРЕТНОЇ версії (`/template-versions/<число>/structure`) —
   * НЕ порожня: версія без аркушів показує `EmptyState`, і гейт сканував би
   * сторінку версії без переліку аркушів, заради якого вона існує. До
   * `RouteShell` сюди приходило `NaN` (параметра не було), тож порожня
   * відповідь нічого не ховала — тепер ховала б.
   */
  if (/\/template-versions\/\d+\/structure$/.test(url)) return TemplateStructureFixture;

  if (url.includes('/structure')) {
    return { templateVersionId: 0, presentationRevision: 0, sheets: [], isEditable: true };
  }

  // ⛔ Зв'язки КОНКРЕТНОЇ версії — один зв'язок, а не порожній перелік: інакше
  // `TableRelationsPage` показує `EmptyState`, і таблиця зв'язків не
  // рендериться взагалі.
  if (/\/template-versions\/\d+\/relations$/.test(url)) return TableRelationsFixture;

  if (url.includes('/relations')) return { isEditable: true, relations: [] };

  // ⛔ ПЕРЕД `/definition`: чернетка опису живе під тим самим префіксом
  // (`BE-24` крок 2), і зворотний порядок віддав би їй опублікований опис —
  // тобто відповідь іншої форми, у якій немає ані `draft`, ані
  // `definitionVersion` поруч із ним.
  if (url.includes('/definition/draft')) return RegistryDefinitionDraftFixture;

  if (url.includes('/definition')) {
    // ⚠ Код береться з адреси: сторінка шле `/registries/<code>/definition`, і
    // відповідь про ІНШИЙ довідник була б неправдоподібною.
    const code = /\/registries\/([^/?]+)\/definition/.exec(url)?.[1];

    return {
      id: 0,
      code: code === undefined || code === '' ? 'test' : decodeURIComponent(code),
      nameL10n: { values: code === undefined || code === '' ? {} : { en: RegistryDefinitionName } },
      isTemporal: false,
      sourceKind: 'Local',
      definitionVersion: 1,
      dataRevision: 0,
      fields: [],
      relations: [],
      rules: [],
      mappings: [],
    };
  }

  // ⛔ Точний збіг, а не `includes('/me')`: під підрядок підпадав і
  // `/methodologies`, тож сторінка методик отримувала профіль замість переліку
  // і падала на `.map`. Поки сторож сканував DOM до відповідей, цього не було
  // видно (`settleQueries`).
  if (/\/api\/v1\/me(\?|$)/.test(url)) {
    return {
      userId: 0,
      userName: 'test',
      language: 'en',
      permissions: FullAccessPermissions,
      isSimulation: false,
    };
  }

  // Налаштування користувача (`BE-20`): порожньо — оболонка лишається на
  // локальних значеннях, і гейт сканує той самий вигляд, що й раніше.
  if (url.includes('/api/v1/me/preferences')) return [];

  if (url.includes('/audit/cells')) return { items: [], nextCursor: null };

  // ⚠ Форми відповідей, а не `[]` за замовчуванням: матриця правил — об'єкт,
  // журнал доставок — сторінка з курсором (`NotificationRuleMatrix`,
  // `PagedResultOfNotificationDeliveryView`).
  if (url.includes('/notifications/rules')) return { eventKinds: [], rules: [] };
  // ⚠ Два поштові канали з РІЗНОЮ ознакою транспорту й один Teams: гейт має
  // сканувати і рядок «з налаштувань застосунку», і попередження «не
  // налаштовано» (`TransportSource`, контраст `statusWarning`), а не порожню
  // таблицю. Форма — як у видачі сервера
  // (`NotificationChannelView`, `c3652cee`): без host/port/useTls/from.
  if (/\/notifications\/channels(\?|$)/.test(url)) {
    return [
      {
        id: 1,
        kind: 'Smtp',
        name: 'Ops mailbox',
        isEnabled: true,
        hasSecret: false,
        modifiedAt: '2026-09-01T10:00:00Z',
        settings: { recipients: ['ops@example.org'], title: 'ECR' },
        transportFromConfiguration: true,
      },
      {
        id: 2,
        kind: 'Smtp',
        name: 'Legacy mailbox',
        isEnabled: true,
        hasSecret: false,
        modifiedAt: '2026-09-01T10:00:00Z',
        settings: { recipients: ['legacy@example.org'] },
        transportFromConfiguration: false,
      },
      {
        id: 3,
        kind: 'TeamsWebhook',
        name: 'Duty channel',
        isEnabled: true,
        hasSecret: false,
        modifiedAt: '2026-09-02T10:00:00Z',
        settings: { title: 'ECR' },
        transportFromConfiguration: false,
      },
    ];
  }
  if (url.includes('/notifications/deliveries')) return { items: [], nextCursor: null, totalCount: null };

  // ⛔ НЕ порожня сторінка — та сама причина, що у `DocumentSliceFixture`:
  // порожній журнал показує `EmptyState`, і таблиця знахідок не рендериться
  // взагалі. Тобто гейт доступності сканував би екран без того, заради чого
  // екран існує. Два рядки навмисно РІЗНІ: нерозв'язана помилка і закрите
  // попередження — два різні бейджі, два різні кольори.
  if (url.includes('/consistency/issues')) {
    return {
      items: [
        {
          id: 2,
          detectedAt: '2026-09-18T03:00:00Z',
          severity: 3,
          ruleCode: 'BROKEN_FK',
          entityType: 'doc.TableRow',
          entityId: 4021,
          message: 'Рядок 4021 посилається на екземпляр таблиці 77 періоду 202601, якого не існує.',
          resolvedAt: null,
          resolvedByUserId: null,
        },
        {
          id: 1,
          detectedAt: '2026-09-17T03:00:00Z',
          severity: 2,
          ruleCode: 'ORPHANED_CELL',
          entityType: 'doc.CellValue',
          entityId: 1188,
          message: 'Комірка рядка 1188 періоду 202601 посилається на запис довідника 9001, якого не існує.',
          resolvedAt: '2026-09-17T09:15:00Z',
          resolvedByUserId: 3,
        },
      ],
      nextCursor: null,
      totalCount: null,
    };
  }

  if (url.includes('/calculation-results')) return [];

  // ⛔ НЕ порожній зріз — див. `DocumentSliceFixture`: порожній означав, що
  // сітка ніколи не рендерила жодної комірки, і гейт доступності перевіряв
  // сторінку документа без документа.
  // ⛔ ПЕРЕД зрізом, а не після: `/tables/status?` підпадає під регулярку
  // зрізу нижче, і без цього рядка панель заповненості отримувала б ОБ'ЄКТ
  // зрізу замість масиву. Знайдено не читанням — `renderFeedback` дав «сіток
  // у DOM нуль замість двох»: `summarize()` кликав `.some()` на об'єкті,
  // кидав, і маршрут цілком замінювався екраном помилки.
  if (url.includes('/tables/status')) return DocumentTableStatusFixture;

  if (/\/tables\/[^/?]+/.test(url)) return DocumentSliceFixture;

  if (url.includes('/tables')) return [DocumentTableFixture];

  if (url.includes('/validation')) return { documentId: 1, messages: [], periodKey: 0 };

  if (/\/documents\/[^/?]+\?/.test(url)) {
    return {
      businessKey: 'DOC-0001',
      createdAt: '2026-01-01T00:00:00Z',
      id: 1,
      projectId: 1,
      sheetCount: 1,
      sheetStates: { GEN: 'Draft' },
    };
  }

  /*
   * ⛔ Картка ОДНОГО шаблону (`UI-09`) — ПЕРЕД переліком нижче: `/templates`
   * входить у `paged`, тож без цього рядка картка отримала б `{items: []}`
   * замість `TemplateCard`, і сторінка показала б не картку, а падіння на
   * `dependents` — тобто гейт сканував би екран помилки.
   *
   * ⛔ Числа залежних НЕнульові й РІЗНІ (2 і 5): нуль сховав би сам рядок
   * (`KeyValue`, `D15-06`), а однакові дозволили б сумі збігтися зі
   * складовою. `isActive: true` — щоб у шапці був саме той стан, у якому
   * малюється кнопка архівування з підтвердженням.
   */
  // ⚠ Перелік версій шаблону — одна ЧЕРНЕТКА: саме з нього
  // `TemplateVersionPage` знає стан версії (`canPublish`). Без параметра в
  // адресі цей запит був вимкнений (`enabled: Number.isFinite(...)`).
  if (/\/templates\/\d+\/versions(\?|$)/.test(url)) return TemplateVersionListFixture;

  // ⚠ Версії методології — одна чернетка: без неї `MethodologyVersionsPage`
  // показує `EmptyState` і не монтує жодної панелі обраної версії.
  if (/\/methodologies\/\d+\/versions$/.test(url)) return MethodologyVersionsFixture;

  // ⛔ Матриця покриття правил — ОБ'ЄКТ, а не `[]` за замовчуванням: панель
  // читає `combinations.length`, і на масиві маршрут замінювався б екраном
  // помилки (та сама пастка, що з `/tables/status` вище).
  if (url.includes('/rule-coverage')) return RuleCoverageFixture;

  // ⚠ `/coverage`, ПІСЛЯ перевірки `/rule-coverage` вище: та адреса теж
  // закінчується на «coverage», але не містить `/coverage` (там
  // `rule-coverage` одним словом через дефіс) — порядок тут не рятує від
  // цього, а лише документує, що колізії немає (перевірено `git grep`).
  if (/\/versions\/\d+\/coverage$/.test(url)) return MethodologyCoverageFixture;

  if (/\/templates\/\d+$/.test(url)) {
    return {
      code: 'TPL-A11Y',
      createdAt: '2026-01-01T00:00:00Z',
      dependents: { documents: 5, projects: 2, publishedVersions: 1, versions: 3 },
      id: 1,
      isActive: true,
      nameL10n: { values: { en: 'Stationary sources' } },
    };
  }

  /*
   * ⛔ З'єднання (`UI-09`) — НЕ порожній перелік: порожній показав би
   * `EmptyState`, і гейт сканував би `/admin/sources` без таблиці з'єднань,
   * заради якої секція існує. Одне активне й одне вимкнене — два різні бейджі
   * й `aria-label` рядка лише на другому.
   */
  /*
   * ⛔ Каталог імен джерела (`ФВ-13.13`) — ПЕРЕД переліком з'єднань нижче:
   * `/data-sources` входить у його адресу як префікс, тож без цього рядка
   * вибір імені отримав би МАСИВ з'єднань замість сторінки `{items,
   * nextCursor}` — і `page.data.items` там немає взагалі.
   *
   * ⚠ Не порожньо: елемент і його атрибут з одиницею — те саме правило, що
   * для `DocumentSliceFixture`. Саме вікно закрите за замовчуванням (`L2`),
   * тож у чинному наборі цей запит не йде; рядок існує, щоб перший тест, який
   * його відкриє, не почав із розслідування, чому сітка порожня.
   */
  if (/\/data-sources\/\d+\/catalog(\?|$)/.test(url)) {
    return {
      items: [
        {
          code: 'Unit1',
          displayName: 'Unit 1',
          path: 'Plant/Unit1',
          kind: 'Element',
          dataType: 'Element',
          unitSymbol: null,
        },
        {
          code: 'CO2',
          displayName: 'CO2 flow',
          path: 'Plant/Unit1|CO2',
          kind: 'Attribute',
          dataType: 'Float64',
          unitSymbol: 't/h',
        },
      ],
      nextCursor: null,
    };
  }

  if (url.includes('/data-sources')) {
    return [
      {
        catalog: 'ProdAF',
        code: 'PI-MAIN',
        collectionSchedules: 3,
        endpoint: 'https://pi.example.invalid/piwebapi',
        hasSecret: false,
        id: 1,
        isActive: true,
        maxParallel: 4,
        nameL10n: { en: 'Main PI server' },
        rowVersion: 'AAAAAAAAB9E=',
        secondaryEndpoint: null,
        sourceEntities: 12,
        transport: 'PiWebApi',
      },
      {
        catalog: null,
        code: 'LAB-OLD',
        collectionSchedules: 0,
        endpoint: 'https://lab.example.invalid/api',
        hasSecret: false,
        id: 2,
        isActive: false,
        maxParallel: 1,
        nameL10n: { en: 'Old lab feed' },
        rowVersion: 'AAAAAAAAC7g=',
        secondaryEndpoint: null,
        sourceEntities: 2,
        transport: 'Rest',
      },
    ];
  }

  if (url.includes('/reports/snapshots')) return SnapshotListFixture;

  /*
   * ⚠ Перелік документів — НЕ порожній і з `hasLateEdits` (`BE-09b`): інакше
   * гейт сканував би порожній стан, а позначка пізніх правок і сама таблиця
   * не потрапляли б під перевірку контрасту взагалі.
   */
  if (url.includes('/api/v1/documents?')) {
    return {
      items: [
        {
          businessKey: 'DOC-0001',
          createdAt: '2026-01-01T00:00:00Z',
          hasLateEdits: true,
          id: 1,
          projectId: 1,
          sheetCount: 1,
          sheetStates: { GEN: 'Draft' },
        },
      ],
      nextCursor: null,
    };
  }

  /*
   * ⛔ Реєстр мов — НЕ порожній, і це не «щоб було». Порожній перелік означав,
   * що на `/admin/ui-strings` не малювався ані вибір мови, ані панель обміну
   * CSV (`BE-13` ч.2): обидві не мають чого показати без мови-цілі
   * (`D15-06`). Тобто гейт доступності сканував би екран перекладу без
   * ЄДИНОГО елемента, який задає мову перекладу.
   *
   * ⚠ Склад — як у сіді (`09-seed.sql`, `MERGE sys_ecr.Language`): еталон
   * `en` і дві мови перекладу. Саме `isDefault` відрізняє їх, і рівно на цю
   * ознаку спирається `translationLanguages` — перелік з однією мовою не
   * показав би різниці між «усі мови» і «мови, крім еталона».
   */
  if (/\/api\/v1\/languages(\?|$)/.test(url)) {
    return [
      { code: 'en', nameNative: 'English', isDefault: true },
      { code: 'ru', nameNative: 'Русский', isDefault: false },
      { code: 'kz', nameNative: 'Қазақша', isDefault: false },
    ];
  }

  const paged = ['/documents', '/templates', '/users', '/projects'];

  return paged.some((entry) => url.includes(entry)) ? { items: [], nextCursor: null } : [];
}

/**
 * Реєструє заглушку `fetch` на весь файл (`beforeEach`/`afterEach`
 * кореневого рівня) — та сама поведінка, що й у попередньому єдиному файлі,
 * лише винесена сюди, щоб чотири розбиті файли викликали ОДИН і той самий
 * код, а не чотири копії, що можуть розійтися.
 */
/**
 * Ширина/висота для jsdom, щоб віртуалізована сітка взагалі намалювала рядки.
 *
 * ⛔ Без цього непорожній зріз (`DocumentSliceFixture`) не дає нічого:
 * RevoGrid віртуалізує рядки за РОЗМІРОМ вікна перегляду, а jsdom не рахує
 * розкладки і повертає нулі. Виміряно тут-таки: з порожніми розмірами
 * `revogr-data` лишається порожнім вузлом, і axe бачить сітку без жодної
 * комірки — рівно та сліпа пляма, через яку дефект контрасту й проїхав.
 *
 * ⚠ Висота з `ResizeObserver` (2400) НЕ дорівнює `clientHeight` (600)
 * навмисно: `revogr-viewport-scroll.componentDidLoad` віднімає від розміру
 * `contentRect` висоту шапки й підвалу, читаючи їх `clientHeight`. Рівні
 * значення дали б від'ємний розмір вікна перегляду — і знову жодного рядка,
 * але вже з виглядом «стаб є, отже все гаразд».
 */
const ViewportWidth = 900;
const ViewportHeight = 600;
const ObservedHeight = 2400;

/** Ставить розміри елементів і «живий» `ResizeObserver` на час файлу тестів. */
export function registerGridLayout(): void {
  const saved = new Map<string, PropertyDescriptor | undefined>();
  const savedRect = HTMLElement.prototype.getBoundingClientRect;
  const savedObserver = globalThis.ResizeObserver;

  beforeEach(() => {
    for (const [prop, value] of [
      ['clientWidth', ViewportWidth],
      ['clientHeight', ViewportHeight],
      ['offsetWidth', ViewportWidth],
      ['offsetHeight', ViewportHeight],
    ] as const) {
      saved.set(prop, Object.getOwnPropertyDescriptor(HTMLElement.prototype, prop));
      Object.defineProperty(HTMLElement.prototype, prop, { configurable: true, value });
    }

    HTMLElement.prototype.getBoundingClientRect = function rect(): DOMRect {
      return {
        x: 0,
        y: 0,
        width: ViewportWidth,
        height: ViewportHeight,
        top: 0,
        left: 0,
        right: ViewportWidth,
        bottom: ViewportHeight,
        toJSON: () => ({}),
      } as DOMRect;
    };

    // ⚠ Заглушка з `test/setup.ts` НІКОЛИ не викликає колбек — саме тому
    // сітка й не дізнавалася свого розміру. Тут колбек викликається один раз
    // одразу після `observe`, як це робить справжній браузер.
    globalThis.ResizeObserver = class {
      private readonly callback: ResizeObserverCallback;

      constructor(callback: ResizeObserverCallback) {
        this.callback = callback;
      }

      observe(target: Element): void {
        setTimeout(() => {
          this.callback(
            [
              {
                target,
                contentRect: { width: ViewportWidth, height: ObservedHeight },
              } as unknown as ResizeObserverEntry,
            ],
            this as unknown as ResizeObserver,
          );
        }, 0);
      }

      unobserve(): void {}
      disconnect(): void {}
    } as unknown as typeof ResizeObserver;
  });

  afterEach(() => {
    for (const [prop, descriptor] of saved) {
      if (descriptor === undefined) delete (HTMLElement.prototype as unknown as Record<string, unknown>)[prop];
      else Object.defineProperty(HTMLElement.prototype, prop, descriptor);
    }
    saved.clear();

    HTMLElement.prototype.getBoundingClientRect = savedRect;
    globalThis.ResizeObserver = savedObserver;
  });
}

export function registerA11yFetchMock(): void {
  // ⛔ Розміри ставляться разом із заглушкою мережі, а не окремим викликом у
  // кожному з чотирьох файлів: непорожній зріз без розмірів не малює жодної
  // комірки, тобто половина без половини не працює зовсім.
  registerGridLayout();

  beforeEach(() => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) =>
        Promise.resolve(
          new Response(JSON.stringify(emptyBodyFor(String(input))), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
        ),
      ),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });
}

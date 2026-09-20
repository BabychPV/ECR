import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { MethodologyVersionsPage } from '@/pages/admin/MethodologyVersionsPage';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';

/**
 * «Відмова сервера ≠ порожньо» на конфігураторі версій
 * (`DIRECTIVE-15-FRONTEND.md` §0, L10).
 *
 * ⛔ Два переліки цього екрана збиралися через `?? []`, і відмова читання
 * робила їх порожніми МОВЧКИ. Порожнеча в обох місцях має готове прочитання —
 * і воно хибне:
 *
 * 1. `GET /api/v1/units` → `Select` «Output unit» у діалозі формули. Нуль
 *    варіантів поруч із плейсхолдером «без одиниці» читається як «одиниць у
 *    системі не заведено», і методолог зберігає ЧИСЛОВУ формулу безрозмірною.
 *    Сервер таку приймає (`outputUnitId` необов'язковий) — тобто помилка не
 *    спливає ніде, а число без виміру (`ФВ-16.6`) доїжджає до звіту.
 *
 * 2. `GET …/versions` → `Select` «Copy from» у діалозі нової версії. Нуль
 *    варіантів поруч із плейсхолдером «порожня чернетка» читається як
 *    «копіювати нема з чого». Людина, яка прийшла сюди ЄДИНИМ дозволеним
 *    шляхом зміни опублікованої версії (`ФВ-9.1` — клон), заводить натомість
 *    порожню чернетку.
 *
 * ⚠ Банер сторінки під модалкою (`AsyncBoundary` над таблицею версій) випадку
 * 2 не рятує: модалка перекриває сторінку. Саме тому всі твердження шукаються
 * `within(dialog)` — і саме тому `role="alert"` у DOM буває два.
 *
 * ⚠ Мутаційний доказ — у описі PR: кожен запобіжник має власну мутацію, яка
 * валить рівно свій випадок.
 */

vi.mock('@/features/methodologies/MethodologyContentPanels', () => ({
  MethodologyConstantsPanel: () => null,
  MethodologyOutputsPanel: () => null,
  MethodologyRulesPanel: () => null,
  MethodologyRequiredInputsPanel: () => null,
  MethodologyTestsPanel: () => null,
  MethodologyBindingsPanel: () => null,
  MethodologyModesForm: () => null,
}));

/**
 * ⚠ Monaco заглушено тим самим прийомом, що й у
 * `MethodologyVersionsPage.formulaSyntaxError.test.tsx`: справжній редактор у
 * jsdom не потрібен жодному твердженню цього файлу, а тягне він увесь пакет.
 */
vi.mock('@/features/expressions/monaco', () => {
  const model = { getValue: () => '', dispose: () => {} };

  return {
    LightTheme: 'ecr-light',
    DarkTheme: 'ecr-dark',
    prepare: () => 'ecr-methodology',
    applyGrammar: () => {},
    setTheme: () => {},
    showDiagnostics: vi.fn(),
    create: () => ({
      getValue: () => '',
      setValue: () => {},
      getModel: () => model,
      onDidChangeModelContent: () => {},
      dispose: () => {},
    }),
  };
});

vi.mock('@/features/expressions/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/features/expressions/api')>();

  return {
    ...actual,
    expressionMetadata: vi.fn().mockResolvedValue({ functions: [], constants: [] }),
    validateExpression: vi.fn().mockResolvedValue({
      diagnostics: [],
      resultType: 'Number',
      skippedChecks: [],
    }),
  };
});

const DraftVersion = {
  id: 10,
  versionNumber: '1.0',
  status: 'Draft',
  isEditable: true,
  effectiveFrom: null,
  numericMode: 'Strict',
  calendarMode: 'Actual',
  traceLevel: 'Off',
  createdByUserId: 1,
  level: 'Configuration',
};

const PublishedVersion = {
  ...DraftVersion,
  id: 11,
  versionNumber: '0.9',
  status: 'Published',
  isEditable: false,
  effectiveFrom: '2025-01-01',
};

/**
 * ⚠ `messageKey` обов'язковий: без нього `problemText` вважає `detail` сирим
 * (написаним розробником, не з каталогу) і на екран його НЕ пускає — тоді
 * твердження про текст відмови було б хибно-червоним із іншої причини.
 */
const UnitsRefusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'довідник одиниць прочитати не вдалося',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-units-1',
  messageKey: 'err.ECR-SYS-0500.unexpected',
};

const VersionsRefusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'перелік версій методології прочитати не вдалося',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-versions-1',
  messageKey: 'err.ECR-SYS-0500.unexpected',
};

const Strings = {
  'methodologies.versionsTitle': 'Versions and formulas',
  'methodologies.version': 'Version',
  'methodologies.status': 'Status',
  'methodologies.modes': 'Modes',
  'methodologies.effectiveFrom': 'Effective from',
  'methodologies.effectiveFromHint': 'Periods from this date on are calculated by this version.',
  'methodologies.openVersion': 'Open',
  'methodologies.noVersions': 'This methodology has no versions',
  'methodologies.noVersionsHint': 'A version is what actually calculates.',
  'methodologies.formulas': 'Formulas',
  'methodologies.addFormula': 'Add formula',
  'methodologies.formulaTitle': 'Formula',
  'methodologies.formulaCode': 'Code',
  'methodologies.formulaCodeHint': 'It cannot be renamed: other formulas point at it.',
  'methodologies.expression': 'Expression',
  'methodologies.resultType': 'Result type',
  'methodologies.resultTypeHint': 'What the formula produces.',
  'methodologies.resultNumber': 'Number',
  'methodologies.resultText': 'Text',
  'methodologies.arguments': 'Arguments',
  'methodologies.argumentsHint': 'Comma-separated.',
  'methodologies.outputUnit': 'Output unit',
  'methodologies.outputUnitHint': 'Unit of the result.',
  'methodologies.noUnit': 'No unit',
  'methodologies.saveFormula': 'Save formula',
  'methodologies.noFormulas': 'This version has no formulas',
  'methodologies.noFormulasHint': 'Add one.',
  'methodologies.readOnly': 'Read only',
  'methodologies.readOnlyHint': 'This version is published.',
  'methodologies.newVersion': 'New version',
  'methodologies.newVersionTitle': 'New methodology version',
  'methodologies.cloneHint': 'A published version is changed only by cloning it.',
  'methodologies.versionNumber': 'Version number',
  'methodologies.versionNumberHint': 'For example, 1.1.',
  'methodologies.copyFrom': 'Copy from',
  'methodologies.copyFromHint': 'The clone keeps formulas, constants and outputs.',
  'methodologies.emptyDraft': 'Empty draft',
  'methodologies.level': 'Level',
  'methodologies.levelHint': 'The rung of the expressiveness ladder.',
  'methodologies.createVersion': 'Create version',
  'common.retry': 'Retry',
  'common.cancel': 'Cancel',
};

interface Scenario {
  readonly unitsFail: boolean;
  readonly versionsFail: boolean;

  /**
   * Запит, який не відповідає ВЗАГАЛІ — стан «ще в дорозі».
   *
   * ⚠ Саме він відрізняє «ще не прочитали» від «прочитали, і там порожньо»:
   * без нього запобіжник `isPending` не перевіряє ніщо.
   */
  readonly hangs?: 'units' | 'versions';
}

function mockApi(options: Scenario): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';

      const json = (body: unknown, status = 200): Response =>
        new Response(JSON.stringify(body), {
          status,
          headers: {
            'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json',
          },
        });

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: Strings });
      }

      // ⚠ `endsWith`, не `includes`: `/api/v1/methodologies/…` містить підрядок
      // `/api/v1/me` (перші літери «me»thodologies!).
      if (url.endsWith('/api/v1/me')) {
        return json({
          userId: 1,
          userName: 'test',
          language: 'en',
          permissions: ['Calculation.EditFormula'],
          isSimulation: false,
        });
      }

      /** Відповідь, якої не буде: запит лишається в дорозі до кінця тесту. */
      const never = (): Promise<Response> => new Promise<Response>(() => {});

      if (url.includes('/api/v1/units')) {
        if (options.hangs === 'units') return never();

        return options.unitsFail ? json(UnitsRefusal, 500) : json([{ id: 3, code: 't CO2-eq' }]);
      }

      // ⚠ Вужчий маршрут — ПЕРЕД ширшим: `…/versions/10/formulas` містить
      // `…/versions` як префікс.
      if (/\/methodologies\/1\/versions\/\d+\/formulas$/.test(url) && method === 'GET') {
        return json([]);
      }

      /*
       * ⚠ Панелі змісту версії заглушені вище, але читання все одно доходять
       * сюди (`lazyPanel` тягне модуль через `import()`). Порожня відповідь
       * дешевша за розбір: ці п'ять переліків не є предметом жодного
       * твердження, а їхні відмови засипали б сторінку чужими банерами.
       */
      if (/\/versions\/\d+\/(constants|rules|required-inputs|tests|outputs|modes)$/.test(url)) {
        return json([]);
      }

      if (url.endsWith('/api/v1/methodologies/1/bindings')) {
        return json([]);
      }

      if (url.endsWith('/api/v1/methodologies/1/versions')) {
        if (options.hangs === 'versions') return never();

        return options.versionsFail
          ? json(VersionsRefusal, 500)
          : json([DraftVersion, PublishedVersion]);
      }

      throw new Error(`Немає мока для ${method} ${url}`);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/methodologies/1/versions']}>
          <Routes>
            <Route path="/admin/methodologies/:id/versions" element={<MethodologyVersionsPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/**
 * Банер ВІДМОВИ всередині діалогу — і тільки він.
 *
 * ⚠ `within(dialog).getByRole('alert')` не годиться: `role="alert"` ставить
 * будь-який `Alert` Mantine, а діалог нової версії має власний — пояснення
 * про клон (`methodologies.cloneHint`). Тобто «алерт у діалозі є» було б
 * зеленим ще до правки. Розрізняє їх стабільний код відмови, який малює лише
 * `ErrorAlert`.
 */
function refusalIn(dialog: HTMLElement): HTMLElement | undefined {
  return within(dialog)
    .queryAllByRole('alert')
    .find((alert) => (alert.textContent ?? '').includes('ECR-SYS-0500'));
}

/** Готує екран і повертає `userEvent`, уже з завантаженим каталогом. */
async function arrange(options: Scenario): Promise<ReturnType<typeof userEvent.setup>> {
  mockApi(options);
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  const user = userEvent.setup();

  show();

  return user;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('MethodologyVersionsPage: відмова читання не виглядає як «нічого не заведено»', () => {
  it(
    'довідник одиниць не приїхав — у діалозі формули причина з кодом, а переліку немає зовсім',
    async () => {
      const user = await arrange({ unitsFail: true, versionsFail: false });

      await screen.findByText('1.0');
      await user.click(await screen.findByRole('button', { name: 'Add formula' }));

      const dialog = await screen.findByRole('dialog', { name: 'Formula' });

      /*
       * ⛔ ГОЛОВНЕ твердження. До правки перелік одиниць збирався як
       * `(units.data ?? [])`, тож відмова давала `Select` із нулем варіантів —
       * і жодного тексту про те, що читання не вдалося.
       */
      const alert = await waitFor(() => {
        const found = refusalIn(dialog);

        if (found === undefined) throw new Error('банера відмови в діалозі формули немає');

        return found;
      });

      expect(alert.textContent ?? '').toContain('довідник одиниць прочитати не вдалося');
      expect(within(dialog).getByRole('button', { name: 'Retry' })).toBeDefined();

      /*
       * ⚠ Перелік не просто вимкнений — його НЕМАЄ. Вимкнений `Select` із
       * нулем варіантів однаково виглядав би як факт про світ («одиниць
       * немає»), лише сірий.
       */
      expect(within(dialog).queryByLabelText('Output unit')).toBeNull();
    },
    30_000,
  );

  it(
    'одиниці приїхали — перелік доступний, і банера в діалозі формули немає',
    async () => {
      /*
       * ⚠ Дзеркало, без якого перше твердження нічого не варте: найпростіший
       * спосіб «полагодити» мовчазну порожнечу — не малювати перелік ніколи.
       */
      const user = await arrange({ unitsFail: false, versionsFail: false });

      await screen.findByText('1.0');
      await user.click(await screen.findByRole('button', { name: 'Add formula' }));

      const dialog = await screen.findByRole('dialog', { name: 'Formula' });

      // ⚠ Чекаємо саме на ДОСТУПНИЙ контрол: доки запит у дорозі, він
      // вимкнений, і твердження «елемент є» було б зеленим на будь-якому коді.
      await waitFor(() => {
        expect(within(dialog).getByLabelText('Output unit')).toHaveProperty('disabled', false);
      });

      expect(refusalIn(dialog)).toBeUndefined();
      expect(within(dialog).queryByRole('button', { name: 'Retry' })).toBeNull();
    },
    30_000,
  );

  it(
    'одиниці ще в дорозі — перелік НЕДОСТУПНИЙ, а не порожній',
    async () => {
      /*
       * ⛔ Другий запобіжник того ж місця, і перевіряє він інше: «ще не
       * прочитали» — теж не «прочитали, і там порожньо». Доступний `Select` із
       * нулем варіантів у цю мить каже неправду так само, як і після відмови.
       */
      const user = await arrange({ unitsFail: false, versionsFail: false, hangs: 'units' });

      await screen.findByText('1.0');
      await user.click(await screen.findByRole('button', { name: 'Add formula' }));

      const dialog = await screen.findByRole('dialog', { name: 'Formula' });

      expect(within(dialog).getByLabelText('Output unit')).toHaveProperty('disabled', true);

      // ⚠ Дорогою нічого не зламалося — банера відмови тут бути не повинно.
      expect(refusalIn(dialog)).toBeUndefined();
    },
    30_000,
  );

  it(
    'перелік версій не приїхав — у діалозі нової версії причина з кодом, а не «копіювати нема з чого»',
    async () => {
      const user = await arrange({ unitsFail: false, versionsFail: true });

      await user.click(await screen.findByRole('button', { name: 'New version' }));

      const dialog = await screen.findByRole('dialog', { name: 'New methodology version' });

      /*
       * ⛔ ГОЛОВНЕ твердження. До правки `all` збирався як
       * `versions.data ?? []`, і відмова читання робила «Copy from» порожнім —
       * тобто діалог сам пропонував завести порожню чернетку замість клону.
       *
       * ⚠ `within(dialog)`: сторінка під модалкою показує власний банер через
       * `AsyncBoundary`, тож `screen.getByRole('alert')` упав би з «Found
       * multiple elements».
       */
      const alert = await waitFor(() => {
        const found = refusalIn(dialog);

        if (found === undefined) throw new Error('банера відмови в діалозі нової версії немає');

        return found;
      });

      expect(alert.textContent ?? '').toContain('перелік версій методології прочитати не вдалося');

      expect(within(dialog).queryByLabelText('Copy from')).toBeNull();
    },
    30_000,
  );

  it(
    'версії приїхали — «Copy from» доступний, і банера в діалозі нової версії немає',
    async () => {
      const user = await arrange({ unitsFail: false, versionsFail: false });

      await screen.findByText('1.0');
      await user.click(await screen.findByRole('button', { name: 'New version' }));

      const dialog = await screen.findByRole('dialog', { name: 'New methodology version' });

      await waitFor(() => {
        expect(within(dialog).getByLabelText('Copy from')).toHaveProperty('disabled', false);
      });

      expect(refusalIn(dialog)).toBeUndefined();
      expect(within(dialog).queryByRole('button', { name: 'Retry' })).toBeNull();
    },
    30_000,
  );

  it(
    'версії ще в дорозі — «Copy from» НЕДОСТУПНИЙ, а не порожній',
    async () => {
      const user = await arrange({ unitsFail: false, versionsFail: false, hangs: 'versions' });

      await user.click(await screen.findByRole('button', { name: 'New version' }));

      const dialog = await screen.findByRole('dialog', { name: 'New methodology version' });

      /*
       * ⛔ Найдорожча мить саме ця: діалог відкривають ПЕРШИМ ділом, ще до
       * того, як таблиця версій намалювалася. Доступний порожній перелік тут
       * означав би «копіювати нема з чого» рівно тоді, коли клієнт ще не знає
       * нічого.
       */
      expect(within(dialog).getByLabelText('Copy from')).toHaveProperty('disabled', true);
      expect(refusalIn(dialog)).toBeUndefined();
    },
    30_000,
  );
});

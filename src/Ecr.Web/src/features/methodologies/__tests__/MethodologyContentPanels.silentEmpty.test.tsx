import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';

/**
 * Відмова допоміжного довідника робила перелік у `Select` ПОРОЖНІМ — і мовчала
 * (директива №15, §0, `L10`: «немає прав» ≠ «порожньо» ≠ «сервер відмовив»).
 *
 * ⛔ Три джерела збиралися через `?? []`, і всі три — переліки, з яких людина
 * ЧИТАЄ ФАКТ ПРО СВІТ:
 *
 * 1. `GET /api/v1/column-defs/search` живить вибір колонки у ДВОХ діалогах —
 *    обов'язкових входів і прив'язок. Порожній перелік читається як «такої
 *    колонки в шаблонах немає»; пошук за назвою при цьому відповідає «нічого
 *    не знайдено» на будь-який запит. Конфігуратор іде перевіряти, чи
 *    опублікована версія шаблону, — замість повторити запит.
 * 2. `GET /api/v1/units` у діалозі КОНСТАНТ. Порожній перелік читається як
 *    «одиниць у системі немає», і константа зберігається БЕЗ одиниці —
 *    сервер це приймає (`unitId` там необов'язковий), тобто відмова читання
 *    перетворюється на тихо безрозмірний коефіцієнт.
 * 3. `GET /api/v1/units` у діалозі ВИХОДІВ. Там одиниця обов'язкова
 *    (`disabled={… || editing.unitId === null}`), тож кнопка «Зберегти»
 *    лишалася недоступною, НЕ назвавши причини: порожній перелік + мертва
 *    кнопка = «виходи тут завести неможливо».
 *
 * ⚠ Кожен випадок має ДЗЕРКАЛО «усе приїхало»: інакше «полагодити» можна було
 * б назавжди схованим `Select`, і перший випадок лишався б зеленим.
 */

const Strings: Record<string, string> = {
  'methodologies.requiredInputs': 'Required inputs',
  'methodologies.addRequiredInput': 'Add required input',
  'methodologies.columnDefId': 'Column',
  'methodologies.severity': 'Severity',
  'methodologies.severityBlock': 'Block',
  'methodologies.severityWarn': 'Warn',
  'methodologies.hint': 'Hint',
  'methodologies.bindings': 'Bindings',
  'methodologies.addBinding': 'Add binding',
  'methodologies.outputCode': 'Output',
  'methodologies.matchJson': 'Match',
  'methodologies.active': 'Active',
  'methodologies.constants': 'Constants',
  'methodologies.addConstant': 'Add constant',
  'methodologies.constantKind': 'Kind',
  'methodologies.resultNumber': 'Number',
  'methodologies.resultText': 'Text',
  'methodologies.categoryLabel': 'Category label',
  'methodologies.value': 'Value',
  'methodologies.outputUnit': 'Unit',
  'methodologies.constantUnit': 'Unit',
  'methodologies.outputs': 'Outputs',
  'methodologies.addOutput': 'Add output',
  'methodologies.ordinal': 'Ordinal',
  'methodologies.code': 'Code',
  'methodologies.save': 'Save',
  'state.errorTitle': 'The request failed',
  'state.emptyTitle': 'Nothing here yet',
  'common.retry': 'Retry',
  'common.loading': 'Loading…',
};

/**
 * ⚠ Подробиця показується лише з `messageKey` (рішення людини про мову,
 * `problemText.ts`). Без нього твердження про текст відмови було б зеленим на
 * будь-якому коді — банер просто не мав би що показати.
 */
function refusal(detail: string, correlationId: string): Record<string, unknown> {
  return {
    type: 'about:blank',
    title: 'Internal Server Error',
    status: 500,
    detail,
    errorCode: 'ECR-SYS-0500',
    correlationId,
    messageKey: 'err.ECR-SYS-0500.unexpected',
  };
}

const ColumnsRefusal = refusal('перелік колонок прочитати не вдалося', 'cid-columns-1');
const UnitsRefusal = refusal('довідник одиниць прочитати не вдалося', 'cid-units-1');

const columnResults = [
  {
    id: 42,
    code: 'VOL',
    headerL10n: { values: { en: 'Volume extracted' } },
    tableDefId: 1,
    tableCode: 'TBL',
    sheetDefId: 1,
    sheetCode: 'SHEET',
    templateVersionId: 1,
  },
];

const units = [{ id: 7, code: 't' }];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json',
    },
  });
}

/** Сервер, у якому падає рівно один довідник — або жоден. */
function mockApi(fails: 'columns' | 'units' | 'nothing'): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: Strings });
      }

      if (url.includes('/api/v1/column-defs/search')) {
        return fails === 'columns' ? json(ColumnsRefusal, 500) : json(columnResults);
      }

      if (url.includes('/api/v1/units')) {
        return fails === 'units' ? json(UnitsRefusal, 500) : json(units);
      }

      // Власний перелік панелі приїжджає ЗАВЖДИ: перевіряється відмова
      // допоміжного довідника, а не порожня панель.
      if (url.includes('/api/v1/methodologies/1/')) {
        return json([]);
      }

      throw new Error(`Немає мока для ${url}`);
    }),
  );
}

type Panel = 'requiredInputs' | 'bindings' | 'constants' | 'outputs';

async function show(panel: Panel): Promise<void> {
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const panels = await import('../MethodologyContentPanels');

  const body =
    panel === 'requiredInputs' ? (
      <panels.MethodologyRequiredInputsPanel methodologyId={1} versionId={2} editable />
    ) : panel === 'bindings' ? (
      <panels.MethodologyBindingsPanel methodologyId={1} editable />
    ) : panel === 'constants' ? (
      <panels.MethodologyConstantsPanel methodologyId={1} versionId={2} editable />
    ) : (
      <panels.MethodologyOutputsPanel methodologyId={1} versionId={2} editable />
    );

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>{body}</QueryClientProvider>
    </MantineProvider>,
  );
}

/**
 * ⚠ Стеля 30 с, а не «скільки не шкода»: перевірка, яка падає шість хвилин,
 * коштує дорожче, ніж допомагає. Панель у jsdom піднімається за секунди.
 */
const SlowEnvTimeout = 30_000;

/**
 * Стеля очікування САМОГО твердження — навмисно нижча за стелю тесту.
 *
 * ⛔ Коли вони рівні, `waitFor` не встигає скласти свою відмову: тест падає
 * раніше, з «Test timed out in 30000ms», і причина зникає. Перевірка, яка на
 * зламаному коді каже лише «довго», не називає, ЩО саме зламане.
 */
const AssertTimeout = 10_000;

/**
 * Відкриває діалог панелі й повертає САМ діалог.
 *
 * ⚠ Усе шукається `within(dialog)`: `role="alert"` у документі може бути два —
 * банер діалогу й банер самої панелі, — і `screen.getByRole('alert')` упав би
 * з «Found multiple elements». Людина при відкритій модалці бачить лише її
 * вміст, тож і тест дивиться туди ж.
 */
async function openDialog(addButton: string): Promise<HTMLElement> {
  fireEvent.click(await screen.findByRole('button', { name: addButton }, { timeout: SlowEnvTimeout }));

  return screen.findByRole('dialog', {}, { timeout: SlowEnvTimeout });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('MethodologyContentPanels: відмова довідника ≠ «нічого не заведено»', () => {
  it(
    'обов’язкові входи: пошук колонок відмовив — причина в діалозі, вибору колонки НЕМАЄ',
    async () => {
      mockApi('columns');
      await show('requiredInputs');

      const dialog = await openDialog('Add required input');
      const alert = await waitFor(() => within(dialog).getByRole('alert'), {
        timeout: AssertTimeout,
      });

      expect(alert.textContent ?? '').toContain('перелік колонок прочитати не вдалося');
      expect(alert.textContent ?? '').toContain('ECR-SYS-0500');
      expect(alert.textContent ?? '').toContain('cid-columns-1');

      // ⛔ Головне: порожнього переліку, який читається як «колонок немає»,
      // на екрані бути не повинно ЗОВСІМ.
      expect(within(dialog).queryByLabelText('Column')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'обов’язкові входи: колонки приїхали — вибір доступний, банера немає',
    async () => {
      mockApi('nothing');
      await show('requiredInputs');

      const dialog = await openDialog('Add required input');

      // ⚠ Саме `disabled === false`: присутність поля ще не означає, що дані
      // приїхали — доки запит у дорозі, поле є, але недоступне.
      await waitFor(
        () => {
          expect(within(dialog).getByLabelText('Column')).toHaveProperty('disabled', false);
        },
        { timeout: AssertTimeout },
      );

      expect(within(dialog).queryByRole('alert')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'прив’язки: пошук колонок відмовив — причина в діалозі, вибору колонки НЕМАЄ',
    async () => {
      mockApi('columns');
      await show('bindings');

      const dialog = await openDialog('Add binding');
      const alert = await waitFor(() => within(dialog).getByRole('alert'), {
        timeout: AssertTimeout,
      });

      expect(alert.textContent ?? '').toContain('перелік колонок прочитати не вдалося');
      expect(alert.textContent ?? '').toContain('cid-columns-1');
      expect(within(dialog).queryByLabelText('Column')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'прив’язки: колонки приїхали — вибір доступний, банера немає',
    async () => {
      mockApi('nothing');
      await show('bindings');

      const dialog = await openDialog('Add binding');

      await waitFor(
        () => {
          expect(within(dialog).getByLabelText('Column')).toHaveProperty('disabled', false);
        },
        { timeout: AssertTimeout },
      );

      expect(within(dialog).queryByRole('alert')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'константи: довідник одиниць відмовив — причина в діалозі, вибору одиниці НЕМАЄ',
    async () => {
      mockApi('units');
      await show('constants');

      const dialog = await openDialog('Add constant');
      const alert = await waitFor(() => within(dialog).getByRole('alert'), {
        timeout: AssertTimeout,
      });

      expect(alert.textContent ?? '').toContain('довідник одиниць прочитати не вдалося');
      expect(alert.textContent ?? '').toContain('cid-units-1');

      // ⛔ Інакше константа зберігалася б безрозмірною, і сервер це прийняв би.
      expect(within(dialog).queryByLabelText('Unit')).toBeNull();

      // ⚠ Решта діалогу ЛИШАЄТЬСЯ робочою: відмова одного довідника не забирає
      // форму — безрозмірна константа є законною, коли це ВИБІР, а не наслідок
      // відмови.
      expect(within(dialog).getByLabelText('Code')).toBeDefined();
    },
    SlowEnvTimeout,
  );

  it(
    'константи: одиниці приїхали — вибір доступний, банера немає',
    async () => {
      mockApi('nothing');
      await show('constants');

      const dialog = await openDialog('Add constant');

      await waitFor(
        () => {
          expect(within(dialog).getByLabelText('Unit')).toHaveProperty('disabled', false);
        },
        { timeout: AssertTimeout },
      );

      expect(within(dialog).queryByRole('alert')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'виходи: довідник одиниць відмовив — причина в діалозі, вибору одиниці НЕМАЄ',
    async () => {
      mockApi('units');
      await show('outputs');

      const dialog = await openDialog('Add output');
      const alert = await waitFor(() => within(dialog).getByRole('alert'), {
        timeout: AssertTimeout,
      });

      expect(alert.textContent ?? '').toContain('довідник одиниць прочитати не вдалося');
      expect(alert.textContent ?? '').toContain('cid-units-1');

      // ⛔ Тут одиниця ОБОВ'ЯЗКОВА, тож кнопка «Зберегти» й так недоступна —
      // дефектом була саме мовчазність: недоступна кнопка без причини.
      expect(within(dialog).queryByLabelText('Unit')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'виходи: одиниці приїхали — вибір доступний, банера немає',
    async () => {
      mockApi('nothing');
      await show('outputs');

      const dialog = await openDialog('Add output');

      await waitFor(
        () => {
          expect(within(dialog).getByLabelText('Unit')).toHaveProperty('disabled', false);
        },
        { timeout: AssertTimeout },
      );

      expect(within(dialog).queryByRole('alert')).toBeNull();
    },
    SlowEnvTimeout,
  );
});

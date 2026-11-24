import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';

/**
 * Відмова допоміжного довідника робила перелік у `Select` ПОРОЖНІМ — і мовчала
 * (директива №15, §0, `L10`: «немає прав» ≠ «порожньо» ≠ «сервер відмовив»).
 *
 * ⛔ Джерела збиралися через `?? []` — переліки, з яких людина
 * ЧИТАЄ ФАКТ ПРО СВІТ:
 *
 * 1. `GET /api/v1/column-defs/search` живить вибір колонки у ДВОХ діалогах —
 *    обов'язкових входів і прив'язок. Порожній перелік читається як «такої
 *    колонки в шаблонах немає»; пошук за назвою при цьому відповідає «нічого
 *    не знайдено» на будь-який запит. Конфігуратор іде перевіряти, чи
 *    опублікована версія шаблону, — замість повторити запит.
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

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json',
    },
  });
}

/** Сервер, у якому падає рівно один довідник — або жоден. */
function mockApi(fails: 'columns' | 'nothing'): void {
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

      // Власний перелік панелі приїжджає ЗАВЖДИ: перевіряється відмова
      // допоміжного довідника, а не порожня панель.
      if (url.includes('/api/v1/methodologies/1/')) {
        return json([]);
      }

      throw new Error(`Немає мока для ${url}`);
    }),
  );
}

type Panel = 'requiredInputs' | 'bindings';

async function show(panel: Panel): Promise<void> {
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const panels = await import('../MethodologyContentPanels');

  const body =
    panel === 'requiredInputs' ? (
      <panels.MethodologyRequiredInputsPanel methodologyId={1} versionId={2} editable />
    ) : (
      <panels.MethodologyBindingsPanel methodologyId={1} editable />
    );

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter>{body}</MemoryRouter>
      </QueryClientProvider>
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
});

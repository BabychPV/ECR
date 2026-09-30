import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';

/**
 * F-03 і F-28 (четвертий раунд UX): вибір колонки шукає НА СЕРВЕРІ за введеним
 * текстом і не питає сервер, доки діалог закрито.
 *
 * ⛔ Відтворено на стенді: 5991 колонка, перелік вантажився один раз
 * (`limit=200`) і фільтрувався в браузері — `EMISSION` у перші 200 не
 * потрапляла, і прив'язку зробити з інтерфейсу було неможливо. А сторінка
 * методології в публікатора щоразу робила цей самий запит і отримувала `403`.
 */

const Strings: Record<string, string> = {
  'methodologies.bindings': 'Bindings',
  'methodologies.addBinding': 'Add binding',
  'methodologies.columnDefId': 'Column',
  'methodologies.columnDefIdHint': 'The column that receives the output.',
  'methodologies.columnNotFound': 'No column matches.',
  'methodologies.outputCode': 'Output',
  'methodologies.outputCodeBindingHint': 'The output code of the methodology.',
  'methodologies.matchJson': 'Match',
  'methodologies.bindingMatchHint': 'Narrows the rows.',
  'methodologies.active': 'Active',
  'methodologies.noBindings': 'This methodology has no bindings',
  'methodologies.noBindingsHint': 'Without a binding, the calculation writes nothing.',
  'methodologies.save': 'Save',
  'common.loading': 'Loading...',
  'state.errorTitle': 'The request failed',
  'state.emptyTitle': 'Nothing here yet',
  'common.retry': 'Retry',
};

const Emission = {
  id: 6017,
  code: 'EMISSION',
  headerL10n: { values: { en: 'Emission' } },
  tableDefId: 193,
  tableCode: 'TBL',
  sheetDefId: 1,
  sheetCode: 'SHEET',
  templateVersionId: 1,
};

function mockApi(): { searches: string[] } {
  const searches: string[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: Strings });
      }

      if (url.endsWith('/api/v1/methodologies/1/bindings') && method === 'GET') {
        return json([]);
      }

      if (url.includes('/api/v1/column-defs/search') && method === 'GET') {
        const q = new URL(url, 'http://localhost').searchParams.get('q') ?? '';
        searches.push(q);

        // Сервер фільтрує сам: порожній запит дає «перші за кодом», серед яких
        // EMISSION немає — рівно як на стенді.
        return json(q.toUpperCase().includes('EMIS') ? [Emission] : []);
      }

      throw new Error(`Немає мока для ${method} ${url}`);
    }),
  );

  return { searches };
}

async function show(): Promise<void> {
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const { MethodologyBindingsPanel } = await import('../MethodologyContentPanels');

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MethodologyBindingsPanel methodologyId={1} editable />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ColumnDefPicker: серверний пошук колонки (F-03, F-28)', () => {
  /** Мутація: повернути одноразовий `limit=200` без `q` — EMISSION не з'являється. */
  it('введений текст іде на сервер як q, і знайдена колонка стає варіантом', async () => {
    const { searches } = mockApi();
    await show();

    fireEvent.click(await screen.findByRole('button', { name: 'Add binding' }));

    const input = await screen.findByLabelText('Column');
    fireEvent.click(input);
    fireEvent.change(input, { target: { value: 'EMIS' } });

    expect(
      await screen.findByRole('option', { name: 'Emission (EMISSION) · SHEET/TBL' }, { timeout: 5000 }),
    ).toBeTruthy();
    expect(searches).toContain('EMIS');
  }, 60000);

  /** Мутація: повернути запит у панель (поза діалогом) — пошук іде одразу після монтування. */
  it('поки діалог закрито, пошук колонок не запитується (F-28)', async () => {
    const { searches } = mockApi();
    await show();

    await screen.findByRole('button', { name: 'Add binding' });
    await waitFor(() => expect(screen.getByText('This methodology has no bindings')).toBeTruthy());

    expect(searches).toHaveLength(0);
  }, 60000);
});

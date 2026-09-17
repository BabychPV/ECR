import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';

/**
 * Директива "пошук колонки за назвою замість голого ColumnDefId", панель
 * `MethodologyRequiredInputsPanel`: до цього поле приймало `ColumnDefId`
 * голим числом у `NumberInput` — адміністратор мав пам'ятати внутрішній
 * ідентифікатор напам'ять. Тепер це вибір зі списку, наповненого `GET
 * /api/v1/column-defs/search`, той самий прийом, що вибір довідника в
 * `ColumnEditor.tsx`.
 *
 * ✎ Тут стояла заглушка `Select` легким `<select>` — нібито тому, що
 * справжній «зависає під jsdom». Причина зависання знайдена й усунена
 * (рекурсія jsdom ↔ nwsapi на станових псевдокласах — коментар у
 * `src/test/setup.ts`), тож тест працює зі справжнім `Select searchable`.
 */

const Strings: Record<string, string> = {
  'methodologies.requiredInputs': 'Required input columns',
  'methodologies.addRequiredInput': 'Add required input',
  'methodologies.requiredInputSaved': 'The required input has been saved.',
  'methodologies.requiredInputColumnHint': 'The column whose emptiness blocks or warns on save.',
  'methodologies.severity': 'Severity',
  'methodologies.severityHint': 'Whether an unfilled column blocks saving or only warns.',
  'methodologies.severityBlock': 'Block',
  'methodologies.severityWarn': 'Warn',
  'methodologies.hint': 'Hint',
  'methodologies.hintHint': 'Text shown instead of the default template.',
  'methodologies.noRequiredInputs': 'This version has no required input columns',
  'methodologies.noRequiredInputsHint': 'Without a required input, nothing is checked.',
  'methodologies.columnDefId': 'Column',
  'methodologies.editFormula': 'Edit',
  'methodologies.save': 'Save',
  'state.errorTitle': 'The request failed',
  'state.errorUnknown': 'An unexpected error occurred.',
  'state.emptyTitle': 'Nothing here yet',
  'common.retry': 'Retry',
};

const searchResults = [
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

function mockApi(): { puts: { columnDefId: number; body: unknown }[] } {
  const puts: { columnDefId: number; body: unknown }[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';

      const json = (body: unknown, status = 200): Response =>
        new Response(JSON.stringify(body), {
          status,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: Strings });
      }

      if (url.endsWith('/api/v1/methodologies/1/versions/10/required-inputs') && method === 'GET') {
        return json([]);
      }

      if (url.includes('/api/v1/column-defs/search') && method === 'GET') {
        return json(searchResults);
      }

      const put = /\/methodologies\/1\/versions\/10\/required-inputs\/(\d+)$/.exec(url);
      if (put && method === 'PUT') {
        const columnDefId = Number(put[1]);
        const body = JSON.parse(String(init?.body)) as unknown;
        puts.push({ columnDefId, body });

        return json({ id: 1, columnDefId, ...(body as object) });
      }

      throw new Error(`Немає мока для ${method} ${url}`);
    }),
  );

  return { puts };
}

async function show(): Promise<void> {
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const { MethodologyRequiredInputsPanel } = await import('../MethodologyContentPanels');

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MethodologyRequiredInputsPanel methodologyId={1} versionId={10} editable />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('MethodologyRequiredInputsPanel: вибір колонки за назвою (директива "пошук колонки за назвою")', () => {
  it('вибір колонки зі списку записує правильний числовий ColumnDefId', async () => {
    const { puts } = mockApi();
    await show();

    fireEvent.click(await screen.findByRole('button', { name: 'Add required input' }));

    // Колонка обирається за назвою — це і є предмет директиви.
    fireEvent.click(await screen.findByLabelText('Column'));
    fireEvent.click(
      await screen.findByRole('option', { name: 'Volume extracted (VOL) · SHEET/TBL' }),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0]?.columnDefId).toBe(42);
  }, 60000);
});

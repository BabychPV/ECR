import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';

/**
 * Директива "пошук колонки за назвою замість голого ColumnDefId", панель
 * `MethodologyBindingsPanel`: те саме, що для `MethodologyRequiredInputsPanel`
 * (`MethodologyRequiredInputsPanel.columnSearch.test.tsx`) — `NumberInput` із
 * голим `ColumnDefId` замінено на `Select searchable`, наповнений `GET
 * /api/v1/column-defs/search`.
 *
 * ⛔ Той самий, уже задокументований у трьох місцях обхід зависання
 * `Select`/`MultiSelect` під jsdom: заглушка легким `<select>`.
 */
vi.mock('@mantine/core', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/core')>();

  type StubOption = { value: string; label: string };
  type StubSelectProps = {
    data?: (string | StubOption)[];
    value?: string | null;
    onChange?: (value: string | null) => void;
    label?: string;
    placeholder?: string;
    'aria-label'?: string;
  };

  function StubSelect(props: StubSelectProps): JSX.Element {
    const options = (props.data ?? []).map((item) =>
      typeof item === 'string' ? { value: item, label: item } : item,
    );

    return (
      <select
        aria-label={props['aria-label'] ?? props.label ?? props.placeholder}
        value={props.value ?? ''}
        onChange={(event) => props.onChange?.(event.target.value === '' ? null : event.target.value)}
      >
        <option value="" />
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
    );
  }

  return { ...actual, Select: StubSelect };
});

const Strings: Record<string, string> = {
  'methodologies.bindings': 'Bindings',
  'methodologies.addBinding': 'Add binding',
  'methodologies.bindingSaved': 'The binding has been saved.',
  'methodologies.tableDefId': 'Table',
  'methodologies.columnDefId': 'Column',
  'methodologies.columnDefIdHint': 'The column that receives the output.',
  'methodologies.outputCode': 'Output',
  'methodologies.outputCodeBindingHint': 'The output code of the methodology.',
  'methodologies.matchJson': 'Match',
  'methodologies.bindingMatchHint': 'Narrows the rows the binding applies to; {} — all.',
  'methodologies.active': 'Active',
  'methodologies.noBindings': 'This methodology has no bindings',
  'methodologies.noBindingsHint': 'Without a binding, the calculation writes nothing.',
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

function mockApi(): { puts: { columnDefId: number; outputCode: string; body: unknown }[] } {
  const puts: { columnDefId: number; outputCode: string; body: unknown }[] = [];

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

      if (url.endsWith('/api/v1/methodologies/1/bindings') && method === 'GET') {
        return json([]);
      }

      if (url.includes('/api/v1/column-defs/search') && method === 'GET') {
        return json(searchResults);
      }

      const put = /\/methodologies\/1\/bindings\/(\d+)\/([^/]+)$/.exec(url);
      if (put && method === 'PUT') {
        const columnDefId = Number(put[1]);
        const outputCode = decodeURIComponent(put[2] ?? '');
        const body = JSON.parse(String(init?.body)) as unknown;
        puts.push({ columnDefId, outputCode, body });

        return json({ id: 1, tableDefId: 1, columnDefId, outputCode, ...(body as object) });
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

describe('MethodologyBindingsPanel: вибір колонки за назвою (директива "пошук колонки за назвою")', () => {
  it('вибір колонки зі списку записує правильний числовий ColumnDefId', async () => {
    const { puts } = mockApi();
    await show();

    fireEvent.click(await screen.findByRole('button', { name: 'Add binding' }));

    const select = await screen.findByLabelText('Column');
    await waitFor(() => {
      expect((select as HTMLSelectElement).querySelectorAll('option').length).toBeGreaterThan(1);
    });

    fireEvent.change(select, { target: { value: '42' } });
    fireEvent.change(screen.getByLabelText('Output'), { target: { value: 'OUT1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0]?.columnDefId).toBe(42);
    expect(puts[0]?.outputCode).toBe('OUT1');
  }, 60000);
});

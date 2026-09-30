import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableDto } from '@/api/types';
import { TablePreview } from '@/features/templates/TablePreview';
import { testTheme } from '@/test/render';

/**
 * `TablePreview` — попередній перегляд таблиці шаблону в конструкторі (`ФВ-2.6`).
 *
 * Предмет файла — з'єднання: правила читаються з `GET …/conditional-formats`
 * саме цієї версії, нічого не пишеться, колонки й рядки стоять у порядку
 * структури, а значення-приклад фарбується правилом своєї колонки. Сама
 * модель — у `tablePreview.test.ts`.
 *
 * Доказ мутацією (перевірено руками): підставити в URL інший id версії —
 * «читає правила своєї версії» червоний; не передавати `value` у
 * `previewCellLook` — «фарбує приклад» червоний; прибрати гілку
 * `MonthsInColumns` — «попереджає про місяці» червоний.
 */

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = String(input);

  if (url.endsWith('/api/v1/template-versions/7/conditional-formats') && (init?.method ?? 'GET') === 'GET') {
    return json([
      { columnCode: 'QTY', operator: 'gt', value: '100', valueTo: null, backgroundHex: '#ff0000', foregroundHex: null, isBold: true },
      { columnCode: 'ELSEWHERE', operator: 'gt', value: '1', valueTo: null, backgroundHex: '#00ff00', foregroundHex: null, isBold: false },
    ]);
  }

  throw new Error(`Немає мока для ${init?.method ?? 'GET'} ${url}`);
});

afterEach(() => {
  vi.unstubAllGlobals();
  fetchMock.mockClear();
});

type Table = Pick<TableDto, 'columns' | 'rows' | 'layoutKind'>;

const table: Table = {
  layoutKind: 'Static',
  columns: [
    {
      code: 'NOTE', dataType: 'String', formulaDialect: null, formulaExpression: null,
      headerL10n: { values: { en: 'Note' } }, displayFormat: null, id: 2, isHidden: false, isReadOnly: true, isRequired: false, ordinal: 2, unitSymbol: null,
    },
    {
      code: 'QTY', dataType: 'Decimal', formulaDialect: null, formulaExpression: null,
      headerL10n: { values: { en: 'Quantity' } }, displayFormat: null, id: 1, isHidden: false, isReadOnly: false, isRequired: true, ordinal: 1, unitSymbol: 't',
    },
  ] as Table['columns'],
  rows: [
    { formulaDialect: null, formulaExpression: null, id: 11, isReadOnly: false, label: 'Second', ordinal: 2, parentRowKey: null, rowKey: 'R2', rowKind: 'Item' },
    { formulaDialect: null, formulaExpression: null, id: 10, isReadOnly: false, label: 'First', ordinal: 1, parentRowKey: null, rowKey: 'R1', rowKind: 'Item' },
  ] as Table['rows'],
};

function show(value: Table = table): void {
  vi.stubGlobal('fetch', fetchMock);
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <TablePreview templateVersionId={7} table={value} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('TablePreview', () => {
  it('читає правила своєї версії і нічого не пише', async () => {
    show();

    await screen.findByRole('table');
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(String(url)).toContain('/api/v1/template-versions/7/conditional-formats');
    expect(init?.method ?? 'GET').toBe('GET');
  });

  it('колонки й рядки — у порядку структури, з одиницею колонки', async () => {
    show();

    const grid = await screen.findByRole('table');
    const headers = Array.from(grid.querySelectorAll('[data-preview-column]')).map((th) =>
      th.getAttribute('data-preview-column'),
    );
    expect(headers).toEqual(['QTY', 'NOTE']);
    expect(Array.from(grid.querySelectorAll('[data-preview-row]')).map((tr) => tr.getAttribute('data-preview-row'))).toEqual([
      'R1',
      'R2',
    ]);
    expect(within(grid).getByText(/, t$/)).toBeTruthy();
  });

  it('фарбує приклад правилом своєї колонки, а чужу колонку лишає як є', async () => {
    show();

    const grid = await screen.findByRole('table');
    const qty = grid.querySelector<HTMLElement>('[data-preview-cell="R1:QTY"]')!;
    const note = grid.querySelector<HTMLElement>('[data-preview-cell="R1:NOTE"]')!;
    expect(qty.style.backgroundColor).toBe('');

    fireEvent.change(screen.getByRole('textbox'), { target: { value: '150' } });

    expect(qty.textContent).toBe('150');
    expect(qty.style.backgroundColor).toBe('rgb(255, 0, 0)');
    expect(qty.style.fontWeight).toBe('bold');
    expect(note.style.backgroundColor).toBe('');

    fireEvent.change(screen.getByRole('textbox'), { target: { value: '50' } });
    expect(qty.style.backgroundColor).toBe('');
  });

  it('правило колонки видно в шапці ще до прикладу', async () => {
    show();

    const grid = await screen.findByRole('table');
    const legend = grid.querySelector<HTMLElement>('[data-preview-rules="QTY"]')!;
    expect(legend.textContent).toContain('100');
    expect(grid.querySelector('[data-preview-rules="NOTE"]')).toBeNull();
  });

  it('попереджає, що колонки повторюються по місяцях', async () => {
    show({ ...table, layoutKind: 'MonthsInColumns' });

    await screen.findByRole('table');
    expect(screen.getByText(/tablePreview\.monthsInColumns/)).toBeTruthy();
  });
});

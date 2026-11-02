import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { SheetLog } from '../SheetLog';

/**
 * Журнал аркуша: додавання й видалення рядка (`kind: RowChange`) — окремий рядок з дією, автором, таблицею · ключем
 * рядка і походженням; без значень комірок і без переходу в комірку (адреси колонки немає).
 */
const RowItem = {
  kind: 'RowChange',
  at: '2026-10-29T10:00:00Z',
  byDisplayName: 'land-owner',
  origin: 'Import',
  periodKey: 202610,
  rowKey: 'NEW_7',
  rowLabelL10n: null,
  columnDefId: null,
  columnCode: null,
  columnHeaderL10n: null,
  columnDataType: null,
  tableCode: 'T1',
  tableNameL10n: null,
  oldValue: null,
  newValue: null,
  action: 'RowAdded',
  fromState: null,
  toState: null,
  reason: null,
};

function mount(items: unknown[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response(JSON.stringify({ items, nextCursor: null }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })),
  );

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <SheetLog
          documentId={1}
          periodKey={202610}
          sheet={{ sheetDefId: 2, code: 'S', name: 'Sheet' }}
          onOpenCell={() => undefined}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SheetLog: додавання й видалення рядків', () => {
  it('додано і видалено рядок — окремі події з таблицею, ключем, автором і походженням, без кнопки переходу', async () => {
    mount([{ ...RowItem, action: 'RowDeleted', origin: 'UserEdit', rowKey: 'OLD_3' }, RowItem]);

    const deleted = await screen.findByText('OLD_3', { exact: false });
    const deletedRow = deleted.closest('[data-inspector-sheet-event]');
    expect(deletedRow?.getAttribute('data-inspector-sheet-event')).toBe('RowDeleted');

    const added = screen.getByText('NEW_7', { exact: false }).closest('[data-inspector-sheet-event]');
    expect(added?.getAttribute('data-inspector-sheet-event')).toBe('RowAdded');
    expect(added?.textContent).toContain('land-owner');
    expect(added?.textContent).toContain('Import');
    expect(added?.querySelector('[data-inspector-row-where]')?.textContent).toBe('T1 · NEW_7');

    // Значень комірок і переходу в комірку немає: рядок журналу рядків не є кнопкою.
    expect(added?.querySelector('[data-inspector-old], [data-inspector-new]')).toBeNull();
    expect(added?.querySelector('button')).toBeNull();
  });
});

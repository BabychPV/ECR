import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { SheetLog } from '../SheetLog';

/**
 * Журнал аркуша показує значення за правилом показу колонки (як вкладка Audit і сітка), а не сирим
 * `437.6311000000000000`; точне значення лишається в `title`. Нечислові колонки — без змін.
 */
const Item = {
  kind: 'CellEdit',
  at: '2026-10-29T10:00:00Z',
  byDisplayName: 'land-owner',
  origin: 'UserEdit',
  periodKey: 202610,
  rowKey: 'R1',
  rowLabelL10n: null,
  columnDefId: 5,
  columnCode: 'C5',
  columnHeaderL10n: null,
  columnDataType: 'Decimal',
  tableCode: 'T1',
  tableNameL10n: null,
  oldValue: '437.6311000000000000',
  newValue: '1234.5000000000000000',
  action: null,
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
        <SheetLog documentId={1} periodKey={202610} sheet={{ sheetDefId: 2, code: 'S', name: 'Sheet' }} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SheetLog: значення комірок', () => {
  it('числова колонка: без хвостових нулів, розряди; точне значення в title', async () => {
    mount([Item]);

    const was = await screen.findByText('437.6311');
    expect(was.getAttribute('title')).toBe('437.6311000000000000');

    const becomes = screen.getByText('1,234.5');
    expect(becomes.getAttribute('title')).toBe('1234.5000000000000000');
    expect(screen.queryByText('437.6311000000000000')).toBeNull();
  });

  it('нечислова колонка й порожні значення лишаються як є', async () => {
    mount([{ ...Item, columnDataType: 'String', oldValue: null, newValue: '12.5000' }]);

    expect(await screen.findByText('12.5000')).toBeTruthy();
    expect(screen.getByText('—')).toBeTruthy();
  });
});

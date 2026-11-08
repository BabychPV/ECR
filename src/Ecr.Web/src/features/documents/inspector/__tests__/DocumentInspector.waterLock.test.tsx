import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { DocumentTableDto } from '@/api/types';
import {
  publishTableAttention,
  requestAttentionFocus,
  resetRowAttentionForTests,
  type TableAttention,
} from '@/features/grid/rowAttentionStore';
import { testTheme } from '@/test/render';
import { DocumentInspector } from '../DocumentInspector';
import { clearInspectedCell, publishInspectedCell } from '../inspectedCell';

/**
 * ✎ UAT 08.11, Water Report (документ LAND-FULL, аркуш «Water Report», рядок «Sea Water Intake», січень 2026).
 *
 * - d3: «!» біля підпису рядка (`requestAttentionFocus`) відкриває Issues і ставить фокус на пункт цього рядка;
 * - d4: Issues перелічує рядки, що потребують уваги, і закриті місяці; порожній перелік каже, ЩО перевірено і КОЛИ;
 * - d5: шапка інспектора називає ТОЙ САМИЙ період, що й сторінка (не «August», коли показано січень).
 *
 * ⛔ Червоне без фіксу: без `AttentionSection` — немає `[data-inspector-attention]`; без ефекту фокуса — панель
 * не відкривається; без чипа періоду — немає `[data-inspector-period]`; без `noIssuesChecked` — немає периоду в
 * порожньому стані.
 */

const Tables: DocumentTableDto[] = [
  {
    allowsDynamicRows: false,
    copyOrdinal: 1,
    isCopyable: false,
    maxDynamicRows: null,
    sheetCode: 'WR',
    sheetDefId: 166,
    sheetNameL10n: { values: { en: 'Water Report' } },
    sheetOrdinal: 0,
    tableCode: 'T7_00',
    tableDefId: 701,
    tableInstanceId: 597,
    tableNameL10n: { values: { en: '7.00 Water Report' } },
    tableOrdinal: 0,
  } as DocumentTableDto,
];

const January: TableAttention = {
  tableInstanceId: 597,
  periodKey: 202601,
  rows: [
    {
      key: '597:R7001000',
      tableInstanceId: 597,
      periodKey: 202601,
      rowKey: 'R7001000',
      rowLabel: 'Sea Water Intake',
      reason: 'grid.rowAttention.orphaned grid.rowAttention.locked',
    },
  ],
  lockedMonths: 'January–August',
};

vi.stubGlobal('fetch', vi.fn(async () => new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } })));

function Harness({ periodKey = 202601, checkedAt = null }: { periodKey?: number; checkedAt?: Date | null }): JSX.Element {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return (
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/documents/2']}>
          <DocumentInspector
            documentId={2}
            periodKey={periodKey}
            tables={Tables}
            messages={[]}
            onSelectFinding={() => undefined}
            checkedAt={checkedAt}
          />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>
  );
}

afterEach(() => {
  cleanup();
  act(() => resetRowAttentionForTests());
  act(() => clearInspectedCell());
});

describe('Water lock: Issues і шапка інспектора (UAT 08.11)', () => {
  it('d3: «!» рядка відкриває Issues і ставить фокус на пункт рядка', async () => {
    render(<Harness />);
    act(() => publishTableAttention(597, January));
    expect(document.querySelector('[data-inspector="issues"]')).toBeNull();

    act(() => requestAttentionFocus('597:R7001000'));

    await waitFor(() => expect(document.querySelector('[data-inspector="issues"]')).not.toBeNull());
    const item = document.querySelector<HTMLElement>('[data-inspector-attention="597:R7001000"]');
    expect(item?.textContent).toContain('Sea Water Intake');
    await waitFor(() => expect(document.activeElement).toBe(item));
  });

  it('d4: закриті місяці перелічено, порожній перелік перевірки каже, що перевірено і коли', async () => {
    render(<Harness checkedAt={new Date(2026, 10, 8, 9, 15)} />);
    act(() => publishTableAttention(597, January));
    act(() => requestAttentionFocus('597:R7001000'));

    await waitFor(() => expect(document.querySelector('[data-inspector-locked="597"]')).not.toBeNull());
    expect(document.querySelector('[data-inspector-locked="597"]')?.textContent).toContain('inspector.attention.lockedMonths');

    const empty = document.querySelector('.ecr-insp-empty');
    expect(empty?.textContent).toContain('inspector.noIssuesChecked');
    expect(empty?.textContent).toContain('inspector.checkedAt');
  });

  it('d4: інший період — чужих пунктів немає; кнопка Issues каже про рядок, що потребує уваги', () => {
    render(<Harness periodKey={202608} />);
    act(() => publishTableAttention(597, January));

    const button = document.querySelector('[data-inspector-issues]');
    expect(button?.textContent).not.toContain('grid.rowAttention.count');

    cleanup();
    render(<Harness periodKey={202601} />);
    expect(document.querySelector('[data-inspector-issues]')?.textContent).toContain('grid.rowAttention.count');
  });

  it('d5: шапка інспектора називає період сторінки', async () => {
    const { rerender } = render(<Harness periodKey={202601} />);
    act(() => publishTableAttention(597, January));
    act(() => requestAttentionFocus('597:R7001000'));

    await waitFor(() => expect(document.querySelector('[data-inspector-period]')).not.toBeNull());
    expect(document.querySelector('[data-inspector-period]')?.getAttribute('data-inspector-period')).toBe('202601');
    expect(screen.getByRole('heading', { level: 2 }).textContent).toMatch(/2026/);

    rerender(<Harness periodKey={202608} />);
    expect(document.querySelector('[data-inspector-period]')?.getAttribute('data-inspector-period')).toBe('202608');
  });

  // ⛔ Червоне без фіксу: чип був «R7001000 · August» (код колонки місяця) — без «Column», і читався як період.
  it('d5: чип адреси комірки — назва КОЛОНКИ з позначкою «Column», не голий код місяця', async () => {
    act(() =>
      publishInspectedCell({
        tableInstanceId: 597,
        periodKey: 202601,
        rowKey: 'R7001000',
        rowLabel: 'Sea Water Intake',
        columnCode: 'August',
        columnDefId: 7008,
        columnHeader: 'August',
        unitSymbol: null,
        dataType: 'Decimal',
        scale: 2,
        isCalculated: false,
        isReadOnly: true,
        value: '',
      }),
    );
    render(<Harness periodKey={202601} />);
    act(() => publishTableAttention(597, January));
    act(() => requestAttentionFocus('597:R7001000'));

    await waitFor(() => expect(document.querySelector('[data-inspector-address]')).not.toBeNull());
    const chip = document.querySelector('[data-inspector-address]');
    expect(chip?.getAttribute('data-inspector-column')).toBe('August');
    expect(chip?.textContent).toContain('inspector.info.column');
    expect(chip?.textContent).toContain('R7001000');
  });
});

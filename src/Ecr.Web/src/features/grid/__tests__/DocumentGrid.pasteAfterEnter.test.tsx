import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { pendingSlice, resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';
import { planPaste } from '../clipboard';

// Лічильник вставок: «одна вставка = один розбір буфера» (без подвійної від gate + bodyPaste).
vi.mock('../clipboard', async (importOriginal) => {
  const original = await importOriginal<typeof import('../clipboard')>();

  return { ...original, planPaste: vi.fn(original.planPaste) };
});

/**
 * AN-39 / L8-16: Ctrl+V за 70-250 мс після Enter (макрос, сканер) вставляв у ПОПЕРЕДНЮ
 * комірку - фокус ще не перейшов, а нативний `paste` не ставиться в чергу keyCommitGate.
 * Тепер вставка відкладається до кінця вікна коміту.
 *
 * Заглушка сітки несе обгортку редактора: Enter у ній відкриває вікно затримки (250 мс).
 */
vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: () => (
    <div data-testid="revogrid-stub">
      <div className="edit-input-wrapper">
        <input data-testid="editor-input" />
      </div>
    </div>
  ),
}));

const column: ColumnDto = {
  code: 'C1',
  dataType: 'Int',
  defaultValue: null,
  displayFormat: null,
  header: 'C1',
  id: 1,
  isReadOnly: false,
  isRequired: false,
  isRequiredByMethodology: false,
  lookupRegistryDefId: null,
  ordinal: 0,
  unitId: null,
  unitSymbol: null,
};

const slice: TableSliceDto = {
  cellConfirmations: {},
  cellPermissions: {},
  periodKey: 202609,
  tableInstanceId: 4,
  columns: [column],
  rows: [{ cells: { C1: 1 }, isOrphaned: false, label: null, ordinal: 0, rowKey: 'r1', rowKind: 'Item', rowVersion: 'v1' }],
};

async function show(): Promise<HTMLElement> {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response(JSON.stringify(slice), { status: 200, headers: { 'Content-Type': 'application/json' } })),
  );

  render(
    <MantineProvider>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <DocumentGrid
          documentId={12}
          tableInstanceId={4}
          tableDefId={1}
          periodKey={202609}
          readOnly={false}
          allowsDynamicRows={false}
          maxDynamicRows={null}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return screen.findByTestId('revogrid-stub');
}

async function wait(ms: number): Promise<void> {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, ms));
  });
}

function paste(stub: HTMLElement): void {
  fireEvent.paste(stub, { clipboardData: { getData: () => '7' } });
}

afterEach(() => {
  vi.mocked(planPaste).mockClear();
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('L8-16: Ctrl+V одразу після Enter', () => {
  it('контроль: без вікна коміту вставка застосовується одразу', async () => {
    const stub = await show();

    paste(stub);

    expect(pendingSlice(4, 202609).size).toBe(1);
  });

  it('у вікні коміту вставка чекає його кінця, а потім застосовується', async () => {
    const stub = await show();

    fireEvent.keyDown(screen.getByTestId('editor-input'), { key: 'Enter' });
    paste(stub);

    // ⛔ Фокус ще не перейшов: застосувати зараз = записати в попередню комірку.
    expect(pendingSlice(4, 202609).size).toBe(0);

    await wait(500);

    expect(pendingSlice(4, 202609).size).toBe(1);
    // Одна вставка = один розбір (не подвійна від gate + нативного шляху).
    expect(vi.mocked(planPaste)).toHaveBeenCalledTimes(1);
  });
});

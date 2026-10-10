import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { showWarning } from '@/shared/ui/notify';
import { cancelAutosave } from '../autosave';
import { pendingSlice, resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * F6-03: вставка з Excel, більша за таблицю, обрізається — але людині кажуть,
 * скільки рядків і колонок не вмістилося. Доти — мовчки: «вставилося», а дві
 * третини даних не введено.
 */
vi.mock('../autosave', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../autosave')>();

  return { ...actual, scheduleAutosave: (): void => undefined };
});

vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showWarning: vi.fn(),
}));

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: () => <div data-testid="revogrid-stub" />,
}));

function column(code: string, ordinal: number): ColumnDto {
  return {
    code,
    dataType: 'Int',
    defaultValue: null,
    displayFormat: null,
    header: code,
    id: ordinal + 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    lookupRegistryDefId: null,
    ordinal,
    unitId: null,
    unitSymbol: null,
  };
}

function slice(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns: [column('C1', 0), column('C2', 1)],
    rows: ['r1', 'r2'].map((rowKey, ordinal) => ({
      cells: { C1: 1, C2: 2 },
      isOrphaned: false,
      label: null,
      ordinal,
      rowKey,
      rowKind: 'Item' as const,
      rowVersion: 'v1',
    })),
  };
}

function show(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(() =>
      Promise.resolve(new Response(JSON.stringify(slice()), { status: 200, headers: { 'Content-Type': 'application/json' } })),
    ),
  );

  render(
    <MantineProvider>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <DocumentGrid
          documentId={1}
          tableInstanceId={1}
          tableDefId={1}
          periodKey={202609}
          readOnly={false}
          allowsDynamicRows={false}
          maxDynamicRows={null}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function paste(text: string): Promise<void> {
  // ⚠ Якір без виділення — кут таблиці (`r1:C1`).
  await act(async () => {
    fireEvent.paste(screen.getByTestId('revogrid-stub'), { clipboardData: { getData: () => text } });
    await Promise.resolve();
  });
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
  vi.mocked(showWarning).mockClear();
});

describe('DocumentGrid: вставка, більша за таблицю (F6-03)', () => {
  it('рядок і колонка не вмістилися — вставлено те, що влізло, і сказано, скільки відкинуто', async () => {
    show();
    await screen.findByTestId('revogrid-stub');
    // Дочекатися зрізу: без нього вставці нема куди лягти.
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));
    });

    await paste('1\t2\t3\n4\t5\t6\n7\t8\t9\n');

    expect([...pendingSlice(1, 202609).keys()].sort()).toEqual(['r1:C1', 'r1:C2', 'r2:C1', 'r2:C2']);
    // ⛔ Мутація: прибрати попередження в `onPaste` — тут жодного виклику.
    const shown = vi.mocked(showWarning).mock.calls.map(([message]) => String(message));
    expect(shown).toHaveLength(1);
    expect(shown[0]).toContain('grid.pasteClipped');
    expect(shown[0]).toContain('rows=1');
    expect(shown[0]).toContain('columns=1');
  });

  it('усе вмістилося — без попередження', async () => {
    show();
    await screen.findByTestId('revogrid-stub');
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));
    });

    await paste('1\t2\n3\t4\n');

    expect(pendingSlice(1, 202609).size).toBe(4);
    expect(vi.mocked(showWarning)).not.toHaveBeenCalled();
  });
});

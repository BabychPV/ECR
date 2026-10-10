import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cancelAutosave, holdRejectedEdits, resetFailedSaves } from '../autosave';
import { putPendingEdit, resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';
import type { PendingEdit } from '../useCellPatch';

/**
 * `G1-04`: модель сітки = зріз + УСЕ незбережене. Доти в `source` потрапляли
 * лише відхилені правки, і сітка, змонтована при поверненні на аркуш (чи
 * перебудована після чужої відповіді), показувала в комірці з позначкою
 * «незбережено» старе число, а «Retry save» після мережевої відмови зникав.
 *
 * ⚠ Автозбереження приглушене: предмет — що показано, а не що надіслано.
 */
vi.mock('../autosave', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../autosave')>();

  return { ...actual, scheduleAutosave: (): void => undefined };
});

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { source?: Record<string, unknown>[] }) => (
    <div data-testid="revogrid-stub">
      {(props.source ?? []).map((row) => (
        <span key={String(row['__rowKey'])} data-testid={`value-${String(row['__rowKey'])}`}>
          {String(row['C1'] ?? '')}
        </span>
      ))}
    </div>
  ),
}));

function column(): ColumnDto {
  return {
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
}

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns: [column()],
    rows: ['r1', 'r2'].map((rowKey, index) => ({
      cells: { C1: index + 1 },
      isOrphaned: false,
      label: null,
      ordinal: index,
      rowKey,
      rowVersion: `v${String(index)}`,
      rowKind: 'Item' as const,
    })),
  };
}

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify(sliceFixture()), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    ),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
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

const unsaved: PendingEdit = { rowKey: 'r2', columnCode: 'C1', value: 7, isEmpty: false, baseVersion: 'v1', before: 2 };

afterEach(() => {
  cancelAutosave();
  resetFailedSaves();
  resetPending();
  vi.unstubAllGlobals();
});

describe('DocumentGrid: незбережене видно в сітці (G1-04)', () => {
  it('сітка, змонтована з незбереженою правкою в сховищі, показує її значення, а не збережене', async () => {
    putPendingEdit(1, 202609, unsaved);
    mockServer();
    show();

    // ⛔ Мутація: повернути `shownOverrides` «лише відхилені» — тут буде 2.
    await waitFor(() => expect(screen.getByTestId('value-r2').textContent).toBe('7'));
    expect(screen.getByTestId('value-r1').textContent).toBe('1');
  });

  it('останнє збереження зрізу впало мережею — повернувшись на аркуш, людина бачить «Retry save»', async () => {
    putPendingEdit(1, 202609, unsaved);
    expect(holdRejectedEdits(1, 202609, new TypeError('Failed to fetch'), [unsaved])).toBe(false);

    mockServer();
    show();

    await screen.findByTestId('value-r2');
    expect(await screen.findByTestId('grid-retry-save')).toBeTruthy();
  });

  it('без невдалого збереження «Retry save» немає (вікно дебаунсу — не привід)', async () => {
    putPendingEdit(1, 202609, unsaved);
    mockServer();
    show();

    await screen.findByTestId('value-r2');
    expect(screen.queryByTestId('grid-retry-save')).toBeNull();
  });
});

import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cancelAutosave, holdRejectedEdits, resetFailedSaves } from '../autosave';
import { resetConfirmed } from '../confirmedEdits';
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
  RevoGrid: (props: {
    source?: Record<string, unknown>[];
    onBeforeedit?: (event: { detail: unknown; preventDefault: () => void }) => void;
    onAfteredit?: (event: { detail: unknown }) => void;
  }) => (
    <div data-testid="revogrid-stub">
      <button
        type="button"
        onClick={() => {
          const detail = { prop: 'C1', model: { __rowKey: 'r1' }, val: '5' };
          let prevented = false;
          props.onBeforeedit?.({
            detail,
            preventDefault: () => {
              prevented = true;
            },
          });
          if (!prevented) props.onAfteredit?.({ detail });
        }}
      >
        edit-r1
      </button>
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

let confirmations: Record<string, string> = {};

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: confirmations,
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
  confirmations = {};
  resetConfirmed();
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

  /*
   * ⛔ X2-02: підтверджена підстава (`overrides`) перекривала НОВІШУ незбережену правку тієї
   * самої комірки (вставка й Undo/Redo пишуть у сховище повз неї): «підтвердив 5 → вставив 7»
   * показувало 5, хоча піде 7.
   */
  it('підтверджене 5, потім незбережене 7 тієї самої комірки → на екрані 7, а не 5', async () => {
    confirmations = { 'r1:C1': 'Outside the permit window' };
    mockServer();
    show();

    await screen.findByTestId('value-r1');
    fireEvent.click(screen.getByRole('button', { name: 'edit-r1' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: /grid\.confirmProceed|Proceed/ }));
    await waitFor(() => expect(screen.getByTestId('value-r1').textContent).toBe('5'));

    act(() => {
      putPendingEdit(1, 202609, { rowKey: 'r1', columnCode: 'C1', value: 7, isEmpty: false, baseVersion: 'v0', before: 5 });
    });

    await waitFor(() => expect(screen.getByTestId('value-r1').textContent).toBe('7'));
  });
});

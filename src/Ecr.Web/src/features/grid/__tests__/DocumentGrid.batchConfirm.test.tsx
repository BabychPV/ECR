import { type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cancelAutosave, useDocumentPending } from '../autosave';
import { resetConfirmed } from '../confirmedEdits';
import { pendingSlice, resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * `ФВ-2.16`, `AllowWithConfirmation` для ПАКЕТНИХ правок.
 *
 * ⛔ Що ламалося. Діалог підтвердження жив лише в `onBeforeEdit` — тобто на
 * введенні в ОДНУ комірку. Вставка з буфера (`onPaste`) і протягування маркером
 * заповнення (`applyRangeEdit`) писали такі комірки у сховище й у PATCH мовчки,
 * а сервер підтвердження не питав узагалі.
 *
 * ⚠ Заглушка сітки повторює порядок бібліотеки (`revo-grid.entry.js`,
 * `onRangeEdit`): спершу `beforerangeedit`; якщо його НЕ скасовано — сітка
 * малює діапазон (`setRangeData`) і шле `afteredit`. Лічильник `drawn` —
 * скільки разів сітка встигла намалювати діапазон сама.
 */

let drawn = 0;

const FillData = { 0: { C1: 9, C2: 9 }, 1: { C1: 9, C2: 9 } };
const FillModels = { 0: { __rowKey: 'r1' }, 1: { __rowKey: 'r2' } };

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: {
    onBeforeedit?: (event: { detail: unknown; preventDefault: () => void }) => void;
    onBeforerangeedit?: (event: { detail: unknown; preventDefault: () => void }) => void;
    onAfteredit?: (event: { detail: unknown }) => void;
  }) => (
    <div data-testid="revogrid-stub">
      <button
        type="button"
        onClick={() => {
          const detail = { data: FillData, models: FillModels, type: 'rgRow' };
          let prevented = false;
          props.onBeforerangeedit?.({
            detail,
            preventDefault: () => {
              prevented = true;
            },
          });
          if (prevented) return;

          drawn += 1;
          props.onAfteredit?.({ detail });
        }}
      >
        fill
      </button>
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
        edit-one
      </button>
    </div>
  ),
}));

const DocumentId = 12;
const Table = 4;
const Period = 202609;

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

/** `C1` у рядках r1, r2 — поза вікном дозволу: правка лише з підтвердженням. */
function sliceFixture(confirmations: Record<string, string>): TableSliceDto {
  return {
    cellConfirmations: confirmations,
    cellPermissions: {},
    periodKey: Period,
    tableInstanceId: Table,
    columns: [column('C1', 0), column('C2', 1)],
    rows: ['r1', 'r2'].map((rowKey, ordinal) => ({
      cells: { C1: 1, C2: 2 },
      isOrphaned: false,
      label: null,
      ordinal,
      rowKey,
      rowKind: 'Item',
      rowVersion: 'v1',
    })),
  };
}

const Confirming = { 'r1:C1': 'Outside the permit window', 'r2:C1': 'Outside the permit window' };

interface SentPatch {
  confirmed: unknown;
  cells: string[];
}

const patches: SentPatch[] = [];

function mockServer(confirmations: Record<string, string> = Confirming): void {
  patches.length = 0;
  drawn = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        const request = JSON.parse(String(init.body)) as {
          confirmed?: unknown;
          rows: { rowKey: string; cells: { columnCode: string }[] }[];
        };
        const cells = request.rows.flatMap((row) => row.cells.map((cell) => `${row.rowKey}:${cell.columnCode}`));
        patches.push({ confirmed: request.confirmed, cells: cells.sort() });

        return new Response(
          JSON.stringify({
            appliedCells: cells.length,
            rowVersions: Object.fromEntries(request.rows.map((row) => [row.rowKey, 'v2'])),
            validation: [],
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      return new Response(JSON.stringify(sliceFixture(confirmations)), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

function DocumentHost(): JSX.Element {
  useDocumentPending(DocumentId);

  return (
    <DocumentGrid
      documentId={DocumentId}
      tableInstanceId={Table}
      tableDefId={1}
      periodKey={Period}
      readOnly={false}
      allowsDynamicRows={false}
      maxDynamicRows={null}
    />
  );
}

async function show(): Promise<void> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentHost />
      </QueryClientProvider>
    </MantineProvider>,
  );

  await screen.findByTestId('revogrid-stub');
}

async function wait(ms: number): Promise<void> {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, ms));
  });
}

/** Вставка з кута таблиці: `7 1 / 8 2` лягає на r1:C1, r1:C2, r2:C1, r2:C2. */
function paste(): void {
  fireEvent.paste(screen.getByTestId('revogrid-stub'), {
    clipboardData: { getData: () => '7\t1\n8\t2' },
  });
}

const BatchBody = /grid\.batchConfirmBody|need your confirmation/;

/** Діалог підтвердження пакета — з кількістю таких комірок у тексті. */
async function batchDialog(): Promise<HTMLElement> {
  const body = await screen.findByText(BatchBody);
  const dialog = body.closest<HTMLElement>('[role="dialog"]');
  expect(dialog).not.toBeNull();

  return dialog!;
}

/** Діалогу пакета немає (Mantine монтує модалку з переходом — чекаємо його). */
async function noBatchDialog(): Promise<void> {
  await wait(300);
  expect(screen.queryByText(BatchBody)).toBeNull();
}

function click(dialog: HTMLElement, name: RegExp): void {
  fireEvent.click(within(dialog).getByRole('button', { name }));
}

const Proceed = /grid\.confirmProceed|Proceed/;
const Cancel = /grid\.confirmCancel|^Cancel$/;

afterEach(() => {
  cancelAutosave();
  resetPending();
  resetConfirmed();
  vi.unstubAllGlobals();
});

describe('ФВ-2.16: підтвердження пакетних правок', () => {
  it('вставка з коміркою «з підтвердженням» — один діалог із кількістю; «Скасувати» — нічого', async () => {
    mockServer();
    await show();

    paste();

    const dialog = await batchDialog();
    // ⚠ Кількість — лише комірки, що вимагають підтвердження (r1:C1, r2:C1), не весь пакет.
    expect(within(dialog).getByText(/count=2|^2 cell/)).toBeTruthy();
    expect(screen.getAllByRole('dialog')).toHaveLength(1);

    // ⛔ До відповіді НІЧОГО не застосовано.
    expect(pendingSlice(Table, Period).size).toBe(0);

    click(dialog, Cancel);
    await wait(700);

    // ⛔ Пакет не застосовано ЦІЛКОМ — і звичайні комірки поруч теж.
    expect(pendingSlice(Table, Period).size).toBe(0);
    expect(patches).toHaveLength(0);
  });

  it('вставка: «Продовжити» — один PATCH із прапорцем confirmed і всім пакетом', async () => {
    mockServer();
    await show();

    paste();
    click(await batchDialog(), Proceed);
    await wait(300);

    expect(patches).toEqual([{ confirmed: true, cells: ['r1:C1', 'r1:C2', 'r2:C1', 'r2:C2'] }]);
  });

  it('вставка без таких комірок — без діалогу й без прапорця (регрес)', async () => {
    mockServer({});
    await show();

    paste();
    await noBatchDialog();

    expect(patches).toEqual([{ confirmed: undefined, cells: ['r1:C1', 'r1:C2', 'r2:C1', 'r2:C2'] }]);
  });

  it('протягування: діапазон заблоковано до діалогу; «Скасувати» — сітка не малювала, сховище порожнє', async () => {
    mockServer();
    await show();

    fireEvent.click(screen.getByRole('button', { name: 'fill' }));

    const dialog = await batchDialog();
    expect(within(dialog).getByText(/count=2|^2 cell/)).toBeTruthy();

    // ⛔ Сітка НЕ намалювала діапазон: відкочувати на «Скасувати» нічого.
    expect(drawn).toBe(0);
    expect(pendingSlice(Table, Period).size).toBe(0);

    click(dialog, Cancel);
    await wait(700);

    expect(drawn).toBe(0);
    expect(pendingSlice(Table, Period).size).toBe(0);
    expect(patches).toHaveLength(0);
  });

  it('протягування: «Продовжити» — один PATCH із прапорцем confirmed', async () => {
    mockServer();
    await show();

    fireEvent.click(screen.getByRole('button', { name: 'fill' }));
    click(await batchDialog(), Proceed);
    await wait(300);

    expect(patches).toEqual([{ confirmed: true, cells: ['r1:C1', 'r1:C2', 'r2:C1', 'r2:C2'] }]);
  });

  it('протягування без таких комірок — сітка малює сама, без діалогу й без прапорця (регрес)', async () => {
    mockServer({});
    await show();

    fireEvent.click(screen.getByRole('button', { name: 'fill' }));
    await noBatchDialog();

    expect(drawn).toBe(1);
    expect(patches).toEqual([{ confirmed: undefined, cells: ['r1:C1', 'r1:C2', 'r2:C1', 'r2:C2'] }]);
  });

  it('одинична правка після свого діалогу теж їде з прапорцем confirmed', async () => {
    mockServer();
    await show();

    fireEvent.click(screen.getByRole('button', { name: 'edit-one' }));

    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: Proceed }));

    // ⚠ Одинична правка йде автозбереженням (дебаунс 500 мс), а не негайно.
    await wait(900);

    expect(patches).toEqual([{ confirmed: true, cells: ['r1:C1'] }]);
  });
});

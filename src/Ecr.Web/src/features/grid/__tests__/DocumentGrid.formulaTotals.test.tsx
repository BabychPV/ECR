import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { resetFocus } from '../focusStore';
import { TotalsRowKey } from '../gridTotals';
import { NoLocalFlags } from '../cellState';
import { DocumentGrid, gridColumns } from '../DocumentGrid';

/**
 * `UI-08`: підключення рядка формули й рядка підсумків до сітки документа.
 *
 * ⛔ Чисті модулі (`gridTotals.ts`, `formulaBar.ts`) мають власні тести; тут
 * перевіряється рівно те, чого вони довести не можуть, — що сітка справді
 * ОТРИМУЄ закріплений рядок і що подія фокуса з тіньового дерева доходить до
 * рядка формули. Обидва з'єднання беззвучні: без них екран виглядає так само,
 * як і до цієї роботи.
 *
 * ⚠ RevoGrid підмінено — той самий прийом, що в `DocumentGrid.saveError.test.tsx`
 * (справжній веб-компонент у jsdom лише заважає). Заглушка ВІДДАЄ назад те, що
 * їй передали, і вміє підняти `focuscell` тим самим шляхом, що й
 * `revogr-overlay-selection`: `bubbles: true, composed: true`.
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { pinnedBottomSource?: unknown[] }) => (
    <div
      data-testid="revogrid-stub"
      data-pinned-bottom={JSON.stringify(props.pinnedBottomSource ?? null)}
      ref={(node) => {
        if (node !== null) stub = node;
      }}
    />
  ),
}));

let stub: HTMLElement | null = null;

/** Підіймає `focuscell` так, як його шле `revogr-overlay-selection`. */
function focusCell(columnIndex: number, rowIndex: number): void {
  stub?.dispatchEvent(
    new CustomEvent('focuscell', {
      bubbles: true,
      composed: true,
      detail: {
        rowType: 'rgRow',
        colType: 'rgCol',
        focus: { x: columnIndex, y: rowIndex },
        end: { x: columnIndex, y: rowIndex },
      },
    }),
  );
}

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns: [
      {
        code: 'C1',
        dataType: 'Decimal',
        defaultValue: null,
        displayFormat: null,
        header: 'Викиди',
        id: 1,
        isReadOnly: false,
        isRequired: false,
        isRequiredByMethodology: false,
        lookupRegistryDefId: null,
        ordinal: 0,
        unitId: null,
        unitSymbol: 'т',
      },
      {
        code: 'F1',
        dataType: 'Formula',
        defaultValue: null,
        displayFormat: null,
        header: 'Разом',
        id: 2,
        isReadOnly: true,
        isRequired: false,
        isRequiredByMethodology: false,
        lookupRegistryDefId: null,
        ordinal: 1,
        unitId: null,
        unitSymbol: null,
      },
    ],
    rows: [
      {
        cells: { C1: '10', F1: '1.5' },
        isOrphaned: false,
        label: 'Діоксид вуглецю',
        ordinal: 0,
        rowKey: 'r1',
        rowKind: 'Item',
        rowVersion: 'v1',
      },
      {
        cells: { C1: '2.5' },
        isOrphaned: false,
        label: 'Метан',
        ordinal: 1,
        rowKey: 'r2',
        rowKind: 'Item',
        rowVersion: 'v2',
      },
    ],
  };
}

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(
      async () =>
        new Response(JSON.stringify(sliceFixture()), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
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
          periodKey={202609}
          readOnly={false}
          allowsDynamicRows={false}
          maxDynamicRows={null}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function pinnedBottom(): Record<string, unknown>[] {
  const attribute = screen.getByTestId('revogrid-stub').dataset['pinnedBottom'] ?? 'null';

  return JSON.parse(attribute) as Record<string, unknown>[];
}

function bar(): HTMLElement {
  const found = document.querySelector('[data-formula-bar]');
  if (found === null) throw new Error('Рядка формули немає в DOM');

  return found as HTMLElement;
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  resetFocus();
  stub = null;
  vi.unstubAllGlobals();
});

describe('DocumentGrid: рядок підсумків (UI-08)', () => {
  it('сітка отримує ЗАКРІПЛЕНИЙ знизу рядок із сумами колонок', async () => {
    /*
     * ⛔ Мутаційний доказ: прибери `pinnedBottomSource` з `<RevoGrid>` — і
     * тест падає `expected [] to have a length of 1`. Порахований підсумок,
     * який нікуди не переданий, на екрані не видно взагалі.
     *
     * ⛔ І це саме ЗАКРІПЛЕНИЙ рядок, а не зайвий рядок у `source`: доданий у
     * дані, він поїхав би у вставку, копіювання і `PATCH` як звичайний.
     */
    mockServer();
    show();

    await screen.findByTestId('revogrid-stub');

    await waitFor(() => expect(pinnedBottom()).toHaveLength(1));

    const totals = pinnedBottom()[0] ?? {};

    expect(totals['__rowKey']).toBe(TotalsRowKey);
    expect(totals['C1']).toBe('12.5');
    expect(totals['F1']).toBe('1.5');
  });

  it('підпис підсумку лягає в колонку підпису рядків, яка в цій таблиці є', async () => {
    mockServer();
    show();

    await screen.findByTestId('revogrid-stub');
    await waitFor(() => expect(pinnedBottom()).toHaveLength(1));

    expect(String(pinnedBottom()[0]?.['__rowLabel'])).toContain('totalsRowLabel');
  });

  it('комірка підсумку — лише для читання, хоч документ і відкритий на запис', async () => {
    /*
     * ⛔ `decide()` на невідомому рядку звичайної колонки чесно віддає
     * «можна»: заборон у `cellPermissions` на нього немає. Без перевірки
     * `isTotalsRow` RevoGrid відкрив би редактор на сумі, а `captureEdit`
     * мовчки викинув би введене — правка, яка виглядає зробленою і нікуди не
     * доїжджає.
     */
    mockServer();
    show();

    await screen.findByTestId('revogrid-stub');
    await waitFor(() => expect(pinnedBottom()).toHaveLength(1));

    const columns = gridColumns(sliceFixture(), false, NoLocalFlags, {}, {
      blocked: new Map(),
      warning: new Map(),
    });

    const dataColumn = columns.find((c) => c.prop === 'C1');
    const readonly = dataColumn?.readonly as (p: { model: unknown }) => boolean;

    expect(readonly({ model: { __rowKey: TotalsRowKey } })).toBe(true);

    // ⚠ Опудало: звичайний рядок тієї самої колонки лишається редагованим —
    // інакше твердження вище доводило б лише те, що сітку замкнули цілком.
    expect(readonly({ model: { __rowKey: 'r1' } })).toBe(false);
  });
});

describe('DocumentGrid: рядок формули (UI-08)', () => {
  it('подія фокуса з тіньового дерева доходить до рядка формули', async () => {
    /*
     * ⛔ Мутаційний доказ: прибери `trackFocusedCell` із `gridContainerRef` —
     * і рядок формули назавжди лишається в стані «оберіть комірку», бо фокус
     * у сховище не потрапляє ніколи.
     */
    mockServer();
    show();

    await screen.findByTestId('revogrid-stub');
    await waitFor(() => expect(pinnedBottom()).toHaveLength(1));

    expect(bar().dataset['formulaBar']).toBe('empty');

    focusCell(1, 0);

    await waitFor(() => expect(bar().dataset['formulaBar']).toBe('cell'));

    // ⚠ Індекс 0 — колонка підпису рядків (вона в цій таблиці є), тож
    // індекс 1 має дати ПЕРШУ колонку зрізу з її одиницею.
    expect(bar().textContent).toContain('Діоксид вуглецю');
    expect(bar().textContent).toContain('Викиди, т');
  });

  it('фокус на обчислюваній колонці оголошує комірку обчислюваною', async () => {
    mockServer();
    show();

    await screen.findByTestId('revogrid-stub');
    await waitFor(() => expect(pinnedBottom()).toHaveLength(1));

    focusCell(2, 0);

    await waitFor(() =>
      expect(bar().querySelector('[data-formula-bar-calculated]')).not.toBeNull(),
    );

    // ⚠ Вираз сервер зрізом не віддає (`formulaBar.ts`) — і рядок формули
    // каже це словами, а не мовчить.
    expect(bar().querySelector('[data-formula-bar-no-expression]')).not.toBeNull();
  });

  it('подія закріпленої секції фокус НЕ рухає — індекси там від своєї секції', async () => {
    mockServer();
    show();

    await screen.findByTestId('revogrid-stub');
    await waitFor(() => expect(pinnedBottom()).toHaveLength(1));

    stub?.dispatchEvent(
      new CustomEvent('focuscell', {
        bubbles: true,
        composed: true,
        detail: { rowType: 'rowPinEnd', colType: 'rgCol', focus: { x: 1, y: 0 } },
      }),
    );

    // ⛔ Клік по самому рядку підсумків не має підміняти адресу в рядку
    // формули: нумерація закріпленої секції власна, і рядок 0 там — це
    // підсумок, а не перший рядок таблиці.
    expect(bar().dataset['formulaBar']).toBe('empty');
  });
});

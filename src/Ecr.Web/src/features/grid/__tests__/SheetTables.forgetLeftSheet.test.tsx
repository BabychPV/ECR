import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { JSX } from 'react';
import type { DocumentTableDto } from '@/api/types';
import { SheetTables } from '../SheetTables';

/**
 * C1-01: у режимі «одна таблиця» відвідані таблиці лишаються змонтованими прихованими сітками — це
 * свідомо (правки, Undo). Але:
 *   1. прихована сітка має знати, що вона прихована (`hidden`), інакше кожен перерахунок
 *      перезапитує її зріз (`invalidateSlices`);
 *   2. після переходу на інший аркуш/період і назад НЕ можна монтувати всі колись відвідані
 *      сітки одним комітом: їх уже розмонтовано, а id лишалися в `mounted`.
 *
 * ⚠ `DocumentGrid` підмінений, `IntersectionObserver` прибрано: тоді `SheetTables` монтує лише
 * вибрану таблицю (свідома деградація без спостерігача), і рішення видно без розкладки.
 */
vi.mock('../DocumentGrid', () => ({
  DocumentGrid: ({ tableInstanceId, hidden }: { tableInstanceId: number; hidden?: boolean }): JSX.Element => (
    <div data-testid={`grid-${String(tableInstanceId)}`} data-hidden={hidden === true ? 'true' : 'false'} />
  ),
}));

function table(index: number, sheetDefId: number): DocumentTableDto {
  return {
    allowsDynamicRows: false,
    maxDynamicRows: null,
    sheetCode: `S${String(sheetDefId)}`,
    sheetDefId,
    sheetNameL10n: { values: { en: 'Sheet' } },
    sheetOrdinal: sheetDefId,
    tableCode: `T${String(index)}`,
    tableDefId: index,
    tableInstanceId: 1000 + index,
    tableNameL10n: { values: { en: `Table ${String(index)}` } },
    tableOrdinal: index,
  } as DocumentTableDto;
}

// ⚠ Стабільна ідентичність масивів — як `useMemo` у `DocumentPage`.
const SheetA = [table(1, 1), table(2, 1), table(3, 1)];
const SheetB = [table(4, 2)];

function ui(tables: DocumentTableDto[], selected: number): JSX.Element {
  return (
    <MantineProvider>
      <SheetTables
        documentId={7}
        periodKey={202609}
        readOnly={false}
        tables={tables}
        layout="single"
        selectedTableInstanceId={selected}
        onSelectTable={() => undefined}
      />
    </MantineProvider>
  );
}

const mountedSlots = (): number => document.querySelectorAll('[data-table-mounted="true"]').length;

describe('C1-01 · SheetTables «одна таблиця»', () => {
  beforeEach(() => {
    vi.stubGlobal('IntersectionObserver', undefined);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('невибрана змонтована сітка отримує hidden, вибрана — ні', () => {
    const view = render(ui(SheetA, 1001));
    view.rerender(ui(SheetA, 1002));

    expect(view.getByTestId('grid-1001').getAttribute('data-hidden')).toBe('true');
    expect(view.getByTestId('grid-1002').getAttribute('data-hidden')).toBe('false');
  });

  it('повернення на аркуш монтує лише вибрану таблицю, а не всі колись відвідані', () => {
    const view = render(ui(SheetA, 1001));
    view.rerender(ui(SheetA, 1002));
    view.rerender(ui(SheetA, 1003));
    expect(mountedSlots()).toBe(3);

    view.rerender(ui(SheetB, 1004));
    expect(mountedSlots()).toBe(1);

    view.rerender(ui(SheetA, 1001));

    // ⛔ Ядро: до виправлення тут було 3 — id 1002 і 1003 лишалися в `mounted`.
    expect(mountedSlots()).toBe(1);
    expect(view.queryByTestId('grid-1002')).toBeNull();
    expect(view.queryByTestId('grid-1003')).toBeNull();
  });
});

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useEffect, useState, type JSX } from 'react';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import type { DocumentTableDto } from '@/api/types';
import { SheetWorkspace } from '../SheetWorkspace';

/**
 * Робоче місце аркуша (`UI-22`): дерево + одна таблиця, вибір в адресі.
 *
 * ⛔ Приймання п.3 картки: перехід між таблицями НЕ губить незбережені правки й Undo. Вони
 * живуть у стані `DocumentGrid` (`SheetTables.tsx`), тож доказ — сітка, яку вже змонтовано,
 * не розмонтовується і зберігає свій стан, поки людина дивиться на іншу таблицю.
 */
const lifecycle = { mounts: new Map<number, number>(), unmounts: new Map<number, number>() };

vi.mock('../DocumentGrid', () => ({
  DocumentGrid: ({ tableInstanceId }: { tableInstanceId: number }): JSX.Element => {
    // Підміна «незбережених правок»: лічильник у стані компонента, як `pending` у сітці.
    const [edits, setEdits] = useState(0);

    useEffect(() => {
      lifecycle.mounts.set(tableInstanceId, (lifecycle.mounts.get(tableInstanceId) ?? 0) + 1);
      return () => {
        lifecycle.unmounts.set(tableInstanceId, (lifecycle.unmounts.get(tableInstanceId) ?? 0) + 1);
      };
    }, [tableInstanceId]);

    return (
      <button type="button" data-testid={`grid-${String(tableInstanceId)}`} onClick={() => setEdits((n) => n + 1)}>
        edits:{edits}
      </button>
    );
  },
}));

vi.mock('@/features/documents/api', () => ({
  useTableStatus: () => ({
    data: [
      { tableDefId: 1, sheetCode: 'S1', filledCells: 5, inputCells: 5, errorCount: 0, warningCount: 0 },
      { tableDefId: 2, sheetCode: 'S1', filledCells: 0, inputCells: 5, errorCount: 2, warningCount: 0 },
    ],
  }),
}));

function table(ordinal: number): DocumentTableDto {
  return {
    allowsDynamicRows: false,
    maxDynamicRows: null,
    sheetCode: 'S1',
    sheetDefId: 1,
    sheetNameL10n: { values: { en: 'Sheet' } },
    sheetOrdinal: 1,
    tableCode: `T${String(ordinal)}`,
    tableDefId: ordinal,
    tableInstanceId: 100 + ordinal,
    tableNameL10n: { values: { en: `Table ${String(ordinal)}` } },
    tableOrdinal: ordinal,
  };
}

// ⚠ Стабільна ідентичність масиву — так само, як `useMemo` у `DocumentPage`.
const Tables = [table(1), table(2), table(3)];

let search = '';
function LocationProbe(): null {
  search = useLocation().search;
  return null;
}

function renderWorkspace(entry = '/documents/1?periodKey=202401'): void {
  render(
    <MantineProvider>
      <MemoryRouter initialEntries={[entry]}>
        <SheetWorkspace documentId={1} periodKey={202401} readOnly={false} tables={Tables} />
        <LocationProbe />
      </MemoryRouter>
    </MantineProvider>,
  );
}

const slot = (id: number): HTMLElement | null => document.querySelector(`[data-table-slot="${String(id)}"]`);
const treeItem = (ordinal: number): HTMLElement =>
  within(screen.getByRole('tree')).getAllByRole('treeitem')[ordinal - 1]!;

// ⚠ Очищення ДО тесту: розмонтування попереднього тесту (`cleanup` бібліотеки) приходить
// уже після наших `afterEach` і інакше потрапило б у лічильники наступного.
beforeEach(() => {
  lifecycle.mounts.clear();
  lifecycle.unmounts.clear();
  vi.stubGlobal('IntersectionObserver', VisibleObserver);
});

/**
 * Спостерігач «як у браузері» для режиму однієї таблиці: спостережуваний слот — єдиний видимий,
 * тож про нього повідомляється в наступному такті (у jsdom розкладки немає, а заглушка з
 * `src/test/setup.ts` інертна).
 */
class VisibleObserver {
  private readonly targets = new Set<Element>();

  constructor(private readonly callback: IntersectionObserverCallback) {}

  observe(target: Element): void {
    this.targets.add(target);
    setTimeout(() => {
      if (!this.targets.has(target)) return;
      this.callback(
        [{ target, isIntersecting: true } as unknown as IntersectionObserverEntry],
        this as unknown as IntersectionObserver,
      );
    }, 0);
  }

  unobserve(target: Element): void {
    this.targets.delete(target);
  }

  disconnect(): void {
    this.targets.clear();
  }

  takeRecords(): IntersectionObserverEntry[] {
    return [];
  }
}

afterEach(() => {
  window.localStorage.clear();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('SheetWorkspace', () => {
  it('без ?table= — перша таблиця, на екрані лише вона', async () => {
    renderWorkspace();

    expect(slot(101)?.getAttribute('data-table-selected')).toBe('true');
    expect(await screen.findByTestId('grid-101')).toBeTruthy();
    // Інші таблиці слота не мають зовсім: жодного зайвого запиту зрізу.
    expect(slot(102)).toBeNull();
    expect(slot(103)).toBeNull();
    expect(treeItem(1).getAttribute('aria-selected')).toBe('true');
  });

  it('посилання з ?table= відновлює вибір', () => {
    renderWorkspace('/documents/1?periodKey=202401&table=T3');

    expect(slot(103)?.getAttribute('data-table-selected')).toBe('true');
    expect(slot(101)).toBeNull();
    expect(treeItem(3).getAttribute('aria-selected')).toBe('true');
  });

  it('вибір у дереві оновлює ?table= і переносить фокус на заголовок таблиці', async () => {
    renderWorkspace();

    treeItem(2).focus();
    fireEvent.keyDown(treeItem(2), { key: 'Enter' });
    await screen.findByTestId('grid-102');

    expect(new URLSearchParams(search).get('table')).toBe('T2');
    expect(slot(102)?.getAttribute('data-table-selected')).toBe('true');
    expect(document.activeElement).toBe(slot(102)?.querySelector('[data-table-title]'));
  });

  it('перехід між таблицями не розмонтовує сітку і не губить її стан (правки, Undo)', async () => {
    renderWorkspace();

    // «Незбережена правка» в першій таблиці.
    fireEvent.click(await screen.findByTestId('grid-101'));
    expect(screen.getByTestId('grid-101').textContent).toBe('edits:1');

    fireEvent.click(treeItem(2));
    await screen.findByTestId('grid-102');
    expect(slot(101)?.style.display).toBe('none');
    expect(slot(102)?.style.display).toBe('');

    fireEvent.click(treeItem(1));

    expect(lifecycle.unmounts.get(101) ?? 0).toBe(0);
    expect(lifecycle.mounts.get(101)).toBe(1);
    expect(screen.getByTestId('grid-101').textContent).toBe('edits:1');
    // Друга таблиця теж лишилася змонтованою, лише прихованою.
    expect(slot(102)?.style.display).toBe('none');
    expect(lifecycle.unmounts.get(102) ?? 0).toBe(0);
  });

  it('перемикач ховає дерево і пам’ятає це; aria-expanded каже стан', () => {
    renderWorkspace();

    const toggle = screen.getByTestId('table-navigator-toggle');
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(toggle.getAttribute('aria-controls')).toBe(screen.getByTestId('table-navigator').id);

    fireEvent.click(toggle);
    expect(screen.getByTestId('table-navigator').hidden).toBe(true);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(window.localStorage.getItem('ecr.tableNavigator.hidden')).toBe('1');
  });

  it('?view=all — колишній стос усіх таблиць, без дерева', () => {
    renderWorkspace('/documents/1?periodKey=202401&view=all');

    expect(screen.queryByTestId('table-navigator')).toBeNull();
    expect(document.querySelectorAll('[data-table-slot]')).toHaveLength(3);
  });
});

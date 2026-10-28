import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { JSX } from 'react';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import type { DocumentTableDto } from '@/api/types';
import { SheetWorkspace } from '../SheetWorkspace';

/**
 * ✎ ui-sheet-bottom: нижній рядок (смуга аркушів + горизонтальна прокрутка таблиці) стоїть У РАМЦІ панелі
 * таблиці, ПІД сіткою; без нього штатна прокрутка RevoGrid лишається видимою.
 */
vi.mock('@/features/grid/DocumentGrid', () => ({
  DocumentGrid: ({ tableInstanceId }: { tableInstanceId: number }): JSX.Element => (
    <div data-testid={`grid-${String(tableInstanceId)}`} />
  ),
}));
vi.mock('@/features/documents/api', () => ({ useTableStatus: () => ({ data: [] }) }));

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

const Tables = [table(1), table(2)];

class VisibleObserver {
  private readonly targets = new Set<Element>();
  constructor(private readonly callback: IntersectionObserverCallback) {}
  observe(target: Element): void {
    this.targets.add(target);
    setTimeout(() => {
      if (!this.targets.has(target)) return;
      this.callback([{ target, isIntersecting: true } as unknown as IntersectionObserverEntry], this as unknown as IntersectionObserver);
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

beforeEach(() => {
  window.localStorage.clear();
  vi.stubGlobal('IntersectionObserver', VisibleObserver);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function show(entry: string, withBar: boolean): void {
  render(
    <MantineProvider>
      <MemoryRouter initialEntries={[entry]}>
        <SheetWorkspace
          documentId={1}
          periodKey={202610}
          readOnly={false}
          tables={Tables}
          bottomBar={
            withBar
              ? (scroll) => (
                  <div data-testid="bottom-bar" className="doc-sheet-bar">
                    <div data-testid="bar-scroll">{scroll}</div>
                  </div>
                )
              : undefined
          }
        />
      </MemoryRouter>
    </MantineProvider>,
  );
}

describe('SheetWorkspace: нижній рядок', () => {
  it('рядок — всередині рамки панелі, ПІСЛЯ сітки, з горизонтальною прокруткою справа', async () => {
    show('/documents/1?periodKey=202610', true);

    const panel = document.querySelector('.ecr-sheet-panel') as HTMLElement;
    expect(panel.classList.contains('ecr-sheet-panel--bar')).toBe(true);
    const grid = await screen.findByTestId('grid-101');
    const bar = screen.getByTestId('bottom-bar');
    expect(panel.contains(bar)).toBe(true);
    expect(grid.compareDocumentPosition(bar) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(screen.getByTestId('bar-scroll').querySelector('[data-testid="grid-hscroll"]')).not.toBeNull();
  });

  it('без нижнього рядка штатна прокрутка RevoGrid не ховається (клас --bar відсутній)', () => {
    show('/documents/1?periodKey=202610', false);

    const panel = document.querySelector('.ecr-sheet-panel') as HTMLElement;
    expect(panel.classList.contains('ecr-sheet-panel--bar')).toBe(false);
    expect(screen.queryByTestId('grid-hscroll')).toBeNull();
  });

  it('повний екран: той самий рядок лишається внизу всередині region', () => {
    show('/documents/1?periodKey=202610&full=1', true);

    const region = screen.getByRole('region');
    expect(region.contains(screen.getByTestId('bottom-bar'))).toBe(true);
  });

  it('кілька таблиць (?view=all): рядок один, під останньою таблицею', async () => {
    show('/documents/1?periodKey=202610&view=all', true);

    expect(screen.getAllByTestId('bottom-bar')).toHaveLength(1);
    const slots = document.querySelectorAll('[data-table-slot]');
    const last = slots[slots.length - 1] as HTMLElement;
    expect(last.compareDocumentPosition(screen.getByTestId('bottom-bar')) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });
});

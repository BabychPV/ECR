import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { JSX } from 'react';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import type { DocumentTableDto } from '@/api/types';
import { isFullToggleKey, SheetWorkspace, shouldExitOnEscape } from '../SheetWorkspace';

/**
 * ui-sheet-focus: згортання панелі таблиць (A) і режим «на весь екран» (B).
 */
vi.mock('@/features/grid/DocumentGrid', () => ({
  DocumentGrid: ({ tableInstanceId }: { tableInstanceId: number }): JSX.Element => (
    <input data-testid={`grid-${String(tableInstanceId)}`} defaultValue="x" />
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

const One = [table(1)];
const Three = [table(1), table(2), table(3)];

let search = '';
function Probe(): null {
  search = useLocation().search;
  return null;
}

function show(tables: DocumentTableDto[], entry = '/documents/1?periodKey=202610'): void {
  render(
    <MantineProvider>
      <MemoryRouter initialEntries={[entry]}>
        <SheetWorkspace documentId={1} periodKey={202610} readOnly={false} tables={tables} />
        <Probe />
      </MemoryRouter>
    </MantineProvider>,
  );
}

/** Спостерігач «як у браузері»: єдиний слот видимий (у jsdom розкладки немає). */
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

beforeEach(() => {
  window.localStorage.clear();
  vi.stubGlobal('IntersectionObserver', VisibleObserver);
});

afterEach(() => {
  window.localStorage.clear();
  document.querySelectorAll('revogr-edit').forEach((node) => node.remove());
});

describe('панель таблиць: згортання', () => {
  it('одна таблиця і вибору немає — згорнуто, є смужка з кількістю', () => {
    show(One);

    expect(screen.getByTestId('table-navigator').hidden).toBe(true);
    expect(screen.getByTestId('table-navigator-rail-count').textContent).toBe('1');
    expect(screen.getByTestId('table-navigator-rail-btn').getAttribute('aria-expanded')).toBe('false');
  });

  it('кілька таблиць — розгорнуто, у шапці є «Сховати»', () => {
    show(Three);

    expect(screen.getByTestId('table-navigator').hidden).toBe(false);
    expect(screen.queryByTestId('table-navigator-rail')).toBeNull();
    expect(screen.getByTestId('table-navigator-hide')).toBeTruthy();
  });

  it('явний вибір сильніший за дефолт «одна таблиця»', () => {
    window.localStorage.setItem('ecr.tableNavigator.hidden', '0');
    show(One);

    expect(screen.getByTestId('table-navigator').hidden).toBe(false);
  });

  it('«Сховати» → смужка, вибір пам’ятається, фокус на кнопці смужки; розгорнути — назад', () => {
    vi.useFakeTimers();
    show(Three);

    fireEvent.click(screen.getByTestId('table-navigator-hide'));
    act(() => {
      vi.runAllTimers();
    });
    expect(screen.getByTestId('table-navigator').hidden).toBe(true);
    expect(window.localStorage.getItem('ecr.tableNavigator.hidden')).toBe('1');
    expect(document.activeElement).toBe(screen.getByTestId('table-navigator-rail-btn'));

    fireEvent.click(screen.getByTestId('table-navigator-rail-btn'));
    act(() => {
      vi.runAllTimers();
    });
    expect(screen.getByTestId('table-navigator').hidden).toBe(false);
    expect(window.localStorage.getItem('ecr.tableNavigator.hidden')).toBe('0');
    expect(document.activeElement).toBe(screen.getByTestId('table-navigator-hide'));
    vi.useRealTimers();
  });
});

describe('таблиця на весь екран', () => {
  it('кнопка входить і виходить, стан у адресі (&full=1)', () => {
    show(Three);

    const button = screen.getByTestId('sheet-full-toggle');
    expect(button.getAttribute('data-full')).toBe('false');

    fireEvent.click(button);
    expect(new URLSearchParams(search).get('full')).toBe('1');
    expect(screen.getByRole('region', { name: 'Table 1' })).toBeTruthy();
    expect(screen.getByTestId('table-navigator').hidden).toBe(true);
    expect(document.body.hasAttribute('data-sheet-full')).toBe(true);

    fireEvent.click(screen.getByTestId('sheet-full-toggle'));
    expect(new URLSearchParams(search).get('full')).toBeNull();
    expect(screen.queryByRole('region')).toBeNull();
    expect(document.body.hasAttribute('data-sheet-full')).toBe(false);
  });

  it('посилання з full=1 одразу відкриває повний екран, сітка та сама (не перемонтована)', async () => {
    show(Three, '/documents/1?periodKey=202610&full=1');

    expect(screen.getByRole('region', { name: 'Table 1' })).toBeTruthy();
    expect(await screen.findByTestId('grid-101')).toBeTruthy();
  });

  it('Ctrl+Shift+Enter вмикає, Esc вимикає і повертає фокус на кнопку', () => {
    vi.useFakeTimers();
    show(Three);

    act(() => {
      document.body.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Enter', ctrlKey: true, shiftKey: true, bubbles: true, cancelable: true }),
      );
    });
    expect(new URLSearchParams(search).get('full')).toBe('1');

    act(() => {
      document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
      vi.runAllTimers();
    });
    expect(new URLSearchParams(search).get('full')).toBeNull();
    expect(document.activeElement).toBe(screen.getByTestId('sheet-full-toggle'));
    vi.useRealTimers();
  });

  it('Esc у редакторі комірки НЕ виходить з повного екрана і не перехоплюється', async () => {
    show(Three, '/documents/1?periodKey=202610&full=1');

    // Редактор RevoGrid у DOM: Esc належить йому (скасувати правку).
    document.body.appendChild(document.createElement('revogr-edit'));
    const event = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
    const grid = await screen.findByTestId('grid-101');
    act(() => {
      grid.dispatchEvent(event);
    });

    expect(new URLSearchParams(search).get('full')).toBe('1');
    expect(event.defaultPrevented).toBe(false);
  });

  it('shouldExitOnEscape: поле вводу і відкритий діалог мають власний Esc', () => {
    const escape = (): KeyboardEvent => new KeyboardEvent('keydown', { key: 'Escape', cancelable: true });
    expect(shouldExitOnEscape(escape())).toBe(true);

    const dialog = document.createElement('div');
    dialog.setAttribute('role', 'dialog');
    document.body.appendChild(dialog);
    expect(shouldExitOnEscape(escape())).toBe(false);
    dialog.remove();

    expect(shouldExitOnEscape(new KeyboardEvent('keydown', { key: 'Enter' }))).toBe(false);
  });

  it('isFullToggleKey: лише Ctrl/Cmd+Shift+Enter', () => {
    expect(isFullToggleKey(new KeyboardEvent('keydown', { key: 'Enter', ctrlKey: true, shiftKey: true }))).toBe(true);
    expect(isFullToggleKey(new KeyboardEvent('keydown', { key: 'Enter', ctrlKey: true }))).toBe(false);
    expect(isFullToggleKey(new KeyboardEvent('keydown', { key: 'Enter', shiftKey: true }))).toBe(false);
  });
});

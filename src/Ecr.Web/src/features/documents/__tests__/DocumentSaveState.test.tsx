import { afterEach, describe, expect, it } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { DocumentSaveState } from '@/features/documents/DocumentSaveState';
import { discardPendingEdit, openDocument, putPendingEdit, resetPending } from '@/features/grid/pendingStore';
import { publishTableAttention, resetRowAttentionForTests } from '@/features/grid/rowAttentionStore';
import { testTheme } from '@/test/render';

/** UI-14: стан збереження документа словами (макет `renderSave`). */
const Table = 7;
const Period = 202610;

function edit(rowKey: string, columnCode: string, value: number) {
  return { rowKey, columnCode, value, isEmpty: false, baseVersion: 'V1' };
}

function show(readOnly = false): HTMLElement {
  render(
    <MantineProvider theme={testTheme}>
      <DocumentSaveState readOnly={readOnly} />
    </MantineProvider>,
  );

  return screen.getByTestId('document-save-state');
}

afterEach(() => {
  resetPending();
  act(() => resetRowAttentionForTests());
});

describe('DocumentSaveState', () => {
  it('без правок — «All changes saved», а не вигаданий час', () => {
    openDocument(1);
    const state = show();

    expect(state.textContent).toBe('⟦document.saveState.allSaved⟧');
    expect(state.getAttribute('aria-live')).toBe('polite');
  });

  it('незбережені правки документа — число словами; спорожніло — «Saved HH:MM»', () => {
    openDocument(1);
    const state = show();

    act(() => {
      putPendingEdit(Table, Period, edit('R1', 'C1', 1));
      putPendingEdit(Table, Period, edit('R1', 'C2', 2));
    });
    // ⛔ Мутаційний доказ: покажи «Saved» незалежно від лічильника — тут червоне.
    expect(state.getAttribute('data-save-state')).toBe('unsaved');
    expect(state.textContent).toBe('⟦document.saveState.unsaved.other (count=2)⟧');

    act(() => {
      discardPendingEdit(Table, Period, { rowKey: 'R1', columnCode: 'C1' });
      discardPendingEdit(Table, Period, { rowKey: 'R1', columnCode: 'C2' });
    });
    expect(state.getAttribute('data-save-state')).toBe('saved');
    expect(state.textContent).toMatch(/^⟦document\.saveState\.savedAt \(time=.+\)⟧$/);
  });

  it('аркуш не редагується — «Read-only»', () => {
    openDocument(1);

    expect(show(true).textContent).toBe('⟦document.saveState.readOnly⟧');
  });

  // ✎ UAT 08.11 (d6): «All changes saved» поруч із червоним рядком без жодного слова читалося як суперечність.
  // ⛔ Червоне без фіксу: рядок стану не читав `rowAttentionStore` — тексту про рядки немає.
  it('Water lock: поруч зі «Збережено» — «N rows need attention» за період сторінки', () => {
    openDocument(1);
    render(
      <MantineProvider theme={testTheme}>
        <DocumentSaveState readOnly={false} periodKey={Period} />
      </MantineProvider>,
    );
    const state = screen.getByTestId('document-save-state');
    expect(state.textContent).toBe('⟦document.saveState.allSaved⟧');

    const row = (rowKey: string, periodKey = Period) => ({
      key: `${String(Table)}:${rowKey}`,
      tableInstanceId: Table,
      periodKey,
      rowKey,
      rowLabel: rowKey,
      reason: 'why',
    });
    act(() =>
      publishTableAttention(Table, { tableInstanceId: Table, periodKey: Period, rows: [row('R1'), row('R2')], lockedMonths: null }),
    );
    expect(state.getAttribute('data-row-attention')).toBe('2');
    expect(state.getAttribute('data-save-state')).toBe('saved');
    expect(state.textContent).toContain('⟦document.saveState.allSaved⟧');
    expect(state.textContent).toContain('grid.rowAttention.count.other (count=2)');

    // Інший період — не рахується.
    act(() =>
      publishTableAttention(Table, { tableInstanceId: Table, periodKey: 202611, rows: [row('R1', 202611)], lockedMonths: null }),
    );
    expect(state.getAttribute('data-row-attention')).toBe('0');
    expect(state.textContent).toBe('⟦document.saveState.allSaved⟧');
  });
});

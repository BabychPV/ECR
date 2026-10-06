import { afterEach, describe, expect, it } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { DocumentSaveState } from '@/features/documents/DocumentSaveState';
import { discardPendingEdit, openDocument, putPendingEdit, resetPending } from '@/features/grid/pendingStore';
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
});

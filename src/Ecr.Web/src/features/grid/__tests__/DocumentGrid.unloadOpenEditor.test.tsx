import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { beginSignOut, resetSignOutForTests } from '@/api/client';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * F6-02: значення, набране у ВІДКРИТОМУ редакторі (без Enter), живе лише в його
 * полі — у сховищі правок його немає, і `registerUnloadFlush` про нього не знав.
 * Закриття вкладки чи F5 губили його мовчки. Тепер сітка просить рідне питання
 * браузера, коли редактор відкритий і людина в ньому щось увела.
 *
 * ⚠ Заглушка RevoGrid малює відкритий редактор тією ж розміткою, що й
 * `revogr-edit` (`.edit-input-wrapper`).
 */
vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: () => (
    <div data-testid="revogrid-stub">
      <div className="edit-input-wrapper">
        <input data-testid="cell-editor" />
      </div>
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

function slice(value: number, version: string): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns: [column()],
    rows: [
      {
        cells: { C1: value },
        isOrphaned: false,
        label: null,
        ordinal: 0,
        rowKey: 'r1',
        rowVersion: version,
        rowKind: 'Item' as const,
      },
    ],
  };
}

function show(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(() =>
      Promise.resolve(new Response(JSON.stringify(slice(1, 'v1')), { status: 200, headers: { 'Content-Type': 'application/json' } })),
    ),
  );

  render(
    <MantineProvider>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
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

function unload(): boolean {
  const event = new Event('beforeunload', { cancelable: true });
  window.dispatchEvent(event);

  return event.defaultPrevented;
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  resetSignOutForTests();
  vi.unstubAllGlobals();
});

describe('DocumentGrid: набране у відкритому редакторі при закритті вкладки (F6-02)', () => {
  it('у редакторі введено значення — браузер питає', async () => {
    show();
    const editor = await screen.findByTestId('cell-editor');
    fireEvent.focusIn(editor);
    fireEvent.input(editor, { target: { value: '125' } });

    // ⛔ Мутація: прибрати слухача `beforeunload` у `gridContainerRef` — тут false.
    expect(unload()).toBe(true);
  });

  it('редактор відкрито без введення — без питання', async () => {
    show();
    fireEvent.focusIn(await screen.findByTestId('cell-editor'));

    expect(unload()).toBe(false);
  });

  it('сеанс уже закрито (вихід) — без питання: зберегти нема чим', async () => {
    show();
    const editor = await screen.findByTestId('cell-editor');
    fireEvent.focusIn(editor);
    fireEvent.input(editor, { target: { value: '125' } });
    beginSignOut();

    expect(unload()).toBe(false);
  });
});

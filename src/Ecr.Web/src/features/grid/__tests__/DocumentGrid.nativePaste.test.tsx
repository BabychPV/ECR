import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * P2 (2026-10-01): вставка `12<TAB>8` в C3, коли сусідня C4 — read-only. Наш
 * `onPaste` відхиляв батч («Nothing from this paste was saved»), а нативна
 * вставка RevoGrid (слухач `paste` на `document`, `defaultPrevented` не
 * перевіряє) паралельно зберігала C3 через `afteredit` → `applyRangeEdit`.
 *
 * ⛔ Нативну вставку гасить `beforepasteapply`. НЕ `beforepaste`: обгортка
 * react-datagrid не підписується на подію, назва якої збігається з DOM-подією
 * `onbeforepaste` (`isCoveredByReact`), тож `onBeforepaste` мовчки не діє
 * (відтворено в браузері). Тест тримає саме ім'я пропа.
 */
const props: { current: Record<string, unknown> } = { current: {} };

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (p: Record<string, unknown>) => {
    props.current = p;

    return <div data-testid="revogrid-stub" />;
  },
}));

function column(code: string, ordinal: number): ColumnDto {
  return {
    code,
    dataType: 'Decimal',
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

const slice: TableSliceDto = {
  cellConfirmations: {},
  cellPermissions: {},
  periodKey: 202609,
  tableInstanceId: 1,
  columns: [column('C1', 0), column('C2', 1)],
  rows: [
    {
      cells: { C1: '1', C2: '2' },
      isOrphaned: false,
      label: null,
      ordinal: 0,
      rowKey: 'r1',
      rowVersion: 'v0',
      rowKind: 'Item' as const,
    },
  ],
};

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('DocumentGrid: нативна вставка RevoGrid погашена', () => {
  it('beforepasteapply скасовується, тож onPaste — єдиний шлях вставки', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify(slice), { status: 200, headers: { 'Content-Type': 'application/json' } }),
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

    const stub = await screen.findByTestId('revogrid-stub');

    // Подія, яку шле `revogr-clipboard`: bubbles + composed + cancelable.
    const event = new CustomEvent('beforepasteapply', { bubbles: true, composed: true, cancelable: true });
    fireEvent(stub, event);

    expect(event.defaultPrevented).toBe(true);
    // Мовчки-неробочий проп: обгортка його не підписує.
    expect(props.current.onBeforepaste).toBeUndefined();
  });
});

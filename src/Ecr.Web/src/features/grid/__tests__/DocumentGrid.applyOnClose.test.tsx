import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * T4-03 (P2): значення, набране в редакторі, мовчки зникало при кліку на
 * іншу комірку/кнопку (Enter/Tab фіксували). RevoGrid фіксує закриття
 * редактора кліком лише з `applyOnClose`; Esc при цьому скасовує
 * (`cancelChanges`) - це підтверджено живим прогоном у Chromium: з прапором
 * клік у комірку/поза сіткою дає `afteredit`, Esc - ні, Tab/Enter без змін.
 *
 * ⚠ Сама поведінка редактора - бібліотечна й у jsdom не монтується; тут
 * доводиться, що сітка вмикає прапор (мутація «прибрати applyOnClose» червоніє).
 */
let lastProps: Record<string, unknown> = {};

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: Record<string, unknown>) => {
    lastProps = props;

    return createElement('revo-grid', { 'data-testid': 'revogrid-stub' });
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
    scale: 2,
    unitId: null,
    unitSymbol: null,
  };
}

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns: [column('C1', 0)],
    rows: [
      { cells: { C1: '1' }, isOrphaned: false, label: null, ordinal: 0, rowKey: 'r1', rowVersion: 'v1', rowKind: 'Item' },
    ],
  };
}

beforeEach(() => {
  lastProps = {};
  vi.stubGlobal(
    'fetch',
    vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify(sliceFixture()), { status: 200, headers: { 'Content-Type': 'application/json' } }),
      ),
    ),
  );
});

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('закриття редактора кліком фіксує значення (T4-03)', () => {
  it('сітка вмикає applyOnClose', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <MantineProvider>
        <QueryClientProvider client={client}>
          <DocumentGrid
            documentId={1}
            tableInstanceId={1}
            tableDefId={9}
            periodKey={202609}
            readOnly={false}
            allowsDynamicRows={false}
            maxDynamicRows={null}
            navigateTo={null}
          />
        </QueryClientProvider>
      </MantineProvider>,
    );

    await waitFor(() => expect(lastProps['source']).toBeDefined());

    expect(lastProps['applyOnClose']).toBe(true);
  });
});

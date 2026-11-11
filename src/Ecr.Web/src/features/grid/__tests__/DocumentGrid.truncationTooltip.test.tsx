import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * Підказка обрізаного тексту ПІДКЛЮЧЕНА до справжнього `DocumentGrid` (корінь сітки — `gridContainerRef`):
 * обрізаний заголовок під фокусом дає `role=tooltip` з повним текстом, вузол прибирається з розмонтуванням.
 * RevoGrid замінено заглушкою з тією самою розміткою комірок (`.rgHeaderCell`, `.rgCell`).
 *
 * Мутація: прибрати `installTruncationTooltip(node)` з `gridContainerRef` — підказки немає.
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: () => (
    <div data-testid="revogrid-stub">
      <div className="rgHeaderCell" tabIndex={-1} data-testid="long-header">
        Description of equipment
      </div>
      <div className="rgCell" tabIndex={-1} data-testid="short-cell">
        1
      </div>
    </div>
  ),
}));

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns: [
      {
        code: 'C1',
        dataType: 'Text',
        defaultValue: null,
        displayFormat: null,
        header: 'Description of equipment',
        id: 1,
        isReadOnly: false,
        isRequired: false,
        isRequiredByMethodology: false,
        lookupRegistryDefId: null,
        ordinal: 0,
        unitId: null,
        unitSymbol: null,
      },
    ],
    rows: [{ cells: { C1: '1' }, isOrphaned: false, label: null, ordinal: 0, rowKey: 'r1', rowKind: 'Item', rowVersion: 'v1' }],
  } as unknown as TableSliceDto;
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('DocumentGrid · підказка обрізаного тексту підключена до кореня сітки', () => {
  it('фокус на обрізаному заголовку — role=tooltip з повним текстом; розмонтування прибирає вузол', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(JSON.stringify(sliceFixture()), { status: 200, headers: { 'Content-Type': 'application/json' } })),
    );
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const view = render(
      <MantineProvider>
        <QueryClientProvider client={client}>
          <DocumentGrid documentId={1} tableInstanceId={1} tableDefId={1} periodKey={202609} readOnly={false} allowsDynamicRows maxDynamicRows={null} />
        </QueryClientProvider>
      </MantineProvider>,
    );

    const header = await screen.findByTestId('long-header');
    Object.defineProperty(header, 'scrollWidth', { configurable: true, get: () => 240 });
    Object.defineProperty(header, 'clientWidth', { configurable: true, get: () => 60 });

    header.focus();

    const tooltip = await waitFor(() => {
      const node = document.querySelector<HTMLElement>('[data-truncation-tooltip]');
      expect(node?.hidden).toBe(false);
      return node;
    });
    expect(tooltip?.getAttribute('role')).toBe('tooltip');
    expect(tooltip?.textContent).toBe('Description of equipment');

    // Комірка, що вміщається, — підказку ховає і нової не дає.
    screen.getByTestId('short-cell').focus();
    expect(tooltip?.hidden).toBe(true);

    view.unmount();
    expect(document.querySelector('[data-truncation-tooltip]')).toBeNull();
  });
});

import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cancelAutosave, useDocumentPending } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';
import { RevoGridDefaults } from '../revoGridDefaults';

/**
 * ✎ Рішення людини 24.11 («прибери цей атрибут також»): позначка «RevoGrid» (посилання rv-grid.com/guide/attribution)
 * під сіткою документа прибирається штатною властивістю бібліотеки `hideAttribution` (MIT, @revolist/revogrid 4.11) —
 * не CSS. Спільні умовчання `RevoGridDefaults` несуть її для КОЖНОЇ сітки (документ і стенди).
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { hideAttribution?: boolean }) => (
    <div data-testid="revogrid-stub" data-hide-attribution={String(props.hideAttribution)} />
  ),
}));

const column = {
  dataType: 'Int', defaultValue: null, displayFormat: null, isReadOnly: false, isRequired: false,
  isRequiredByMethodology: false, lookupRegistryDefId: null, unitId: null, unitSymbol: null,
} as const;

const slice: TableSliceDto = {
  cellConfirmations: {},
  cellPermissions: {},
  periodKey: 202609,
  tableInstanceId: 1,
  columns: [{ ...column, code: 'C1', header: 'C1', id: 1, ordinal: 0, widthPx: null }],
  rows: [{ cells: { C1: 1 }, isOrphaned: false, label: null, ordinal: 0, rowKey: 'r1', rowKind: 'Item', rowVersion: 'v1' }],
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function Host(): JSX.Element {
  useDocumentPending(1);

  return (
    <DocumentGrid
      documentId={1}
      tableInstanceId={1}
      tableDefId={88}
      periodKey={202609}
      readOnly={false}
      allowsDynamicRows={false}
      maxDynamicRows={null}
    />
  );
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('позначка RevoGrid прибрана штатною властивістю', () => {
  it('спільні умовчання сітки — hideAttribution: true', () => {
    expect(RevoGridDefaults.hideAttribution).toBe(true);
  });

  it('кожен <RevoGrid> у коді (документ і стенди) розгортає RevoGridDefaults', () => {
    const sources = import.meta.glob<string>(['/src/**/*.tsx', '!/src/**/__tests__/**'], {
      query: '?raw',
      import: 'default',
      eager: true,
    });
    // Монтування — `<RevoGrid` з атрибутами (пробіл/перенос); згадки в коментарях — `<RevoGrid>`.
    const grids = Object.entries(sources).filter(([, text]) => /<RevoGrid\s/.test(text));

    expect(grids.length).toBeGreaterThanOrEqual(3);
    for (const [path, text] of grids) {
      const mounts = text.split(/<RevoGrid\s/).length - 1;
      const withDefaults = text.split(/<RevoGrid\s+\{\.\.\.RevoGridDefaults\}/).length - 1;
      expect(withDefaults, `${path}: <RevoGrid> без {...RevoGridDefaults}`).toBe(mounts);
    }
  });

  it('сітка документа передає hideAttribution у RevoGrid', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn((input: string) => Promise.resolve(json(String(input).includes('/preferences') ? [] : slice))),
    );
    render(
      <MantineProvider>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Host />
        </QueryClientProvider>
      </MantineProvider>,
    );

    expect((await screen.findByTestId('revogrid-stub')).getAttribute('data-hide-attribution')).toBe('true');
  });
});

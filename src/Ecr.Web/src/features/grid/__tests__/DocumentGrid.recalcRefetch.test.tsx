import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * D-1 / CL-01 (DoD): після правки комірки — нуль `GET` зрізу, поки задача
 * перерахунку йде, і рівно ОДИН, коли вона завершилась. Без нього обчислені
 * колонки лишалися старими до F9, хоча статус казав «Recalculated».
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: () => <div data-testid="revogrid-stub" />,
}));

vi.mock('../useCellPatch', async () => {
  const actual = await vi.importActual<typeof import('../useCellPatch')>('../useCellPatch');
  return {
    ...actual,
    useCellPatch: () => ({
      patch: vi.fn(),
      rowVersions: {},
      isPending: false,
      conflicts: [],
      status: 'saved',
      recalculationJobId: 'IFormulaRecalculationJob#42',
    }),
  };
});

function sliceFixture(total: number): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202601,
    tableInstanceId: 1,
    columns: [
      {
        code: 'C1', dataType: 'Decimal', defaultValue: null, displayFormat: null, header: 'К1',
        id: 1, isReadOnly: false, isRequired: false, isRequiredByMethodology: false,
        lookupRegistryDefId: null, ordinal: 0, unitId: null, unitSymbol: null,
      },
    ],
    rows: [
      {
        cells: { C1: total }, isOrphaned: false, label: null, ordinal: 0,
        rowKey: 'r1', rowKind: 'Item', rowVersion: 'v1',
      },
    ],
  };
}

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function stubFetch(jobState: string): { slices: () => number; jobs: () => number } {
  let slices = 0;
  let jobs = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/v1/jobs/')) {
        jobs += 1;
        return json({ state: jobState });
      }
      if (!url.includes('/tables/')) return json([]);
      slices += 1;
      return json(sliceFixture(slices));
    }),
  );
  return { slices: () => slices, jobs: () => jobs };
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentGrid
          documentId={1} tableInstanceId={1} tableDefId={1} periodKey={202601}
          readOnly={false} allowsDynamicRows={false} maxDynamicRows={null}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('DocumentGrid: зріз після перерахунку (D-1, CL-01)', () => {
  it('задача завершилась — рівно один додатковий GET зрізу', async () => {
    const calls = stubFetch('Succeeded');
    show();

    await waitFor(() => expect(calls.slices()).toBe(2));
    // Даємо часу на зайвий перезапит, якщо він є.
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(calls.slices()).toBe(2);
  });

  it('задача ще йде — зріз не перезапитується (опитування задачі було)', async () => {
    const calls = stubFetch('Running');
    show();

    await screen.findByTestId('revogrid-stub');
    await waitFor(() => expect(calls.jobs()).toBeGreaterThan(0));
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(calls.slices()).toBe(1);
  });
});

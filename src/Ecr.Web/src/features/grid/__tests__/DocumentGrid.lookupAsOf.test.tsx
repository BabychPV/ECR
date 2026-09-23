import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * `GET …/registries/{code}/entries` для Lookup-колонки сітки — `asOf` лише
 * для ТЕМПОРАЛЬНОГО довідника, і саме дата КІНЦЯ ПЕРІОДУ документа, а не
 * «сьогодні» (`GetRegistryEntriesHandler.cs`, той самий PR).
 *
 * ⛔ Дефект живого прогону: перевірка `asOf` на сервері була БЕЗУМОВНОЮ, а
 * жоден клієнтський Lookup-піцкер його не надсилав НІКОЛИ — тобто відмовляв
 * `422` для КОЖНОГО довідника, включно з нетемпоральним (наприклад,
 * Substance). `DocumentGrid.lookupError.test.tsx` (сусідній набір) цього не
 * ловив: реєстр там без `isTemporal` (`undefined`), і жодна перевірка не
 * дивилася на сам рядок запиту.
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: () => <div data-testid="revogrid-stub" />,
}));

/** Період — вересень 2026: кінець `2026-09-30`, останній день місяця. */
const PeriodKey = 202609;
const ExpectedPeriodEnd = '2026-09-30';

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: PeriodKey,
    tableInstanceId: 1,
    columns: [
      {
        code: 'SRC',
        dataType: 'Lookup',
        defaultValue: null,
        displayFormat: null,
        header: 'Джерело',
        id: 1,
        isReadOnly: false,
        isRequired: false,
        isRequiredByMethodology: false,
        lookupRegistryDefId: 42,
        ordinal: 0,
        unitId: null,
        unitSymbol: null,
      },
    ],
    rows: [
      {
        cells: { SRC: null },
        isOrphaned: false,
        label: null,
        ordinal: 0,
        rowKey: 'r1',
        rowKind: 'Item',
        rowVersion: 'v1',
      },
    ],
  };
}

/** Кожен GET `…/entries`: сирий рядок запиту (без `?`), чи `null` — немає. */
const entriesRequests: (string | null)[] = [];

function mockServer(isTemporal: boolean): void {
  entriesRequests.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const [path, query] = url.split('?');

      if (path?.endsWith('/api/v1/registries') === true) {
        return new Response(
          JSON.stringify([{ id: 42, code: 'SOURCES', nameL10n: { values: {} }, isTemporal }]),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (path?.includes('/api/v1/registries/') === true && path.endsWith('/entries')) {
        entriesRequests.push(query ?? null);
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify(sliceFixture()), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentGrid
          documentId={1}
          tableInstanceId={1}
          periodKey={PeriodKey}
          readOnly={false}
          allowsDynamicRows={false}
          maxDynamicRows={null}
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

describe('DocumentGrid: asOf у запиті записів довідника Lookup-колонки', () => {
  it('нетемпоральний довідник — запит БЕЗ query-параметра asOf', async () => {
    mockServer(false);
    show();

    await screen.findByTestId('revogrid-stub');

    await waitFor(() => expect(entriesRequests.length).toBeGreaterThan(0));

    expect(entriesRequests[0]).toBeNull();
  });

  it('темпоральний довідник — запит несе asOf кінця ПЕРІОДУ документа, не «сьогодні»', async () => {
    mockServer(true);
    show();

    await screen.findByTestId('revogrid-stub');

    await waitFor(() => expect(entriesRequests.length).toBeGreaterThan(0));

    // ⛔ Мутаційний доказ: прибери гейт `isTemporal` перед `asOf` у
    // `DocumentGrid.tsx` (лишити `null` завжди) — цей рядок почервоніє: у
    // темпорального довідника запит лишиться без параметра.
    //
    // ⚠ Саме КІНЕЦЬ періоду (`2026-09-30`), а не сьогоднішня системна дата
    // тесту: якби `DocumentGrid` рахував "сьогодні" замість дати періоду
    // документа, це порівняння впало б будь-якого дня, крім 30 вересня.
    expect(entriesRequests[0]).toBe(`asOf=${ExpectedPeriodEnd}`);
  });
});

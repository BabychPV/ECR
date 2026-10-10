import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid, forgetAsOfRequired } from '../DocumentGrid';

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

/**
 * Квартальний Q2 2026: ключ `202602`, але кінець — `2026-06-30` (D1-02).
 * Арифметика `YYYYMM` дала б `2026-02-28`.
 */
const QuarterKey = 202602;
const QuarterEnd = '2026-06-30';

function sliceFixture(periodKey: number = PeriodKey): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey,
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

function show(periodKey: number = PeriodKey, periodEnd: string | null = ExpectedPeriodEnd): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentGrid
          documentId={1}
          tableInstanceId={1}
          tableDefId={1}
          periodKey={periodKey}
          periodEnd={periodEnd}
          readOnly={false}
          allowsDynamicRows={false}
          maxDynamicRows={null}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  forgetAsOfRequired();
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

  it('квартальний документ — asOf це PeriodEnd із календаря, а не YYYYMM з periodKey (D1-02)', async () => {
    mockServer(true);
    show(QuarterKey, QuarterEnd);

    await screen.findByTestId('revogrid-stub');

    await waitFor(() => expect(entriesRequests.length).toBeGreaterThan(0));

    // ⛔ Мутаційний доказ: поверни виведення дати з `periodKey` — запит
    // піде з `asOf=2026-02-28`, і PATCH відхилить вибране на 2026-06-30.
    expect(entriesRequests[0]).toBe(`asOf=${QuarterEnd}`);
  });
});

/**
 * D1-06: довідник-ЧАСТИНА композиції з темпоральним батьком сам нетемпоральний, але сервер вимагає `asOf`
 * (`422 err.ECR-REQ-0422.asOfRequired`) — клієнт слав дату лише за `isTemporal`, і пікер лишався порожнім.
 */
describe('DocumentGrid: asOf для частини композиції з темпоральним батьком (D1-06)', () => {
  const requests: (string | null)[] = [];

  function mockCompositionServer(): void {
    requests.length = 0;
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        const [path, query] = url.split('?');

        if (path?.endsWith('/api/v1/registries') === true) {
          // `isTemporal: false` — як у переліку довідників: темпоральний лише батько.
          return new Response(JSON.stringify([{ id: 42, code: 'PARTS', nameL10n: { values: {} }, isTemporal: false }]), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          });
        }

        if (path?.includes('/api/v1/registries/') === true && path.endsWith('/entries')) {
          requests.push(query ?? null);
          if (query === undefined) {
            return new Response(
              JSON.stringify({
                title: 'asOf required',
                status: 422,
                errorCode: 'ECR-REQ-0422',
                correlationId: 'c1',
                messageKey: 'err.ECR-REQ-0422.asOfRequired',
                parameter: 'asOf',
              }),
              { status: 422, headers: { 'Content-Type': 'application/problem+json' } },
            );
          }

          return new Response(JSON.stringify([{ id: 1, code: 'P1', display: 'Part 1' }]), {
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

  it('422 asOfRequired для «нетемпорального» довідника → повтор із asOf кінця періоду, помилки немає', async () => {
    mockCompositionServer();
    show();

    await screen.findByTestId('revogrid-stub');

    // ⛔ Мутація: прибрати `isAsOfRequired`-гілку в `queryFn` — запит лишається без `asOf` (і єдиним).
    await waitFor(() => expect(requests).toContain(`asOf=${ExpectedPeriodEnd}`));
    expect(requests[0]).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('запам’ятовано: наступна сітка того ж довідника одразу питає з asOf (без другої відмови)', async () => {
    mockCompositionServer();
    show();
    await waitFor(() => expect(requests).toContain(`asOf=${ExpectedPeriodEnd}`));
    cleanup();

    requests.length = 0;
    show();

    await waitFor(() => expect(requests.length).toBeGreaterThan(0));
    expect(requests.every((query) => query === `asOf=${ExpectedPeriodEnd}`)).toBe(true);
  });
});

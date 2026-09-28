import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider, focusManager } from '@tanstack/react-query';
import type { ReactElement } from 'react';
import type { TableSliceDto } from '@/api/types';
import { queryKeys } from '@/api/queryKeys';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * Перф сітки: записи Lookup-довідників (`GET …/registries/{code}/entries`, до
 * 50 тис. записів) не перекачуються на кожне повернення у вкладку, а
 * `columns` сітки не перебудовуються на кожен рендер.
 *
 * ⚠ Дефолт застосунку (`app/queryClient.ts`) — `staleTime` 30 с і
 * фокус-рефетч увімкнено; клієнт тесту відтворює саме його, інакше доказ
 * міряв би не ту конфігурацію.
 */

/** Останні `columns`, які `DocumentGrid` віддав сітці, — по одному на рендер заглушки. */
const columnsSeen: unknown[] = [];

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { columns: unknown }) => {
    columnsSeen.push(props.columns);
    return <div data-testid="revogrid-stub" />;
  },
}));

const PeriodKey = 202609;

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

let entriesRequests = 0;

function mockServer(): void {
  entriesRequests = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.endsWith('/api/v1/registries')) {
        return new Response(
          JSON.stringify([{ id: 42, code: 'SOURCES', nameL10n: { values: {} }, isTemporal: false }]),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (path.includes('/api/v1/registries/') && path.endsWith('/entries')) {
        entriesRequests += 1;
        return new Response(
          JSON.stringify([
            { id: 7, code: 'A', display: 'Котельня', parentEntryId: null, validFrom: null, validTo: null },
          ]),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      return new Response(JSON.stringify(sliceFixture()), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

function appLikeClient(): QueryClient {
  return new QueryClient({
    defaultOptions: { queries: { retry: false, staleTime: 30_000, refetchOnWindowFocus: true } },
  });
}

/** Щоразу новий елемент — `rerender` проводить `DocumentGrid` через повний рендер. */
function grid(client: QueryClient): ReactElement {
  return (
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentGrid
          key="grid"
          documentId={1}
          tableInstanceId={1}
          tableDefId={1}
          periodKey={PeriodKey}
          readOnly={false}
          allowsDynamicRows={false}
          maxDynamicRows={null}
        />
      </QueryClientProvider>
    </MantineProvider>
  );
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  focusManager.setFocused(undefined);
  columnsSeen.length = 0;
});

describe('DocumentGrid: кеш записів Lookup-довідників', () => {
  it('повернення у вкладку після 30 с — жодного нового запиту entries', async () => {
    mockServer();
    const client = appLikeClient();
    render(grid(client));

    await screen.findByTestId('revogrid-stub');
    await waitFor(() => expect(entriesRequests).toBe(1));

    // Минуло більше, ніж дефолтний `staleTime` застосунку (30 с).
    const later = Date.now() + 31_000;
    vi.spyOn(Date, 'now').mockReturnValue(later);

    act(() => {
      focusManager.setFocused(false);
      focusManager.setFocused(true);
    });
    // Дати шанс рефетчу стартувати, якби він був.
    await new Promise((resolve) => setTimeout(resolve, 50));

    // ⛔ Мутаційний доказ: прибери `staleTime: Infinity` /
    // `refetchOnWindowFocus: false` із запитів entries у `DocumentGrid.tsx` —
    // тут стане 2.
    expect(entriesRequests).toBe(1);
  });

  it('інвалідація ключа довідника (мутація записів) — дані все ж перезапитуються', async () => {
    mockServer();
    const client = appLikeClient();
    render(grid(client));

    await screen.findByTestId('revogrid-stub');
    await waitFor(() => expect(entriesRequests).toBe(1));

    await act(async () => {
      await client.invalidateQueries({ queryKey: queryKeys.registries.entries('SOURCES') });
    });

    expect(entriesRequests).toBe(2);
  });

  it('повторний рендер без зміни даних — `columns` сітки той самий обʼєкт', async () => {
    mockServer();
    const client = appLikeClient();
    const view = render(grid(client));

    await screen.findByTestId('revogrid-stub');
    await waitFor(() => expect(entriesRequests).toBe(1));
    // Дочекатися, доки сітка отримає колонку з уже завантаженим довідником.
    await waitFor(() => expect(client.isFetching()).toBe(0));

    const before = columnsSeen.at(-1);
    const rendersBefore = columnsSeen.length;

    view.rerender(grid(client));

    expect(columnsSeen.length).toBeGreaterThan(rendersBefore);
    // ⛔ Мутаційний доказ: прибери `combine` із `useQueries` записів
    // довідників — масив результатів новий на кожен рендер, за ним нова
    // `Map` і нові `columns`.
    expect(columnsSeen.at(-1)).toBe(before);
  });
});

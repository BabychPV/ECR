import type { ReactNode } from 'react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import { h } from '@revolist/revogrid';
import type { ColumnDto, RegistryEntryDto, TableSliceDto } from '@/api/types';
import { gridColumns } from '../DocumentGrid';
import { NoLocalFlags } from '../cellState';
import { lookupCellDisplay } from '../LookupCellEditor';
import { unlistedEntryLabel, unlistedIdsOf, useUnlistedLookupLabels } from '../unlistedLookupEntries';

const apiFetch = vi.hoisted(() => vi.fn());
vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  apiFetch,
}));

/**
 * PS-P2 (D-PS-6): закритий запис довідника не в переліку на дату, а в комірці
 * лежить його id — показується назва з позначкою «закрито», не сирий id.
 */

function entry(id: number, display: string): RegistryEntryDto {
  return { id, code: `E${String(id)}`, display, parentEntryId: null, validFrom: null, validTo: null };
}

function detail(id: number, name: string, validTo: string | null) {
  return {
    id,
    code: `E${String(id)}`,
    displayL10n: { values: { en: name, ru: name, kz: name } },
    parentEntryId: null,
    validFrom: null,
    validTo,
    values: {},
  };
}

describe('unlistedIdsOf', () => {
  it('віддає лише числові id, яких немає в переліку; без дублів, за зростанням', () => {
    const entries = [entry(1, 'A'), entry(2, 'B')];

    expect(unlistedIdsOf([1, '2', '9', 9, 5, null, '', 'abc', 1.5, 0, -3], entries)).toEqual([5, 9]);
  });

  it('перелік ще їде (undefined) — нічого не добирається', () => {
    expect(unlistedIdsOf([9], undefined)).toEqual([]);
  });

  it('не більше 200 id на довідник', () => {
    const many = Array.from({ length: 500 }, (_, i) => i + 1);

    expect(unlistedIdsOf(many, [])).toHaveLength(200);
  });
});

describe('unlistedEntryLabel', () => {
  it('запис із кінцем чинності — назва з позначкою «закрито»', () => {
    expect(unlistedEntryLabel(detail(5, 'Старий', '2026-01-01'))).toBe('⟦grid.lookupClosedEntry (name=Старий)⟧');
  });

  it('без кінця чинності — просто назва', () => {
    expect(unlistedEntryLabel(detail(5, 'Майбутній', null))).toBe('Майбутній');
  });
});

describe('lookupCellDisplay з підписами закритих', () => {
  it('запис у переліку — назва; закритий — підпис; невідомий — сирий id', () => {
    const entries = [entry(1, 'Казахстан')];
    const unlisted = new Map([[5, 'Старий (закрито)']]);

    expect(lookupCellDisplay(1, entries, unlisted)).toBe('Казахстан');
    expect(lookupCellDisplay(5, entries, unlisted)).toBe('Старий (закрито)');
    expect(lookupCellDisplay(7, entries, unlisted)).toBe('7');
  });
});

describe('gridColumns — закритий запис у комірці', () => {
  const lookupColumn: ColumnDto = {
    id: 1,
    code: 'C1',
    header: 'Країна',
    dataType: 'Lookup',
    ordinal: 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    displayFormat: null,
    defaultValue: null,
    lookupRegistryDefId: 7,
    unitId: null,
    unitSymbol: null,
  };
  const slice: TableSliceDto = {
    tableInstanceId: 1,
    periodKey: 202609,
    columns: [lookupColumn],
    rows: [],
    cellPermissions: {},
    cellConfirmations: {},
  };

  it('cellTemplate показує підпис закритого запису, а перелік вибору його не містить', () => {
    const entries = [entry(1, 'Казахстан')];
    const columns = gridColumns(
      slice,
      false,
      NoLocalFlags,
      {},
      { blocked: new Map(), warning: new Map() },
      new Map(),
      new Map([[7, entries]]),
      new Map(),
      new Set(),
      null,
      null,
      new Set(),
      new Map([[7, new Map([[5, 'Старий (закрито)']])]]),
    );
    const template = columns.find((c) => c.prop === 'C1')?.cellTemplate as (
      hh: unknown,
      props: { value?: unknown },
    ) => unknown;

    expect(template(h, { value: 5 })).toBe('Старий (закрито)');
    expect(template(h, { value: 1 })).toBe('Казахстан');
    // Вибір: перелік `entries` не змінювався — закритого там немає.
    expect(entries.map((e) => e.id)).toEqual([1]);
  });
});

describe('useUnlistedLookupLabels', () => {
  beforeEach(() => apiFetch.mockReset());

  function wrapper({ children }: { children: ReactNode }) {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  }

  it('добирає закритий запис за id', async () => {
    apiFetch.mockResolvedValue(detail(5, 'Старий', '2026-01-01'));
    const requests = [{ registryId: 7, code: 'COUNTRY', ids: [5] }];

    const { result } = renderHook(() => useUnlistedLookupLabels(requests), { wrapper });

    await waitFor(() => expect(result.current.get(7)?.get(5)).toBe('⟦grid.lookupClosedEntry (name=Старий)⟧'));
    expect(apiFetch).toHaveBeenCalledWith('/api/v1/registries/COUNTRY/entries/5');
  });
});

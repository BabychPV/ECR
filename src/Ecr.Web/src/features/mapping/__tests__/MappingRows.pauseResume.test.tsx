import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { MappingPreview } from '@/api/types';
import { MappingRows } from '@/features/mapping/MappingRows';

/**
 * Кнопка паузи/відновлення на самому екрані перегляду мапінгу (`BE-27`),
 * а не лише в ізоляції `PauseResumeAction.test.tsx`: тут перевіряється, що
 * право доступу й вибір рядка стикуються з таблицею правильно.
 */
const Preview: MappingPreview = {
  sourceEntityId: 1,
  code: 'FLARE_01',
  displayName: 'Факел 01',
  fromUtc: '2026-09-01T00:00:00Z',
  toUtc: '2026-09-08T00:00:00Z',
  pointsSeen: 3,
  isTruncated: false,
  fields: [
    {
      fieldMapId: 1,
      sourceField: 'Flare_01_CO',
      outcome: 'Materialized',
      targetRowKey: 'Flare_01',
      targetColumnDefId: 100,
      targetColumnCode: 'CO_MASS',
      aggregation: 'Sum',
      sourceUnitCode: 'kg',
      targetUnitCode: 't',
      pointCount: 2,
      foldedValue: '42.5',
      isActive: true,
    },
    {
      fieldMapId: 2,
      sourceField: 'Flare_01_NOx',
      outcome: 'Materialized',
      targetRowKey: 'Flare_01',
      targetColumnDefId: 101,
      targetColumnCode: 'NOX_MASS',
      aggregation: 'Sum',
      sourceUnitCode: 'kg',
      targetUnitCode: 't',
      pointCount: 7,
      foldedValue: '9.5',
      isActive: false,
    },
  ],
  rows: [],
  unmappedSourceFields: [],
  uncoveredColumns: [],
};

function show(allowed: boolean): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <MappingRows preview={Preview} allowed={allowed} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  cleanup();
});

describe('MappingRows: право доступу перед кнопкою паузи/відновлення', () => {
  it('без права Integration.Manage кнопок немає взагалі', () => {
    show(false);

    expect(screen.queryByText('⟦mapping.pause⟧')).toBeNull();
    expect(screen.queryByText('⟦mapping.resume⟧')).toBeNull();
  });

  it('за замовчуванням (пропс не заданий) кнопок теж немає', () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <MantineProvider>
        <QueryClientProvider client={client}>
          <MappingRows preview={Preview} />
        </QueryClientProvider>
      </MantineProvider>,
    );

    expect(screen.queryByText('⟦mapping.pause⟧')).toBeNull();
    expect(screen.queryByText('⟦mapping.resume⟧')).toBeNull();
  });

  it('з правом — кожен рядок отримує СВОЮ дію: Pause на діючому, Resume на призупиненому', () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init?: RequestInit) => {
        expect(String(init?.method ?? 'GET')).toBe('POST');
        expect(String(url)).toBe('/api/v1/entity-field-maps/1/pause');

        return new Response(JSON.stringify({ id: 1, isActive: false }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }),
    );

    show(true);

    const activeRow = screen.getByText('Flare_01_CO').closest('tr')!;
    const pausedRow = screen.getByText('Flare_01_NOx').closest('tr')!;

    expect(within(activeRow).getByText('⟦mapping.pause⟧')).toBeDefined();
    expect(within(activeRow).queryByText('⟦mapping.resume⟧')).toBeNull();
    expect(within(pausedRow).getByText('⟦mapping.resume⟧')).toBeDefined();
    expect(within(pausedRow).queryByText('⟦mapping.pause⟧')).toBeNull();

    fireEvent.click(within(activeRow).getByText('⟦mapping.pause⟧'));

    return waitFor(() => expect(fetch).toHaveBeenCalledTimes(1));
  });
});

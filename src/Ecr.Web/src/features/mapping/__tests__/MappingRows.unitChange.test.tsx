import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { MappingPreview } from '@/api/types';
import { MappingRows } from '@/features/mapping/MappingRows';

/**
 * Рядок мапінгу зі зміною одиниці джерела (`ФВ-16.9`) на самому екрані
 * перегляду — а не лише в ізоляції `UnitChangeAction.test.tsx`: тут
 * перевіряється, який ІЗ ДВОХ рядків малює `MappingRows` для поля.
 */
function preview(pendingSourceUnitChange: MappingPreview['fields'][number]['pendingSourceUnitChange']): MappingPreview {
  return {
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
        foldedValue: null,
        isActive: false,
        pendingSourceUnitChange,
      },
    ],
    rows: [],
    unmappedSourceFields: [],
    uncoveredColumns: [],
  };
}

const Pending = {
  actualUnitCode: 'm3',
  actualUnitId: null,
  detectedAt: '2026-09-20T08:00:00Z',
} as const;

function show(data: MappingPreview, allowed: boolean): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <MemoryRouter>
        <QueryClientProvider client={client}>
          <MappingRows preview={data} allowed={allowed} />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  cleanup();
});

describe('MappingRows: рядок зі зміною одиниці джерела', () => {
  it('pendingSourceUnitChange не null — банер замість звичайного «paused», без PausedBadge для цього поля', () => {
    show(preview(Pending), true);

    expect(
      screen.getByText('⟦mapping.unitChangeBanner (actualUnitCode=m3, expectedUnitCode=t)⟧'),
    ).toBeDefined();

    // ⛔ Мутаційний доказ: якби рядок малювався ОБОМА шляхами одразу
    // (банер + звичайний paused), цей тест — єдиний, хто про це скаже.
    expect(screen.queryByText('⟦mapping.paused⟧')).toBeNull();

    const row = screen.getByText('Flare_01_CO').closest('tr')!;
    expect(row.getAttribute('data-mapping-state')).toBe('pending-unit-change');
  });

  it('Дзеркало: pendingSourceUnitChange null — звичайний рядок Pause/Resume, без банера', () => {
    show(preview(null), true);

    expect(screen.queryByText(/mapping\.unitChangeBanner/)).toBeNull();
    expect(screen.getByText('⟦mapping.paused⟧')).toBeDefined();

    const row = screen.getByText('Flare_01_CO').closest('tr')!;
    expect(row.getAttribute('data-mapping-state')).toBe('paused');
  });

  it('без права Integration.Manage — банер видно, кнопок немає', () => {
    show(preview(Pending), false);

    expect(
      screen.getByText('⟦mapping.unitChangeBanner (actualUnitCode=m3, expectedUnitCode=t)⟧'),
    ).toBeDefined();
    expect(screen.queryByText('⟦mapping.unitChangeDecline⟧')).toBeNull();
  });

  it('клік «Ні, це помилка джерела» ховає банер і показує звичайний paused-рядок — без виклику сервера', () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => {
        throw new Error('мережа не мала б бути викликана — «Ні» не ходить на сервер');
      }),
    );

    show(preview(Pending), true);

    fireEvent.click(screen.getByText('⟦mapping.unitChangeDecline⟧'));

    expect(screen.queryByText(/mapping\.unitChangeBanner/)).toBeNull();
    expect(screen.getByText('⟦mapping.paused⟧')).toBeDefined();

    const row = screen.getByText('Flare_01_CO').closest('tr')!;
    expect(row.getAttribute('data-mapping-state')).toBe('paused');
    expect(within(row).getByText('⟦mapping.resume⟧')).toBeDefined();
  });
});

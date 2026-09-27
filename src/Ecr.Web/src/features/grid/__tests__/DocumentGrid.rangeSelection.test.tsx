import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * `ФВ-3.3`, частина «виділення діапазону»: сітка документа ВМИКАЄ діапазонне
 * виділення RevoGrid.
 *
 * ⚠ Що саме тут доведено і чого ні. Виділення мишею/Shift+стрілками малює сама
 * бібліотека (`DocumentGrid.tsx`, коментар над компонентом: «пункти 1, 2, 5 і
 * 7 забезпечує RevoGrid»), але лише за пропа `range`: без нього RevoGrid
 * виділяє рівно одну комірку, і Ctrl+C/Ctrl+V діапазоном втрачають сенс.
 * Те, що наш код ЧИТАЄ виділений діапазон (`setrange` → Ctrl+C віддає саме
 * його), доводить `DocumentGrid.pasteAnchor.test.tsx`; тут — що діапазон
 * узагалі можна виділити.
 *
 * ⚠ RevoGrid підмінено заглушкою, яка запам'ятовує пропи: справжній
 * веб-компонент у jsdom не рендериться, а перевіряється рівно та межа, яку
 * контролює наш код, — що саме він передає бібліотеці.
 */
const captured = vi.hoisted(() => ({ props: null as Record<string, unknown> | null }));

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: Record<string, unknown>) => {
    captured.props = props;

    return <div data-testid="revogrid-stub" />;
  },
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
        dataType: 'Int',
        defaultValue: null,
        displayFormat: null,
        header: 'C1',
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
    rows: [
      { cells: { C1: 1 }, isOrphaned: false, label: null, ordinal: 0, rowKey: 'r1', rowKind: 'Item', rowVersion: 'v1' },
    ],
  };
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  captured.props = null;
  vi.unstubAllGlobals();
});

describe('DocumentGrid: діапазонне виділення', () => {
  it('ФВ-3.3: сітка вмикає виділення діапазону в RevoGrid (проп range)', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        Promise.resolve(
          new Response(JSON.stringify(sliceFixture()), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
        ),
      ),
    );

    render(
      <MantineProvider>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <DocumentGrid
            documentId={1}
            tableInstanceId={1}
            tableDefId={1}
            periodKey={202609}
            readOnly={false}
            allowsDynamicRows={false}
            maxDynamicRows={null}
          />
        </QueryClientProvider>
      </MantineProvider>,
    );

    await screen.findByTestId('revogrid-stub');

    // ⛔ Саме `true`, а не «щось правдиве»: RevoGrid читає проп як boolean, і
    // рядок `'false'` увімкнув би виділення так само, як `true`.
    expect(captured.props?.['range']).toBe(true);
  });
});

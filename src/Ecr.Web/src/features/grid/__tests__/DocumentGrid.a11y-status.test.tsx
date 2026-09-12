import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import type { SaveStatus } from '../useCellPatch';
import { DocumentGrid } from '../DocumentGrid';

/**
 * Q-264 (High): індикатор автозбереження grid — ЄДИНИЙ статусний елемент
 * застосунку, що змінював текст без `aria-live`/`role`, на відміну від
 * `RouteAnnouncer` (`role="status" aria-live="polite"`), `AsyncBoundary`
 * (`role="status" aria-busy="true"`) чи Mantine `Alert` (`role="alert"`
 * вбудовано).
 *
 * ⛔ Grid — клавіатурна, Excel-подібна навігація: оператор одразу тікає з
 * клітинки табуляцією, не дивлячись на панель інструментів. Без `aria-live`
 * читалка НІКОЛИ не озвучує ні «зберігається», ні «збережено», ні (найгірше)
 * «не вдалося зберегти» — відмова читається як тиша, а дані комплаєнс-звіту
 * тихо губляться з точки зору користувача, що покладається на читалку.
 *
 * RevoGrid підмінено заглушкою: сама сітка (клавіатурна навігація, вставка,
 * undo) уже вкрита `clipboard.test.ts`/`undo.test.ts`/`permissions.test.ts` —
 * тут перевіряється ЛИШЕ доступність індикатора статусу в панелі
 * інструментів, і реальний веб-компонент RevoGrid у jsdom тільки заважав би.
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: () => <div data-testid="revogrid-stub" />,
}));

let mockStatus: SaveStatus = 'idle';

vi.mock('../useCellPatch', async () => {
  const actual = await vi.importActual<typeof import('../useCellPatch')>('../useCellPatch');
  return {
    ...actual,
    useCellPatch: () => ({
      patch: vi.fn(),
      rowVersions: {},
      isPending: false,
      conflicts: [],
      status: mockStatus,
    }),
  };
});

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202601,
    tableInstanceId: 1,
    columns: [
      {
        code: 'C1',
        dataType: 'Decimal',
        defaultValue: null,
        displayFormat: null,
        header: 'Колонка 1',
        id: 1,
        isReadOnly: false,
        isRequired: false,
        lookupRegistryDefId: null,
        ordinal: 0,
        unitId: null,
        unitSymbol: null,
      },
    ],
    rows: [
      {
        cells: { C1: 1 },
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

function mockSliceFetch(): void {
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
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentGrid
          documentId={1}
          tableInstanceId={1}
          periodKey={202601}
          readOnly={false}
          allowsDynamicRows={false}
          maxDynamicRows={null}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  mockStatus = 'idle';
});

describe('DocumentGrid: доступність індикатора автозбереження (Q-264)', () => {
  it('"зберігається" — role="status" + aria-live="polite"', async () => {
    mockStatus = 'saving';
    mockSliceFetch();
    show();

    // ⚠ Чекаємо, поки зникне лоадер `AsyncBoundary` (той теж малює
    // `role="status"` для стану завантаження, `#146` у AsyncBoundary.tsx) —
    // інакше `findByRole('status')` зловить його, а не наш індикатор.
    await screen.findByTestId('revogrid-stub');

    const indicator = screen.getByRole('status');
    expect(indicator.getAttribute('data-save-status')).toBe('saving');
    expect(indicator.getAttribute('aria-live')).toBe('polite');
  });

  it('"збережено" — role="status" + aria-live="polite"', async () => {
    mockStatus = 'saved';
    mockSliceFetch();
    show();

    await screen.findByTestId('revogrid-stub');

    const indicator = screen.getByRole('status');
    expect(indicator.getAttribute('data-save-status')).toBe('saved');
    expect(indicator.getAttribute('aria-live')).toBe('polite');
  });

  it('помилка збереження — role="alert" (читалка перериває читання, як і будь-яка інша помилка застосунку)', async () => {
    mockStatus = 'error';
    mockSliceFetch();
    show();

    await screen.findByTestId('revogrid-stub');

    const indicator = screen.getByRole('alert');
    expect(indicator.getAttribute('data-save-status')).toBe('error');
  });

  it('idle — індикатора взагалі немає (порожнє місце не привертає уваги)', async () => {
    mockStatus = 'idle';
    mockSliceFetch();
    show();

    await waitFor(() => {
      expect(screen.getByTestId('revogrid-stub')).toBeTruthy();
    });

    expect(screen.queryByRole('status')).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});

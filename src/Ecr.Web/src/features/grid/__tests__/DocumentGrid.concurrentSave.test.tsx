import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnRegular } from '@revolist/revogrid';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { DocumentGrid } from '../DocumentGrid';

/**
 * Аудит 2026-09-16, §10.2 (High, тиха втрата даних): успішне збереження
 * очищало ВСІ незбережені правки, а не лише щойно збережені.
 *
 * ⛔ `setPending(new Map())` / `setOverrides(new Map())` не дивилися на
 * `touchedRowKeys` — на відміну від `requiredInputBlocked`/`saveErrorCells`
 * кількома рядками нижче, які фільтрують саме за ними. А `save()` кличуть
 * чотири НЕЗАЛЕЖНІ, несеріалізовані шляхи (автозбереження, Ctrl+S, вставка,
 * undo/redo), і ніщо не забороняє двом запитам бути в дорозі одночасно.
 *
 * **Сценарій:** правка A в дорозі (повільний PATCH) → правка B зберігається
 * швидко і коректно чистить `pending` → правка C потрапляє в `pending` і ще
 * НЕ надіслана → повільний A нарешті завершується і безумовно чистить
 * `pending`, відкидаючи C. Той самий resolve запускає `invalidateQueries`,
 * тож `rows` перерахунковується з `data` — і введене значення зникає з екрана
 * без помилки, без позначки «незбережено», без відновлення.
 *
 * ⚠ Спостережуване — саме позначка «незбережено» (`data-cell-state="dirty"`,
 * `ФВ-14.18`), і читається вона через СПРАВЖНІЙ `cellProperties` колонки —
 * той самий колбек, який у застосунку викликає RevoGrid. Не через кнопку
 * «Зберегти»: Mantine вимикає її ще й на `loading`, тож у момент, коли в
 * дорозі є хоч один запит, її стан не відрізняє «нічого не збережено» від
 * «зберігається».
 */
type CellProps = { class?: string; 'data-cell-state'?: string };

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: {
    columns?: ColumnRegular[];
    onAfteredit?: (event: { detail: unknown }) => void;
  }) => (
    <div data-testid="revogrid-stub">
      {['r1', 'r2', 'r3'].map((rowKey) => (
        <button
          key={rowKey}
          type="button"
          onClick={() =>
            props.onAfteredit?.({
              detail: { prop: 'C1', model: { __rowKey: rowKey }, val: '5' },
            })
          }
        >
          {`edit-${rowKey}`}
        </button>
      ))}

      {/* Дзеркало станів комірок: рівно те, що грід намалював би сам. */}
      {['r1', 'r2', 'r3'].map((rowKey) => {
        const column = (props.columns ?? []).find((candidate) => candidate.prop === 'C1');
        const cellProperties = column?.cellProperties as
          | ((args: { model: unknown }) => CellProps)
          | undefined;
        const properties = cellProperties?.({ model: { __rowKey: rowKey } }) ?? {};

        return (
          <span key={`state-${rowKey}`} data-testid={`state-${rowKey}`}>
            {properties['data-cell-state'] ?? 'none'}
          </span>
        );
      })}
    </div>
  ),
}));

function column(code: string): ColumnDto {
  return {
    code,
    dataType: 'Decimal',
    defaultValue: null,
    displayFormat: null,
    header: code,
    id: 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    lookupRegistryDefId: null,
    ordinal: 0,
    unitId: null,
    unitSymbol: null,
  };
}

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns: [column('C1')],
    rows: ['r1', 'r2', 'r3'].map((rowKey, index) => ({
      cells: { C1: index },
      isOrphaned: false,
      label: null,
      ordinal: index,
      rowKey,
      rowVersion: `v${index}`,
      rowKind: 'Item' as const,
    })),
  };
}

/**
 * Кожен `PATCH` лишається в дорозі, доки тест сам його не завершить.
 *
 * ⛔ Саме так відтворюється сценарій аудиту: два незалежні збереження в дорозі
 * ОДНОЧАСНО, і порядок їхнього завершення задає тест, а не випадок.
 */
interface InFlightPatch {
  rowKeys: string[];
  settle: () => void;
}

const inFlight: InFlightPatch[] = [];

function mockServer(): void {
  inFlight.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn((_path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        const body = JSON.parse(String(init.body)) as { rows: { rowKey: string }[] };

        return new Promise<Response>((resolve) => {
          inFlight.push({
            rowKeys: body.rows.map((row) => row.rowKey),
            settle: () =>
              resolve(
                new Response(
                  JSON.stringify({ appliedCells: 1, rowVersions: {}, validation: [] }),
                  { status: 200, headers: { 'Content-Type': 'application/json' } },
                ),
              ),
          });
        });
      }

      return Promise.resolve(
        new Response(JSON.stringify(sliceFixture()), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      );
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
          periodKey={202609}
          readOnly={false}
          allowsDynamicRows={false}
          maxDynamicRows={null}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Завершує запит із указаним номером і дає React домалювати наслідки. */
async function settle(index: number): Promise<void> {
  const patch = inFlight[index];
  expect(patch, `запит №${index} має бути в дорозі`).toBeDefined();

  await act(async () => {
    patch?.settle();
    await Promise.resolve();
  });
}

function stateOf(rowKey: string): string {
  return screen.getByTestId(`state-${rowKey}`).textContent ?? '';
}

async function clickSave(): Promise<void> {
  await act(async () => {
    fireEvent.click(screen.getByRole('button', { name: /grid\.save/ }));
    await Promise.resolve();
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentGrid: успіх одного збереження не викидає правки інших рядків (§10.2)', () => {
  it('правка, введена доки повільний запит у дорозі, лишається незбереженою, а не зникає', async () => {
    mockServer();
    show();

    await screen.findByTestId('revogrid-stub');

    // Правка A: рядок r1, збереження ПОВІЛЬНЕ — лишається в дорозі.
    fireEvent.click(screen.getByRole('button', { name: 'edit-r1' }));
    await clickSave();
    await waitFor(() => expect(inFlight).toHaveLength(1));
    expect(inFlight[0]?.rowKeys).toEqual(['r1']);

    // Правка B: рядок r2, окреме збереження — теж у дорозі, завершиться ПЕРШИМ.
    fireEvent.click(screen.getByRole('button', { name: 'edit-r2' }));
    await clickSave();
    await waitFor(() => expect(inFlight).toHaveLength(2));

    // B завершується: обидва рядки цього патчу коректно перестають бути dirty.
    await settle(1);
    await waitFor(() => expect(stateOf('r2')).toBe('none'));

    // Правка C: рядок r3 — у `pending`, НЕ надіслана.
    fireEvent.click(screen.getByRole('button', { name: 'edit-r3' }));
    await waitFor(() => expect(stateOf('r3')).toBe('dirty'));

    // І лише тепер завершується повільний A — патч, що стосувався ЛИШЕ r1.
    await settle(0);

    // ⛔ Мутаційний доказ (RED до фіксу): `setPending(new Map())` у обробнику
    // успіху A викидав і C — позначка «незбережено» на r3 гасла, значення
    // зникало з екрана (той самий resolve інвалідує зріз), і жодної помилки
    // оператор не бачив.
    await waitFor(() => expect(stateOf('r3')).toBe('dirty'));

    // ⚠ І воно справді ще надсилається: наступне збереження несе саме r3.
    await clickSave();
    await waitFor(() => expect(inFlight).toHaveLength(3));
    expect(inFlight[2]?.rowKeys).toEqual(['r3']);
  });

  it('успіх патчу знімає позначку саме зі своїх рядків', async () => {
    mockServer();
    show();

    await screen.findByTestId('revogrid-stub');

    fireEvent.click(screen.getByRole('button', { name: 'edit-r1' }));
    fireEvent.click(screen.getByRole('button', { name: 'edit-r2' }));
    await waitFor(() => expect(stateOf('r1')).toBe('dirty'));
    expect(stateOf('r2')).toBe('dirty');

    await clickSave();
    await waitFor(() => expect(inFlight).toHaveLength(1));
    expect(inFlight[0]?.rowKeys).toEqual(['r1', 'r2']);

    await settle(0);

    // ⚠ Зворотний бік того самого фільтра: рядки ЦЬОГО патчу мусять
    // очиститися, інакше фікс лише перетворив би одну неправду на іншу —
    // «незбережено» на тому, що вже збережено.
    await waitFor(() => expect(stateOf('r1')).toBe('none'));
    expect(stateOf('r2')).toBe('none');
  });
});

import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { DocumentGrid } from '../DocumentGrid';

/**
 * Аудит 2026-09-16, §10.1: вставка завжди прив'язувалась до (0,0), а
 * копіювання завжди брало всю таблицю.
 *
 * ⛔ `onPaste` передавав у `planPaste` жорстко закодований якір
 * `{ rowIndex: 0, columnIndex: 0 }` — не поточне виділення RevoGrid (у всьому
 * `features/grid/**` не було ні `ref`, ні обробника фокуса). Оператор клацав
 * рядок 3, колонку `C2`, вставляв блок з Excel — і значення лягали з рядка 1,
 * колонки 1, ТИХО перезаписуючи, найімовірніше непов'язані, уже коректні
 * дані. Симетрично `onCopy` серіалізував `data.rows.map(...)` — усю таблицю,
 * незалежно від виділення: Ctrl+C після виділення двох комірок віддавав у
 * буфер тисячі.
 *
 * ⚠ RevoGrid підмінено — як і в `DocumentGrid.saveError.test.tsx` (справжній
 * веб-компонент у jsdom лише заважає). Але заглушка не «імітує» фокус
 * пропом: вона **дispatch-ить справжню `CustomEvent('focuscell')`**
 * (`bubbles: true`, `composed: true` — рівно так їх оголошує
 * `revogr-overlay-selection`), тож перевіряється той самий шлях, яким подія
 * приходить від бібліотеки в реальному застосунку.
 */
vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { onAfteredit?: (event: { detail: unknown }) => void }) => (
    <div data-testid="revogrid-stub">
      <button
        type="button"
        onClick={(event) =>
          // Клік по комірці (рядок 2, колонка 1 — нумерація з нуля) — саме те,
          // що RevoGrid повідомляє через `focuscell`.
          event.currentTarget.dispatchEvent(
            new CustomEvent('focuscell', {
              bubbles: true,
              composed: true,
              detail: {
                rowType: 'rgRow',
                colType: 'rgCol',
                focus: { x: 1, y: 2 },
                end: { x: 1, y: 2 },
                range: { x: 1, y: 2, x1: 1, y1: 2 },
              },
            }),
          )
        }
      >
        focus-r3-c2
      </button>
      <button
        type="button"
        onClick={(event) =>
          // Виділення діапазону мишею: C2..C3 × рядки 2..3.
          event.currentTarget.dispatchEvent(
            new CustomEvent('setrange', {
              bubbles: true,
              composed: true,
              detail: { type: 'rgRow', x: 1, y: 1, x1: 2, y1: 2 },
            }),
          )
        }
      >
        select-range
      </button>
      <button
        type="button"
        onClick={() => props.onAfteredit?.({ detail: { prop: 'C1', model: { __rowKey: 'r1' }, val: '1' } })}
      >
        simulate-edit
      </button>
    </div>
  ),
}));

function column(code: string, ordinal: number): ColumnDto {
  return {
    code,
    dataType: 'Decimal',
    defaultValue: null,
    displayFormat: null,
    header: code,
    id: ordinal + 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    lookupRegistryDefId: null,
    ordinal,
    unitId: null,
    unitSymbol: null,
  };
}

/** Зріз 4×3: достатньо, щоб якір (0,0) і якір (3,C2) не збігалися. */
function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns: [column('C1', 0), column('C2', 1), column('C3', 2)],
    rows: ['r1', 'r2', 'r3', 'r4'].map((rowKey, index) => ({
      cells: { C1: `${rowKey}-1`, C2: `${rowKey}-2`, C3: `${rowKey}-3` },
      isOrphaned: false,
      label: null,
      ordinal: index,
      rowKey,
      rowVersion: `v${index}`,
      rowKind: 'Item' as const,
    })),
  };
}

/** Тіла надісланих `PATCH`. */
const patched: { rowKey: string; cells: { columnCode: string; value: unknown }[] }[] = [];

function mockServer(): void {
  patched.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        const body = JSON.parse(String(init.body)) as {
          rows: { rowKey: string; cells: { columnCode: string; value: unknown }[] }[];
        };
        patched.push(...body.rows);

        return new Response(JSON.stringify({ appliedCells: 1, rowVersions: {}, validation: [] }), {
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
          periodKey={202609}
          readOnly={false}
          allowsDynamicRows={false}
          maxDynamicRows={null}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Буфер обміну, який бачить обробник: `getData` для вставки, `setData` — для копії. */
function clipboard(text: string): { clipboardData: { getData: () => string; setData: ReturnType<typeof vi.fn> } } {
  return { clipboardData: { getData: () => text, setData: vi.fn() } };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentGrid: вставка йде у ВИДІЛЕНУ комірку (§10.1)', () => {
  it('вставка після кліку в рядок 3 / колонку C2 не торкається рядка 1', async () => {
    mockServer();
    show();

    const stub = await screen.findByTestId('revogrid-stub');
    fireEvent.click(screen.getByRole('button', { name: 'focus-r3-c2' }));

    // Один рядок × дві колонки з Excel.
    fireEvent.paste(stub, clipboard('11\t12\n'));

    await waitFor(() => expect(patched).toHaveLength(1));

    // ⛔ Мутаційний доказ (RED до фіксу): якір був жорстко (0,0), тож
    // надсилався рядок `r1`, колонки `C1`/`C2` — тобто ЧУЖІ, уже заповнені
    // комірки перезаписувалися мовчки.
    expect(patched[0]?.rowKey).toBe('r3');
    expect(patched[0]?.cells.map((cell) => cell.columnCode)).toEqual(['C2', 'C3']);
    expect(patched[0]?.cells.map((cell) => cell.value)).toEqual([11, 12]);
  });

  it('без жодного виділення вставка лишається від (0,0) — попередня поведінка', async () => {
    mockServer();
    show();

    const stub = await screen.findByTestId('revogrid-stub');
    fireEvent.paste(stub, clipboard('7\n'));

    await waitFor(() => expect(patched).toHaveLength(1));

    // ⚠ Свідомо: доки оператор нічого не обрав, кут таблиці — єдиний якір,
    // який можна назвати, і Ctrl+V не має падати в нікуди.
    expect(patched[0]?.rowKey).toBe('r1');
    expect(patched[0]?.cells[0]?.columnCode).toBe('C1');
  });

  it('вставка, що не вміщається в сітку, обрізається по межі, а не зсувається', async () => {
    mockServer();
    show();

    const stub = await screen.findByTestId('revogrid-stub');
    fireEvent.click(screen.getByRole('button', { name: 'focus-r3-c2' }));

    // Три колонки від `C2` — у сітці лишилося дві.
    fireEvent.paste(stub, clipboard('21\t22\t23\n'));

    await waitFor(() => expect(patched).toHaveLength(1));

    expect(patched[0]?.cells.map((cell) => cell.columnCode)).toEqual(['C2', 'C3']);
  });
});

describe('DocumentGrid: Ctrl+C віддає ВИДІЛЕНЕ (§10.1)', () => {
  it('копіювання після виділення діапазону віддає лише його', async () => {
    mockServer();
    show();

    const stub = await screen.findByTestId('revogrid-stub');
    fireEvent.click(screen.getByRole('button', { name: 'select-range' }));

    const event = clipboard('');
    fireEvent.copy(stub, event);

    // ⛔ Мутаційний доказ (RED до фіксу): у буфер ішли ВСІ 4 рядки × 3
    // колонки, скільки б комірок оператор не виділив.
    expect(event.clipboardData.setData).toHaveBeenCalledWith('text/plain', 'r2-2\tr2-3\nr3-2\tr3-3\n');
  });

  it('копіювання після кліку в одну комірку віддає саме її', async () => {
    mockServer();
    show();

    const stub = await screen.findByTestId('revogrid-stub');
    fireEvent.click(screen.getByRole('button', { name: 'focus-r3-c2' }));

    const event = clipboard('');
    fireEvent.copy(stub, event);

    expect(event.clipboardData.setData).toHaveBeenCalledWith('text/plain', 'r3-2\n');
  });

  it('без виділення копіюється вся таблиця — попередня поведінка', async () => {
    mockServer();
    show();

    const stub = await screen.findByTestId('revogrid-stub');

    const event = clipboard('');
    fireEvent.copy(stub, event);

    // ⚠ Свідома деградація: порожній буфер на Ctrl+C виглядав би як
    // несправність, а «нічого не виділено» в сітці означає «уся таблиця» і в
    // Excel теж (Ctrl+A перед Ctrl+C).
    expect(event.clipboardData.setData).toHaveBeenCalledWith(
      'text/plain',
      'r1-1\tr1-2\tr1-3\nr2-1\tr2-2\tr2-3\nr3-1\tr3-2\tr3-3\nr4-1\tr4-2\tr4-3\n',
    );
  });
});

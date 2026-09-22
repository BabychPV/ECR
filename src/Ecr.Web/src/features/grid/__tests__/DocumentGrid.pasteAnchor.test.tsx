import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
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

      {/*
       * ⚠ Кнопки нижче — для сценаріїв ІЗ колонкою підписів рядків
       * (`hasRowLabels` істинна): вона вставляється ПЕРШОЮ в `gridColumns`,
       * тож координата `x`, яку справді шле RevoGrid, зсунута на одиницю
       * праворуч відносно того самого стовпця в `data.columns`. Саме тому тут
       * `x=2`/`x1=3` там, де в кнопках вище (без підписів) було б `x=1`/`x1=2`.
       */}
      <button
        type="button"
        onClick={(event) =>
          // Клік по візуальній колонці C2 (грід-індекс 2 — підпис займає 0),
          // рядок 3 (індекс 2).
          event.currentTarget.dispatchEvent(
            new CustomEvent('focuscell', {
              bubbles: true,
              composed: true,
              detail: {
                rowType: 'rgRow',
                colType: 'rgCol',
                focus: { x: 2, y: 2 },
                end: { x: 2, y: 2 },
                range: { x: 2, y: 2, x1: 2, y1: 2 },
              },
            }),
          )
        }
      >
        focus-r3-c2-labeled
      </button>
      <button
        type="button"
        onClick={(event) =>
          // Виділення діапазону C2..C3 (грід-індекси 2..3) × рядки 2..3.
          event.currentTarget.dispatchEvent(
            new CustomEvent('setrange', {
              bubbles: true,
              composed: true,
              detail: { type: 'rgRow', x: 2, y: 1, x1: 3, y1: 2 },
            }),
          )
        }
      >
        select-range-labeled
      </button>
      <button
        type="button"
        onClick={(event) =>
          // Клік по самій колонці підпису (грід-індекс 0), рядок 3.
          event.currentTarget.dispatchEvent(
            new CustomEvent('focuscell', {
              bubbles: true,
              composed: true,
              detail: {
                rowType: 'rgRow',
                colType: 'rgCol',
                focus: { x: 0, y: 2 },
                end: { x: 0, y: 2 },
                range: { x: 0, y: 2, x1: 0, y1: 2 },
              },
            }),
          )
        }
      >
        focus-r3-label
      </button>
      <button
        type="button"
        onClick={(event) =>
          // Виділення, що зачіпає колонку підпису: грід-індекси 0..1 (підпис,
          // C1) × рядки 2..3.
          event.currentTarget.dispatchEvent(
            new CustomEvent('setrange', {
              bubbles: true,
              composed: true,
              detail: { type: 'rgRow', x: 0, y: 1, x1: 1, y1: 2 },
            }),
          )
        }
      >
        select-range-label-included
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

/**
 * Той самий зріз 4×3, але з підписом хоча б в одного рядка — тобто
 * `hasRowLabels` істинна і `gridColumns` вставляє `RowLabelProp` першою
 * колонкою (`DocumentGrid.tsx`), зсуваючи `C1`/`C2`/`C3` на одну позицію
 * праворуч у координатах, які шле RevoGrid.
 */
function sliceFixtureWithLabels(): TableSliceDto {
  const base = sliceFixture();

  return {
    ...base,
    rows: base.rows.map((row) => (row.rowKey === 'r1' ? { ...row, label: 'Мазут' } : row)),
  };
}

/** Тіла надісланих `PATCH`. */
const patched: { rowKey: string; cells: { columnCode: string; value: unknown }[] }[] = [];

function mockServer(slice: () => TableSliceDto = sliceFixture): void {
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

      return new Response(JSON.stringify(slice()), {
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
  // ⛔ `D14-12`: сховище правок модульне — воно переживає кінець тесту, як і
  // запланований дебаунсом пакет. Без скидання правка одного сценарію
  // потрапила б у `PATCH` наступного.
  cancelAutosave();
  resetPending();
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
    // ✎ 2026-09-21: було `[11, 12]`. Після `e470777a` десяткова комірка їде на
    // сервер РЯДКОМ — `Number` на цьому шляху був другою точкою втрати знаків
    // (див. `DocumentGrid.decimalStrings.test.tsx`).
    expect(patched[0]?.cells.map((cell) => cell.value)).toEqual(['11', '12']);
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

/**
 * Дефект, знайдений під час UI-08 (рядок формули/підсумків): на таблиці з
 * підписами рядків (`hasRowLabels(slice)` істинна) `gridColumns` вставляє
 * `RowLabelProp` ПЕРШОЮ колонкою, і решта зсувається на одну позицію
 * праворуч у тому, що шле RevoGrid. `onPaste`/`onCopy` брали
 * `columnIndex`/`fromColumn`/`toColumn` з події НАПРЯМУ, без поправки на цей
 * зсув — тобто Ctrl+V лягав на одну колонку правіше, а Ctrl+C копіював
 * зсунуте вікно. Тести вище цього не ловили: у їхній фікстурі `label: null`
 * на кожному рядку, тож `hasRowLabels` завжди хибна.
 */
describe('DocumentGrid: колонка підписів рядків не зсуває дані (UI-08 slice)', () => {
  it('вставка у другу колонку даних (C2) потрапляє саме в C2, а не в C3', async () => {
    mockServer(sliceFixtureWithLabels);
    show();

    const stub = await screen.findByTestId('revogrid-stub');
    // Грід-індекс 2 — це візуально C2, коли колонка підпису займає індекс 0.
    fireEvent.click(screen.getByRole('button', { name: 'focus-r3-c2-labeled' }));

    fireEvent.paste(stub, clipboard('11\t12\n'));

    await waitFor(() => expect(patched).toHaveLength(1));

    // ⛔ Мутаційний доказ (RED до фіксу): без поправки на зсув
    // `columnCodes[2 + c]` бере C3, потім вихід за межі — цілилося б у ['C3']
    // з одним значенням, а не в ['C2', 'C3'].
    expect(patched[0]?.rowKey).toBe('r3');
    expect(patched[0]?.cells.map((cell) => cell.columnCode)).toEqual(['C2', 'C3']);
    expect(patched[0]?.cells.map((cell) => cell.value)).toEqual(['11', '12']);
  });

  it('копіювання виділеного C2..C3 віддає рівно ці колонки, без зсуву', async () => {
    mockServer(sliceFixtureWithLabels);
    show();

    const stub = await screen.findByTestId('revogrid-stub');
    fireEvent.click(screen.getByRole('button', { name: 'select-range-labeled' }));

    const event = clipboard('');
    fireEvent.copy(stub, event);

    // ⛔ Мутаційний доказ (RED до фіксу): без поправки `clampSelection` бачить
    // `fromColumn=2, toColumn=3` при `columnCount=3` (`data.columns.length`),
    // обрізає `toColumn` до 2 — і в буфер іде лише одна колонка (C3), не дві.
    expect(event.clipboardData.setData).toHaveBeenCalledWith('text/plain', 'r2-2\tr2-3\nr3-2\tr3-3\n');
  });

  it('вставка з якорем на самій колонці підпису клампиться до першої колонки даних', async () => {
    mockServer(sliceFixtureWithLabels);
    show();

    const stub = await screen.findByTestId('revogrid-stub');
    fireEvent.click(screen.getByRole('button', { name: 'focus-r3-label' }));

    fireEvent.paste(stub, clipboard('99\n'));

    await waitFor(() => expect(patched).toHaveLength(1));

    // ⚠ Колонка підпису readonly й не входить у `data.columns`: якір на ній
    // клампиться до першої колонки ДАНИХ (`dataColumnIndexOf`), а не губиться.
    expect(patched[0]?.rowKey).toBe('r3');
    expect(patched[0]?.cells.map((cell) => cell.columnCode)).toEqual(['C1']);
    expect(patched[0]?.cells.map((cell) => cell.value)).toEqual(['99']);
  });

  it('копіювання діапазону, що зачіпає колонку підпису, не тягне зайву колонку даних', async () => {
    mockServer(sliceFixtureWithLabels);
    show();

    const stub = await screen.findByTestId('revogrid-stub');
    fireEvent.click(screen.getByRole('button', { name: 'select-range-label-included' }));

    const event = clipboard('');
    fireEvent.copy(stub, event);

    // Виділення (грід-індекси 0..1: підпис, C1) у просторі даних клампиться
    // до [0,0] — рівно C1, без нічого поза межею вибраного.
    expect(event.clipboardData.setData).toHaveBeenCalledWith('text/plain', 'r2-1\nr3-1\n');
  });

  it('регресія: таблиця БЕЗ підписів рядків лишається як була (hasRowLabels хибна)', async () => {
    mockServer(sliceFixture);
    show();

    const stub = await screen.findByTestId('revogrid-stub');
    fireEvent.click(screen.getByRole('button', { name: 'focus-r3-c2' }));

    fireEvent.paste(stub, clipboard('11\t12\n'));

    await waitFor(() => expect(patched).toHaveLength(1));

    expect(patched[0]?.rowKey).toBe('r3');
    expect(patched[0]?.cells.map((cell) => cell.columnCode)).toEqual(['C2', 'C3']);
  });
});

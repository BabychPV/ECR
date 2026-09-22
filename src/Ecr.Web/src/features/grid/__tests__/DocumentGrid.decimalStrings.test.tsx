import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnRegular } from '@revolist/revogrid';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { DefaultLanguage, setLanguage } from '@/shared/i18n';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * `decimal` приходить РЯДКОМ (коміт `e470777a`) — шлях цілком: відповідь
 * сервера → модель сітки → екран, і ввід → тіло `PATCH`.
 *
 * ⚠ Чому все ж компонентний тест, попри те що чиста логіка вже перевірена
 * окремо (`decimalCells.test.ts`): саме ЗЧЕПЛЕННЯ трьох шарів і було місцем
 * втрати — зріз віддавав рядок, `coerce` робив із нього `double`, а показ
 * малював те, що лишилося. Кожен шар окремо при цьому виглядав правильним.
 *
 * ⛔ `RevoGrid` підмінено — як і в решті тестів сітки (справжній веб-компонент
 * у jsdom лише заважає). Але заглушка НЕ вигадує показу: вона кличе
 * `cellTemplate` самих колонок, які склав продуктовий `gridColumns`, і
 * `onAfteredit` із тим самим `detail`, який шле бібліотека. Інакше «на екрані»
 * означало б «у моєму тесті».
 */
vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: {
    columns?: ColumnRegular[];
    source?: Record<string, unknown>[];
    onAfteredit?: (event: { detail: unknown }) => void;
  }) => (
    <div data-testid="revogrid-stub">
      {(props.source ?? []).map((model) =>
        (props.columns ?? []).map((column) => {
          const prop = String(column.prop);
          const value = model[prop];

          // ⚠ І показ, і СТАН комірки беруться з продуктових колонок, а не
          // вигадуються заглушкою: `cellTemplate` малює значення, а
          // `cellProperties` віддає `data-cell-state` — той самий атрибут, за
          // яким позначку незбереженої правки читає решта тестів сітки.
          const cellProps =
            typeof column.cellProperties === 'function'
              ? ((column.cellProperties({ model } as never) ?? {}) as Record<string, unknown>)
              : {};

          const state = cellProps['data-cell-state'];

          return (
            <span
              key={`${String(model.__rowKey)}:${prop}`}
              data-testid={`cell-${String(model.__rowKey)}-${prop}`}
              data-cell-state={typeof state === 'string' ? state : undefined}
            >
              {typeof column.cellTemplate === 'function'
                ? String(
                    column.cellTemplate(
                      (() => null) as never,
                      { value, model, prop } as never,
                      undefined as never,
                    ),
                  )
                : String(value ?? '')}
            </span>
          );
        }),
      )}

      <button
        type="button"
        onClick={() =>
          props.onAfteredit?.({ detail: { prop: 'C1', model: { __rowKey: 'r2' }, val: '5' } })
        }
      >
        edit-r2-C1-5
      </button>
      <button
        type="button"
        onClick={() =>
          props.onAfteredit?.({ detail: { prop: 'C1', model: { __rowKey: 'r2' }, val: '7' } })
        }
      >
        edit-r2-C1-7
      </button>
    </div>
  ),
}));

/**
 * Значення, яке `double` ГАРАНТОВАНО псує, і те, на що він його перетворює.
 *
 * ⛔ Не будь-які «16 знаків»: `String(Number('1.2345678901234567'))` повертає
 * той самий рядок, і мутація «пустити через `Number`» лишилася б ЗЕЛЕНОЮ. Тут
 * двадцять значущих цифр; окреме твердження нижче фіксує сам факт псування.
 */
const Twenty = '1234.1234567890123456';
const TwentyThroughDouble = '1234.1234567890124';

function column(code: string, ordinal: number, scale: number): ColumnDto {
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
    scale,
    unitId: null,
    unitSymbol: null,
  };
}

/**
 * Зріз рівно такий, яким його ТЕПЕР віддає сервер: усі `decimal` — рядки в
 * масштабі колонки, з хвостовими нулями.
 */
function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns: [column('C1', 0, 16), column('C2', 1, 2)],
    rows: [
      {
        cells: { C1: Twenty, C2: '12.3400000000' },
        isOrphaned: false,
        label: null,
        ordinal: 0,
        rowKey: 'r1',
        rowVersion: 'v1',
        rowKind: 'Item',
      },
      {
        cells: { C1: '5.0000000000', C2: '0.0000000000' },
        isOrphaned: false,
        label: null,
        ordinal: 1,
        rowKey: 'r2',
        rowVersion: 'v2',
        rowKind: 'Item',
      },
    ],
  };
}

/** Тіла надісланих `PATCH`. */
const patched: { rowKey: string; cells: { columnCode: string; value: unknown }[] }[] = [];

function mockServer(): void {
  patched.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn((_path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        const body = JSON.parse(String(init.body)) as {
          rows: { rowKey: string; cells: { columnCode: string; value: unknown }[] }[];
        };
        patched.push(...body.rows);

        return Promise.resolve(
          new Response(JSON.stringify({ appliedCells: 1, rowVersions: {}, validation: [] }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
        );
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

function clipboard(text: string): {
  clipboardData: { getData: () => string; setData: ReturnType<typeof vi.fn> };
} {
  return { clipboardData: { getData: () => text, setData: vi.fn() } };
}

/**
 * Стан комірки, як його бачить оператор; `null` — жодного.
 *
 * ⚠ Читається саме `data-cell-state`, а не клас і не колір: так само робить
 * решта тестів сітки (`ФВ-14.18`), і так твердження не залежить від палітри.
 */
function cellState(testId: string): string | null {
  return screen.getByTestId(testId).getAttribute('data-cell-state');
}

beforeEach(() => {
  setLanguage('en');
});

afterEach(() => {
  cancelAutosave();
  resetPending();
  setLanguage(DefaultLanguage);
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('А: значення з відповіді доходить до екрана без втрати знаків', () => {
  it('замір: Number(...) на цьому вході ТАКИ псує значення', () => {
    // ⛔ Без цього твердження наступне доводило б менше, ніж обіцяє.
    expect(String(Number(Twenty))).toBe(TwentyThroughDouble);
  });

  it('усі двадцять цифр намальовані повністю', async () => {
    mockServer();
    show();

    const cell = await screen.findByTestId('cell-r1-C1');

    /*
     * ⛔ Мутаційна межа. Будь-який `Number(...)` на шляху «відповідь → екран»
     * дав би `1,234.1234567890124` — на три знаки коротше, і жодного сліду в
     * консолі. Тому порівняння РЯДКІВ і точне: `toContain('1,234.12')` був би
     * зелений і на втраченому хвості.
     */
    expect(cell.textContent?.replace(/[   ]/g, ' ')).toBe(
      '1,234.1234567890123456',
    );
  });

  it('Г: хвіст сервера (10 знаків) зрізається до формату колонки (scale)', async () => {
    mockServer();
    show();

    // ✎ 2026-09-22, свідома зміна: раніше тут стояло «5» і «0» — показ
    // зрізав УСІ хвостові нулі. Тепер кількість знаків — конфігурація комірки
    // (як формат у Excel): колонка зі `scale = N` показує рівно N знаків
    // (`DocumentGrid.fixedScale.test.tsx`). Серверний хвіст `.0000000000`
    // (10 знаків) при цьому все одно не просочується: C1 має scale 16, C2 — 2.
    expect((await screen.findByTestId('cell-r2-C1')).textContent).toBe('5.0000000000000000');
    expect((await screen.findByTestId('cell-r1-C2')).textContent).toBe('12.34');
    expect((await screen.findByTestId('cell-r2-C2')).textContent).toBe('0.00');
  });
});

describe('Ctrl+C: у буфер іде число, а не число з хвостом нулів', () => {
  it('копія придатна для вставки назад в Excel', async () => {
    mockServer();
    show();

    const stub = await screen.findByTestId('revogrid-stub');
    const event = clipboard('');
    fireEvent.copy(stub, event);

    /*
     * ⛔ `String(row.cells[...])` клав би сюди `5.0000000000` і `0.0000000000`
     * — у КОЖНУ комірку аркуша, який оператор потім відкриває в Excel. Число
     * те саме, аркуш нечитабельний.
     *
     * ⚠ І водночас НЕ `formatDecimal`: групування розрядів (`1 234,12…`)
     * Excel прочитав би як текст, тобто копія перестала б бути копією.
     */
    expect(event.clipboardData.setData).toHaveBeenCalledWith(
      'text/plain',
      `${Twenty}\t12.34\n5\t0\n`,
    );
  });
});

describe('Б: комірка не лишається брудною', () => {
  it('ввід «5» поверх серверного «5.0000000000» позначки НЕ ставить', async () => {
    /*
     * ⛔ Суть вимоги. Рядок `r2` містить `"5.0000000000"`, оператор бачить `5`
     * (див. Г вище) і набирає `5`; RevoGrid повідомляє `afteredit` на кожен
     * вихід із редактора, змінився текст чи ні. Порівняння ТЕКСТУ назвало б це
     * правкою — і комірка отримала б позначку незбереженої на порожньому
     * місці, а лічильник «змінено комірок: N» показував би роботу, якої не
     * було.
     */
    mockServer();
    show();

    await screen.findByTestId('revogrid-stub');
    expect(cellState('cell-r2-C1')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'edit-r2-C1-5' }));

    await waitFor(() => expect(cellState('cell-r2-C1')).toBeNull());
    expect(patched).toHaveLength(0);
  });

  it('справжня зміна значення позначку ставить і після збереження знімає', async () => {
    /*
     * ⚠ Дзеркало попереднього: без нього воно було б зелене й на коді, який не
     * помічає ЖОДНОЇ правки. І водночас друга половина вимоги — позначка
     * мусить ЗНИКНУТИ, коли сервер відповів.
     */
    mockServer();
    show();

    await screen.findByTestId('revogrid-stub');
    fireEvent.click(screen.getByRole('button', { name: 'edit-r2-C1-7' }));

    await waitFor(() => expect(cellState('cell-r2-C1')).toBe('dirty'));

    // Автозбереження шле пакет через 500 мс тиші.
    await waitFor(() => expect(patched).toHaveLength(1), { timeout: 5000 });
    expect(patched[0]?.cells[0]?.value).toBe('7');

    await waitFor(() => expect(cellState('cell-r2-C1')).toBeNull(), { timeout: 5000 });
  });
});

describe('В: вставка шле на сервер РІВНО той рядок, що показано', () => {
  it('16 знаків із буфера доходять до тіла запиту без double', async () => {
    mockServer();
    show();

    const stub = await screen.findByTestId('revogrid-stub');

    // Масштаб `C1` — 16, а в буфері 17 знаків дробу: округлення СПРАЦЮЄ, тобто
    // перевіряється саме та гілка, де стояв `Number(fixed)`.
    fireEvent.paste(stub, clipboard('1234.12345678901234567\n'));

    await waitFor(() => expect(patched).toHaveLength(1));

    /*
     * ⛔ Мутаційна межа. До 2026-09-21 на межі відправлення стояв
     * `Number(fixed)`, і в тілі запиту опинявся `1234.1234567890124` — інше
     * число, ніж показане оператору. `toBe` над РЯДКОМ: `toBeCloseTo` тут не
     * помітив би нічого.
     */
    expect(patched[0]?.cells[0]?.value).toBe('1234.1234567890123457');
    expect(typeof patched[0]?.cells[0]?.value).toBe('string');
    expect(String(Number(patched[0]?.cells[0]?.value))).toBe(TwentyThroughDouble);
  });

  it('значення в межах масштабу теж їде рядком, а не числом', async () => {
    mockServer();
    show();

    const stub = await screen.findByTestId('revogrid-stub');
    fireEvent.paste(stub, clipboard(`${Twenty}\n`));

    await waitFor(() => expect(patched).toHaveLength(1));

    // ⚠ Тут `roundToScale` віддає `null` (16 знаків при масштабі 16), тож
    // значення йде шляхом `coerce` — другою з двох гілок вставки. Обидві
    // зобов'язані донести той самий рядок.
    expect(patched[0]?.cells[0]?.value).toBe(Twenty);
  });

  it('значення поза масштабом округлюється рядково й тим самим рядком їде', async () => {
    mockServer();
    show();

    const stub = await screen.findByTestId('revogrid-stub');

    // Колонка `C2` має масштаб 2; якір за замовчуванням — кут таблиці, тож
    // вставляємо два стовпці й дивимось на другий.
    fireEvent.paste(stub, clipboard('1\t12.345\n'));

    await waitFor(() => expect(patched).toHaveLength(1));

    const c2 = patched[0]?.cells.find((cell) => cell.columnCode === 'C2');

    // Половина від нуля — як на сервері (`MidpointRounding.AwayFromZero`).
    expect(c2?.value).toBe('12.35');
    expect(typeof c2?.value).toBe('string');
  });

  it('перелік «округлено» показує той самий рядок, що пішов на сервер', async () => {
    mockServer();
    show();

    const stub = await screen.findByTestId('revogrid-stub');
    fireEvent.paste(stub, clipboard('1\t12.345\n'));

    await waitFor(() => expect(patched).toHaveLength(1));

    // ⚠ «Мовчки округлювати не можна ніде» (`ФВ-9.16c`): оператор бачить
    // перелік, і в ньому має стояти рівно те, що поїхало. Кнопку шукаємо за
    // ключем каталогу — у тестовому прогоні каталог не завантажений, і `t()`
    // віддає сам ключ.
    fireEvent.click(await screen.findByRole('button', { name: /roundedShow|Show|Показ/u }));
    expect(await screen.findByText(/12\.345 → 12\.35/u)).toBeTruthy();
  });
});

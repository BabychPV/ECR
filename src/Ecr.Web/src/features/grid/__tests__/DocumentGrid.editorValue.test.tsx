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
 * Редактор комірки відкривається з тим значенням, яке людина БАЧИТЬ (`U-24`).
 *
 * ⛔ Що було зламано. На екрані `610.5291`, а подвійний клік відкривав поле з
 * `610.5291000000000000` — записом сховища `decimal(34,16)`, — і курсор у
 * кінці. Будь-яке дописування йшло ПІСЛЯ шістнадцяти нулів: на живому стенді
 * `123` дав `931.9250000000000000123`, і сервер це «прийняв».
 *
 * ⚠ Звідки редактор бере текст — перевірено в коді пакета, а не припущено:
 * `revogr-overlay-selection.js` відкриває `revogr-edit` зі значенням
 * `editCell.val || getCellData(rowDataModel(y, x).value)`. `editCell.val`
 * непорожній лише тоді, коли редагування почато ДРУКОМ символу (тоді символ
 * замінює вміст, і ця вада не виникає). Подвійний клік, F2 і Enter несуть
 * порожній `val` — тобто всі три шляхи беруть текст із МОДЕЛІ РЯДКА, яку
 * будує `gridRows`. Саме її заглушка нижче й віддає в `data-editor`.
 */
vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: {
    columns?: ColumnRegular[];
    source?: Record<string, unknown>[];
    onAfteredit?: (event: { detail: unknown }) => void;
  }) => {
    const r1 = (props.source ?? []).find((model) => model.__rowKey === 'r1') ?? {};
    const fire = (val: string) =>
      props.onAfteredit?.({ detail: { prop: 'C1', model: r1, val } });

    // Те, що `revogr-edit` отримає як текст поля для порожнього `editCell.val`.
    const editorText = (value: unknown) => (value === null || value === undefined ? '' : String(value));

    return (
      <div data-testid="revogrid-stub">
        {(props.source ?? []).map((model) =>
          (props.columns ?? []).map((column) => {
            const prop = String(column.prop);

            return (
              <span
                key={`${String(model.__rowKey)}:${prop}`}
                data-testid={`cell-${String(model.__rowKey)}-${prop}`}
                data-editor={editorText(model[prop])}
                data-editor-type={typeof model[prop]}
              />
            );
          }),
        )}
        {/* Відкрив редактор (подвійний клік / F2) і закрив Enter, нічого не змінивши. */}
        <button type="button" onClick={() => fire(editorText(r1.C1))}>
          commit-unchanged
        </button>
        {/* Відкрив редактор і дописав «123» у кінець — курсор там, як в Excel. */}
        <button type="button" onClick={() => fire(`${editorText(r1.C1)}123`)}>
          append-123
        </button>
      </div>
    );
  },
}));

function column(code: string, ordinal: number, overrides: Partial<ColumnDto> = {}): ColumnDto {
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
    scale: 4,
    unitId: null,
    unitSymbol: null,
    ...overrides,
  };
}

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns: [
      // Рівно те, що виміряно на стенді: `decimal(34,16)` зі scale 4.
      column('C1', 0),
      // Вихід формули зі значущим знаком ПОНАД `scale` (`U-05`): він має лишитися.
      column('C2', 1, { dataType: 'Formula', scale: 2, isReadOnly: true }),
      // Ціла колонка їде JSON-числом — редактор бере його як є.
      column('C3', 2, { dataType: 'Int', scale: null }),
      // Текстова колонка з «числовим» текстом — не чіпається зовсім.
      column('C4', 3, { dataType: 'String', scale: null }),
      // Недесяткове в числовій колонці, якого сервер ще не відхилив.
      column('C5', 4),
      // Значення ≥ 1000: показ дав би `1,234.5000` — групування й нулі формату.
      column('C6', 5),
    ],
    rows: [
      {
        cells: {
          C1: '610.5291000000000000',
          C2: '1.0050000000000000',
          C3: 4242,
          C4: '0012.5000',
          C5: 'н/д',
          C6: '1234.5000000000000000',
        },
        isOrphaned: false,
        label: null,
        ordinal: 0,
        rowKey: 'r1',
        rowVersion: 'v1',
        rowKind: 'Item',
      },
    ],
  };
}

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

async function editorOf(code: string): Promise<HTMLElement> {
  return screen.findByTestId(`cell-r1-${code}`);
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

describe('редактор відкривається з показаним значенням, не із записом сховища (U-24)', () => {
  it('«610.5291000000000000» → у полі «610.5291»', async () => {
    mockServer();
    show();

    expect((await editorOf('C1')).getAttribute('data-editor')).toBe('610.5291');
  });

  it('без групування розрядів і без нулів формату: це поле вводу, не показ', async () => {
    mockServer();
    show();

    // Показ цієї комірки — `1,234.5000` (scale 4). У поле не йде ні `,` (її
    // `coerce` прочитав би як десяткову кому), ні доповнення до scale —
    // інакше дописування знову йшло б після нулів, лише трьох, а не
    // дванадцяти.
    expect((await editorOf('C6')).getAttribute('data-editor')).toBe('1234.5');
  });

  it('значущий знак понад scale (вихід формули) НЕ губиться', async () => {
    mockServer();
    show();

    // `scale` = 2, значення має третій значущий знак — він справжній.
    expect((await editorOf('C2')).getAttribute('data-editor')).toBe('1.005');
  });

  it('дзеркала: ціле лишається числом, текст і недесяткове — як є', async () => {
    mockServer();
    show();

    const int = await editorOf('C3');
    expect(int.getAttribute('data-editor')).toBe('4242');
    expect(int.getAttribute('data-editor-type')).toBe('number');

    expect((await editorOf('C4')).getAttribute('data-editor')).toBe('0012.5000');
    expect((await editorOf('C5')).getAttribute('data-editor')).toBe('н/д');
  });
});

describe('редагування від показаного значення (U-24)', () => {
  it('дописане в кінець «123» дає «610.5291123», а не «610.5291000000000000123»', async () => {
    mockServer();
    show();

    await screen.findByTestId('revogrid-stub');
    fireEvent.click(screen.getByRole('button', { name: 'append-123' }));

    await waitFor(() => expect(patched).toHaveLength(1), { timeout: 5000 });
    expect(patched[0]?.cells[0]).toMatchObject({ columnCode: 'C1', value: '610.5291123' });
  });

  it('⛔ відкрив і закрив редактор без правки — жодного PATCH', async () => {
    /*
     * RevoGrid повідомляє `afteredit` на кожен вихід із редактора Enter-ом,
     * змінився текст чи ні. Після цієї зміни текст поля (`610.5291`) і
     * значення зрізу (`610.5291000000000000`) різняться ЗАПИСОМ — і рівно це
     * перетворилося б на правку-фантом, якби порівняння було текстовим.
     */
    mockServer();
    show();

    await screen.findByTestId('revogrid-stub');
    fireEvent.click(screen.getByRole('button', { name: 'commit-unchanged' }));

    // Вікно автозбереження — 500 мс тиші; чекаємо втричі довше.
    await new Promise((resolve) => setTimeout(resolve, 1500));

    expect(patched).toHaveLength(0);
  });
});

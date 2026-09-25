import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDataSchemaModel, EditCell, EditorBase, EditorCtrCallable } from '@revolist/revogrid';
import type { ColumnDto, TableSliceDto, UnitRef } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { NoLocalFlags } from '../cellState';
import { captureEdit, coerce } from '../edits';
import { resetPending } from '../pendingStore';
import { DocumentGrid, gridColumns } from '../DocumentGrid';
import { lookupIdOfText } from '../LookupCellEditor';
import { unitCellDisplay, unitIdOfCode } from '../UnitCellEditor';
import { PickerChangeWindowMs } from '../DateCellEditor';

/**
 * `R-01` — комірка одиниці редагується переліком одиниць, а не номером у
 * текстовому полі; `R-02` — комірка дати має поле дати з календарем.
 *
 * ⛔ Що ламалося, живцем на стенді: у колонку `Unit` оператор набирав `kg`, і
 * сервер відмовляв «expects the identifier» — окремого редактора не було, а
 * номера одиниці людина не знає. Дата редагувалась текстом у форматі
 * сховища.
 */

const Units: UnitRef[] = [
  { id: 21, code: 'kg', dimensionCode: 'Mass', dimensionId: 1, factorToBase: '1', offsetToBase: '0' },
  { id: 22, code: 't', dimensionCode: 'Mass', dimensionId: 1, factorToBase: '1000', offsetToBase: '0' },
  { id: 31, code: 'm3', dimensionCode: 'Volume', dimensionId: 2, factorToBase: '1', offsetToBase: '0' },
];

function column(overrides: Partial<ColumnDto> = {}): ColumnDto {
  return {
    id: 1,
    code: 'U1',
    header: 'Unit',
    dataType: 'Unit',
    ordinal: 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    displayFormat: null,
    defaultValue: null,
    lookupRegistryDefId: null,
    unitId: null,
    unitSymbol: null,
    ...overrides,
  };
}

function slice(columns: ColumnDto[]): TableSliceDto {
  return {
    tableInstanceId: 1,
    periodKey: 202609,
    columns,
    rows: [{ rowKey: 'R1', ordinal: 1, rowKind: 'Item', label: null, rowVersion: 'v1', cells: {}, isOrphaned: false }],
    cellPermissions: {},
    cellConfirmations: {},
  };
}

const noRequiredInput = { blocked: new Map<string, string>(), warning: new Map<string, string>() };

function columnsWith(dataColumn: ColumnDto, units: UnitRef[] | null) {
  const found = gridColumns(
    slice([dataColumn]), false, NoLocalFlags, {}, noRequiredInput, new Map(), new Map(), new Map(), new Set(), units,
  ).find((candidate) => candidate.prop === dataColumn.code);
  if (found === undefined) throw new Error('немає колонки');

  return found;
}

function open(editor: EditorCtrCallable, value: unknown, save = vi.fn()): { node: HTMLElement; instance: EditorBase } {
  const instance = editor({ value } as unknown as ColumnDataSchemaModel, save, vi.fn());
  const node = document.createElement('div');
  document.body.append(node);
  hosts.push(node);

  instance.editCell = { val: value } as unknown as EditCell;
  instance.render(((tag: string) => ({ tag })) as never);
  instance.element = node;
  instance.componentDidRender?.();

  return { node, instance };
}

function press(input: Element | null, key: string): void {
  input?.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));
}

/** Вузли, які тест додав сам: чужі (контейнер React) прибирає бібліотека. */
const hosts: HTMLElement[] = [];

afterEach(() => {
  for (const node of hosts.splice(0)) node.remove();
  for (const node of document.querySelectorAll('.ecr-list-editor-list')) node.remove();
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('R-01: комірка Unit', () => {
  it('редактор — перелік одиниць за кодом із розмірністю, збереження — ідентифікатор', () => {
    const save = vi.fn();
    const { node } = open(columnsWith(column(), Units).editor as EditorCtrCallable, 21, save);

    expect([...document.querySelectorAll('[role="option"]')].map((item) => item.textContent)).toEqual([
      '⟦grid.listClear⟧',
      'kgMass',
      'tMass',
      'm3Volume',
    ]);

    const input = node.querySelector('input');
    press(input, 'ArrowDown');
    press(input, 'Enter');

    expect(save).toHaveBeenCalledWith('22', false);
    expect(coerce('22', 'Unit')).toBe(22);
  });

  it('показ комірки — код одиниці, а не номер', () => {
    const template = columnsWith(column(), Units).cellTemplate as (h: unknown, props: { value?: unknown }) => string;

    expect(template(null, { value: 21 })).toBe('kg');
    expect(template(null, { value: null })).toBe('');
    // ⚠ Одиниці немає в переліку — видно те, що лежить у комірці.
    expect(unitCellDisplay(99, Units)).toBe('99');
  });

  it('перелік одиниць ще їде — «завантаження»', () => {
    open(columnsWith(column(), null).editor as EditorCtrCallable, null);

    expect(document.body.textContent).toContain('grid.listLoading');
  });

  it('код одиниці й запису довідника з Excel розпізнається лише ТОЧНО', () => {
    expect(unitIdOfCode(' KG ', Units)).toBe(21);
    expect(unitIdOfCode('kilo', Units)).toBeNull();

    const entries = [{ id: 5, code: 'KZ', display: 'Казахстан', parentEntryId: null, validFrom: null, validTo: null }];
    expect(lookupIdOfText('kz', entries)).toBe(5);
    expect(lookupIdOfText('казахстан', entries)).toBe(5);
    expect(lookupIdOfText('Казах', entries)).toBeNull();
  });
});

describe('R-02: комірка Date', () => {
  it('поле дати з поточною датою без години сховища; Enter зберігає yyyy-MM-dd', () => {
    const save = vi.fn();
    const { node } = open(
      columnsWith(column({ code: 'D1', dataType: 'Date' }), null).editor as EditorCtrCallable,
      '2026-09-15T00:00:00',
      save,
    );

    const input = node.querySelector('input') as HTMLInputElement;
    expect(input.type).toBe('date');
    expect(input.value).toBe('2026-09-15');

    input.value = '2026-09-30';
    press(input, 'Enter');

    expect(save).toHaveBeenCalledWith('2026-09-30', false);
  });

  it('порожнє поле дати — «не заповнено», а не порожній рядок, і не правка порожньої комірки', () => {
    expect(coerce('', 'Date')).toBeNull();
    expect(coerce('2026-09-15', 'Date')).toBe('2026-09-15');

    const dated = slice([column({ code: 'D1', dataType: 'Date' })]);
    expect(captureEdit(dated, { rowKey: 'R1', columnCode: 'D1', raw: '' })).toBeNull();
  });

  it('вибір дня в календарі (change без клавіші) зберігає; change від друку — ні', () => {
    vi.useFakeTimers();
    try {
      const save = vi.fn();
      const { node } = open(
        columnsWith(column({ code: 'D1', dataType: 'Date' }), null).editor as EditorCtrCallable,
        null,
        save,
      );
      const input = node.querySelector('input') as HTMLInputElement;

      // Друк цифри — `change` одразу після клавіші: людина ще друкує.
      press(input, '2');
      input.value = '2026-01-02';
      input.dispatchEvent(new Event('change'));
      expect(save).not.toHaveBeenCalled();

      // Клік у календарі — `change` без клавіші перед ним.
      vi.advanceTimersByTime(PickerChangeWindowMs + 50);
      input.value = '2026-01-20';
      input.dispatchEvent(new Event('change'));
      expect(save).toHaveBeenCalledWith('2026-01-20', false);
    } finally {
      vi.useRealTimers();
    }
  });
});

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: () => <div data-testid="revogrid-stub" />,
}));

describe('R-01: вставка з Excel — код одиниці стає ідентифікатором', () => {
  it('«kg» у колонці Unit їде на сервер як 21', async () => {
    const patches: unknown[] = [];

    vi.stubGlobal(
      'fetch',
      vi.fn(async (path: string, init?: RequestInit) => {
        if (init?.method === 'PATCH') {
          patches.push(JSON.parse(String(init.body)));

          return new Response(JSON.stringify({ appliedCells: 1, rowVersions: { R1: 'v2' }, validation: [] }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          });
        }

        const body = path.includes('/api/v1/units') ? Units : slice([column()]);

        return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }),
    );

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

    const stub = await screen.findByTestId('revogrid-stub');
    await waitFor(() => expect(client.getQueryData(['units'])).toBeDefined());

    fireEvent.paste(stub, { clipboardData: { getData: () => 'kg\n', setData: vi.fn() } });

    await waitFor(() => expect(patches).toHaveLength(1));

    const request = patches[0] as { rows: { cells: { columnCode: string; value: unknown }[] }[] };
    expect(request.rows[0]?.cells[0]).toMatchObject({ columnCode: 'U1', value: 21 });
  });
});

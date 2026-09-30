import { afterEach, describe, expect, it, vi } from 'vitest';
import { h } from '@revolist/revogrid';
import type { ColumnDataSchemaModel, EditCell, EditorBase, EditorCtrCallable, VNode } from '@revolist/revogrid';
import type { ColumnDto, RegistryEntryDto, TableSliceDto } from '@/api/types';
import { gridColumns } from '../DocumentGrid';
import { NoLocalFlags } from '../cellState';
import { coerce } from '../edits';

/** `EditorBase.render` типізовано СОЮЗОМ (`VNode | VNode[] | string | void`) — редактор тут завжди повертає рівно `VNode`, тож тест звужує це один раз тут, а не в кожному виклику. */
function renderOf(instance: EditorBase): VNode {
  return instance.render(h) as VNode;
}

/**
 * Директива `docs/build/directive-registry-lookup-and-cell-style.md`, PR A4:
 * комірка `CellDataType.Lookup` — dropdown записів довідника, не звичайний
 * текстовий інпут.
 *
 * ⚠ `gridColumns` — чиста функція побудови опису колонок, тому доступна для
 * одиничного тесту без монтування веб-компонента RevoGrid (той самий підхід,
 * що й `gridColumns.requiredIndicator.test.ts`/`gridColumns.saveError.test.ts`).
 * Сам редактор (`LookupCellEditor.createLookupCellEditor`) тестується тут
 * ЧЕРЕЗ повернуте `column.editor` — викликаний зі СПРАВЖНЬОЮ `h` (стенсіл),
 * а не заглушкою, щоб довести реальну форму VNode, яку побачить RevoGrid.
 */

function column(overrides: Partial<ColumnDto> = {}): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'Країна',
    dataType: 'Decimal',
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

function slice(overrides: Partial<TableSliceDto> = {}): TableSliceDto {
  return {
    tableInstanceId: 1,
    periodKey: 202609,
    columns: [column()],
    rows: [
      { rowKey: 'R1', ordinal: 1, rowKind: 'Item', label: null, rowVersion: '0x01', cells: {}, isOrphaned: false },
    ],
    cellPermissions: {},
    cellConfirmations: {},
    ...overrides,
  };
}

function entry(overrides: Partial<RegistryEntryDto> = {}): RegistryEntryDto {
  return {
    id: 1,
    code: 'KZ',
    display: 'Казахстан',
    parentEntryId: null,
    validFrom: null,
    validTo: null,
    ...overrides,
  };
}

const noRequiredInput = { blocked: new Map<string, string>(), warning: new Map<string, string>() };

/** Відкриває редактор так, як це робить RevoGrid: `render` → `element` → `componentDidRender`. */
function openEditor(editor: EditorCtrCallable, value: unknown, save: (value?: unknown, preventFocus?: boolean) => void): HTMLElement {
  const instance = editor(
    { prop: 'C1', model: { __rowKey: 'R1' }, value } as unknown as ColumnDataSchemaModel,
    save,
    vi.fn(),
  );
  const node = document.createElement('div');
  node.className = 'ecr-test-host';
  document.body.append(node);

  instance.editCell = { val: value } as unknown as EditCell;
  renderOf(instance);
  instance.element = node;
  instance.componentDidRender?.();

  return node;
}

function optionValues(_host: HTMLElement): (string | null)[] {
  return [...document.querySelectorAll('[role="option"]')].map((item) => item.getAttribute('data-value'));
}

function optionLabels(_host: HTMLElement): (string | null | undefined)[] {
  return [...document.querySelectorAll('[role="option"]')].map((item) => item.firstElementChild?.textContent);
}

function press(node: HTMLElement, key: string): void {
  node.querySelector('input')?.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));
}


function lookupColumnOf(columns: ReturnType<typeof gridColumns>) {
  const found = columns.find((c) => c.prop === 'C1');
  if (found === undefined) throw new Error('Немає колонки C1');
  return found;
}

/** Перелік редактора живе в `document.body` (`listCellEditor.ts`) — прибрати між тестами. */
afterEach(() => {
  for (const node of document.querySelectorAll('.ecr-list-editor-list, .ecr-test-host')) node.remove();
});

describe('gridColumns — Lookup-колонка: dropdown записів довідника (директива registry-lookup, PR A4)', () => {
  it('звичайна (не-Lookup) колонка не отримує ані editor, ані cellTemplate', () => {
    // ✎ 2026-09-21: колонку тут змінено з `Decimal` на `String`. Після
    // `e470777a` ДЕСЯТКОВА колонка таки отримує `cellTemplate` — показ
    // десяткового рядка (`cellValue.cellDisplay`), бо сервер більше не віддає
    // число, а `"5.0000000000"` малювалося б як є. Твердження цього тесту —
    // про `Lookup`, тож перевіряти його треба на колонці, до якої жоден із
    // двох шаблонів стосунку не має.
    const plain = slice({ columns: [column({ dataType: 'String' })] });
    const columns = gridColumns(plain, false, NoLocalFlags, {}, noRequiredInput, new Map(), new Map());
    const found = lookupColumnOf(columns);

    expect(found.editor).toBeUndefined();
    expect(found.cellTemplate).toBeUndefined();
  });

  it('десяткова колонка має показ, але НЕ має Lookup-редактора', () => {
    // ⚠ Дзеркало до попереднього: показ десяткового і dropdown довідника —
    // різні речі, і зникнення одного не має тягти за собою друге.
    const columns = gridColumns(slice(), false, NoLocalFlags, {}, noRequiredInput, new Map(), new Map());
    const found = lookupColumnOf(columns);

    expect(found.editor).toBeUndefined();
    expect(found.cellTemplate).toBeTypeOf('function');
  });

  it('Lookup-колонка з lookupRegistryDefId — редактор і показ підключені', () => {
    const withLookup = slice({ columns: [column({ dataType: 'Lookup', lookupRegistryDefId: 7 })] });
    const entries = new Map([[7, [entry()]]]);
    const columns = gridColumns(withLookup, false, NoLocalFlags, {}, noRequiredInput, new Map(), entries);
    const found = lookupColumnOf(columns);

    expect(found.editor).toBeTypeOf('function');
    expect(found.cellTemplate).toBeTypeOf('function');
  });

  /*
   * ✎ `X-13`: редактор більше не `<select>` зі збереженням на `onChange`, а
   * редактор-список (`listCellEditor.ts`) — поле пошуку й перелік, змонтовані
   * в `componentDidRender`. Тести нижче відкривають його так само, як
   * RevoGrid: `render` → `element` → `componentDidRender`.
   */
  it('редактор показує «очистити» і по одному варіанту на запис', () => {
    const entries = [entry({ id: 1, display: 'Казахстан' }), entry({ id: 2, code: 'UZ', display: 'Узбекистан' })];
    const withLookup = slice({ columns: [column({ dataType: 'Lookup', lookupRegistryDefId: 7 })] });
    const columns = gridColumns(
      withLookup, false, NoLocalFlags, {}, noRequiredInput, new Map(), new Map([[7, entries]]),
    );

    const node = openEditor(lookupColumnOf(columns).editor as EditorCtrCallable, undefined, vi.fn());

    expect(optionValues(node)).toEqual(['', '1', '2']);
    expect(optionLabels(node)).toEqual(['⟦grid.listClear⟧', 'Казахстан', 'Узбекистан']);
  });

  it('вибір запису шле в save() ідентифікатор запису, не текст показу', () => {
    const entries = [entry({ id: 5, display: 'Казахстан' })];
    const withLookup = slice({ columns: [column({ dataType: 'Lookup', lookupRegistryDefId: 7 })] });
    const columns = gridColumns(
      withLookup, false, NoLocalFlags, {}, noRequiredInput, new Map(), new Map([[7, entries]]),
    );
    const save = vi.fn();
    const node = openEditor(lookupColumnOf(columns).editor as EditorCtrCallable, undefined, save);

    press(node, 'ArrowDown');
    press(node, 'Enter');

    // ⚠ Рядок ідентифікатора: числом його робить `coerce(raw, 'Lookup')`.
    expect(save).toHaveBeenCalledWith('5', false);
    expect(save).not.toHaveBeenCalledWith('Казахстан', false);
    expect(coerce('5', 'Lookup')).toBe(5);
  });

  it('«очистити» шле порожній рядок — прибрати вибір, а не «нічого не сталося»', () => {
    const entries = [entry({ id: 5 })];
    const withLookup = slice({ columns: [column({ dataType: 'Lookup', lookupRegistryDefId: 7 })] });
    const columns = gridColumns(
      withLookup, false, NoLocalFlags, {}, noRequiredInput, new Map(), new Map([[7, entries]]),
    );
    const save = vi.fn();
    const node = openEditor(lookupColumnOf(columns).editor as EditorCtrCallable, 5, save);

    press(node, 'ArrowUp');
    press(node, 'Enter');

    expect(save).toHaveBeenCalledWith('', false);
    expect(coerce('', 'Lookup')).toBeNull();
  });

  it('поточне значення комірки — виділений варіант', () => {
    const entries = [entry({ id: 1, display: 'Казахстан' }), entry({ id: 2, code: 'UZ', display: 'Узбекистан' })];
    const withLookup = slice({ columns: [column({ dataType: 'Lookup', lookupRegistryDefId: 7 })] });
    const editor = lookupColumnOf(
      gridColumns(withLookup, false, NoLocalFlags, {}, noRequiredInput, new Map(), new Map([[7, entries]])),
    ).editor as EditorCtrCallable;

    openEditor(editor, 2, vi.fn());

    expect(document.querySelector('[aria-selected="true"]')?.getAttribute('data-value')).toBe('2');
  });

  it('cellTemplate показує entry.Display, не сирий ValueRegistryEntryId', () => {
    const entries = [entry({ id: 1, display: 'Казахстан' })];
    const withLookup = slice({ columns: [column({ dataType: 'Lookup', lookupRegistryDefId: 7 })] });
    const found = lookupColumnOf(
      gridColumns(withLookup, false, NoLocalFlags, {}, noRequiredInput, new Map(), new Map([[7, entries]])),
    );

    const template = found.cellTemplate as (h: unknown, props: { value?: unknown }) => unknown;

    expect(template(h, { value: 1 })).toBe('Казахстан');
    expect(template(h, { value: undefined })).toBe('');
  });

  it('перелік записів ще їде — редактор каже «завантаження», а не «порожньо», і не падає', () => {
    const withLookup = slice({ columns: [column({ dataType: 'Lookup', lookupRegistryDefId: 7 })] });
    // ⚠ Мапа БЕЗ ключа 7 і довідник у переліку тих, що ще їдуть: те, що бачить
    // перший рендер `DocumentGrid`, доки `useQueries` для записів у польоті.
    const found = lookupColumnOf(
      gridColumns(
        withLookup, false, NoLocalFlags, {}, noRequiredInput, new Map(), new Map(), new Map(), new Set([7]),
      ),
    );

    openEditor(found.editor as EditorCtrCallable, undefined, vi.fn());

    expect(document.body.textContent).toContain('grid.listLoading');
    expect((found.cellTemplate as (h: unknown, props: { value?: unknown }) => unknown)(h, { value: undefined })).toBe('');
  });

  it('довідник приїхав порожнім — «нічого не знайдено», а не «завантаження»', () => {
    const withLookup = slice({ columns: [column({ dataType: 'Lookup', lookupRegistryDefId: 7 })] });
    const found = lookupColumnOf(
      gridColumns(withLookup, false, NoLocalFlags, {}, noRequiredInput, new Map(), new Map([[7, []]])),
    );

    const node = openEditor(found.editor as EditorCtrCallable, undefined, vi.fn());

    expect(document.body.textContent).not.toContain('grid.listLoading');
    expect(optionValues(node)).toEqual(['']);
  });
});

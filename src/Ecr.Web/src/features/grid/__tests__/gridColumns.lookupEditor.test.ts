import { describe, expect, it, vi } from 'vitest';
import { h } from '@revolist/revogrid';
import type { ColumnDataSchemaModel, EditorBase, EditorCtrCallable, VNode } from '@revolist/revogrid';
import type { ColumnDto, RegistryEntryDto, TableSliceDto } from '@/api/types';
import { gridColumns } from '../DocumentGrid';
import { NoLocalFlags } from '../cellState';

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

function lookupColumnOf(columns: ReturnType<typeof gridColumns>) {
  const found = columns.find((c) => c.prop === 'C1');
  if (found === undefined) throw new Error('Немає колонки C1');
  return found;
}

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

  it('редактор рендерить <select> з порожньою опцією і по одній опції на запис', () => {
    const entries = [entry({ id: 1, display: 'Казахстан' }), entry({ id: 2, code: 'UZ', display: 'Узбекистан' })];
    const withLookup = slice({ columns: [column({ dataType: 'Lookup', lookupRegistryDefId: 7 })] });
    const columns = gridColumns(
      withLookup, false, NoLocalFlags, {}, noRequiredInput, new Map(), new Map([[7, entries]]),
    );
    const found = lookupColumnOf(columns);

    const editor = found.editor as EditorCtrCallable;
    const save = vi.fn();
    const close = vi.fn();
    const instance = editor(
      { prop: 'C1', model: { __rowKey: 'R1' }, value: undefined } as unknown as ColumnDataSchemaModel,
      save,
      close,
    );

    const vnode = renderOf(instance);
    expect(vnode.$tag$).toBe('select');

    // Порожня опція (плейсхолдер, "готово коли": "комірка без вибору
    // показує порожній плейсхолдер, не помилку") + по одній на запис.
    expect(vnode.$children$).toHaveLength(3);
    expect(vnode.$children$[0]?.$attrs$?.value).toBe('');
    expect(vnode.$children$[1]?.$attrs$?.value).toBe('1');
    expect(vnode.$children$[1]?.$children$?.[0]?.$text$).toBe('Казахстан');
    expect(vnode.$children$[2]?.$attrs$?.value).toBe('2');
    expect(vnode.$children$[2]?.$children$?.[0]?.$text$).toBe('Узбекистан');
  });

  it('вибір опції шле в save() ЧИСЛО entryId, не текст показу', () => {
    const entries = [entry({ id: 5, display: 'Казахстан' })];
    const withLookup = slice({ columns: [column({ dataType: 'Lookup', lookupRegistryDefId: 7 })] });
    const columns = gridColumns(
      withLookup, false, NoLocalFlags, {}, noRequiredInput, new Map(), new Map([[7, entries]]),
    );
    const found = lookupColumnOf(columns);
    const editor = found.editor as EditorCtrCallable;
    const save = vi.fn();

    const instance = editor(
      { prop: 'C1', model: { __rowKey: 'R1' }, value: undefined } as unknown as ColumnDataSchemaModel,
      save,
      vi.fn(),
    );
    const vnode = renderOf(instance);
    const onChange = vnode.$attrs$?.onChange as (event: Event) => void;

    onChange({ target: { value: '5' } } as unknown as Event);

    expect(save).toHaveBeenCalledWith(5);
    expect(save).not.toHaveBeenCalledWith('Казахстан');
    expect(save).not.toHaveBeenCalledWith('5');
  });

  it('вибір порожньої опції шле save(null) — прибрати вибір, а не "нічого не сталося"', () => {
    const entries = [entry({ id: 5 })];
    const withLookup = slice({ columns: [column({ dataType: 'Lookup', lookupRegistryDefId: 7 })] });
    const columns = gridColumns(
      withLookup, false, NoLocalFlags, {}, noRequiredInput, new Map(), new Map([[7, entries]]),
    );
    const editor = lookupColumnOf(columns).editor as EditorCtrCallable;
    const save = vi.fn();

    const instance = editor(
      { prop: 'C1', model: { __rowKey: 'R1' }, value: 5 } as unknown as ColumnDataSchemaModel,
      save,
      vi.fn(),
    );
    const onChange = renderOf(instance).$attrs$?.onChange as (event: Event) => void;

    onChange({ target: { value: '' } } as unknown as Event);

    expect(save).toHaveBeenCalledWith(null);
  });

  it('поточне значення комірки позначає відповідну опцію обраною', () => {
    const entries = [entry({ id: 1, display: 'Казахстан' }), entry({ id: 2, code: 'UZ', display: 'Узбекистан' })];
    const withLookup = slice({ columns: [column({ dataType: 'Lookup', lookupRegistryDefId: 7 })] });
    const editor = lookupColumnOf(
      gridColumns(withLookup, false, NoLocalFlags, {}, noRequiredInput, new Map(), new Map([[7, entries]])),
    ).editor as EditorCtrCallable;

    const instance = editor(
      { prop: 'C1', model: { __rowKey: 'R1' }, value: 2 } as unknown as ColumnDataSchemaModel,
      vi.fn(),
      vi.fn(),
    );
    const vnode = renderOf(instance);

    expect(vnode.$children$[0]?.$attrs$?.selected).toBe(false);
    expect(vnode.$children$[1]?.$attrs$?.selected).toBe(false);
    expect(vnode.$children$[2]?.$attrs$?.selected).toBe(true);
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

  it('колонка налаштована на Lookup, але перелік записів ще не завантажився — редактор не падає, показує лише плейсхолдер', () => {
    const withLookup = slice({ columns: [column({ dataType: 'Lookup', lookupRegistryDefId: 7 })] });
    // ⚠ Мапа БЕЗ ключа 7: те, що бачить перший рендер `DocumentGrid`, доки
    // `useQueries` для записів довідника ще в польоті.
    const found = lookupColumnOf(
      gridColumns(withLookup, false, NoLocalFlags, {}, noRequiredInput, new Map(), new Map()),
    );
    const editor = found.editor as EditorCtrCallable;

    const instance = editor(
      { prop: 'C1', model: { __rowKey: 'R1' }, value: undefined } as unknown as ColumnDataSchemaModel,
      vi.fn(),
      vi.fn(),
    );
    const vnode = renderOf(instance);

    expect(vnode.$children$).toHaveLength(1);
    expect((found.cellTemplate as (h: unknown, props: { value?: unknown }) => unknown)(h, { value: undefined })).toBe('');
  });
});

import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ColumnDataSchemaModel, EditCell, EditorBase } from '@revolist/revogrid';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { captureEdit, coerce } from '../edits';
import { gridColumns } from '../DocumentGrid';
import { NoLocalFlags } from '../cellState';

/**
 * `V-07`: логічна комірка.
 *
 * ⛔ `V-07`: `maybe` у колонці `Bool` мовчки ставав `false` і перезаписував
 * `True` (`aud.CellChange` id 75, `edits.ts`). Неприйнятне значення має
 * відхилятися з поясненням, як число, а не перетворюватися.
 */

function column(overrides: Partial<ColumnDto>): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'C1',
    dataType: 'Bool',
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

function slice(col: ColumnDto, value: unknown): TableSliceDto {
  return {
    tableInstanceId: 1,
    periodKey: 202609,
    columns: [col],
    rows: [
      { rowKey: 'R1', ordinal: 1, rowKind: 'Static', label: null, rowVersion: 'v1', cells: { C1: value }, isOrphaned: false },
    ],
    cellPermissions: {},
    cellConfirmations: {},
  };
}

const noRequiredInput = { blocked: new Map<string, string>(), warning: new Map<string, string>() };

/** Перелік редактора живе в `document.body` (`listCellEditor.ts`) — прибрати між тестами. */
afterEach(() => {
  for (const node of document.querySelectorAll('.ecr-list-editor-list, .ecr-test-host')) node.remove();
});

describe('V-07: логічна комірка не вигадує «ні»', () => {
  it('нерозпізнаний текст лишається текстом — сервер відповість 422 з назвою колонки', () => {
    expect(coerce('maybe', 'Bool')).toBe('maybe');
  });

  it('`maybe` поверх `True` — правка зі значенням `maybe`, а не `false`', () => {
    const captured = captureEdit(slice(column({}), true), { columnCode: 'C1', rowKey: 'R1', raw: 'maybe' });

    expect(captured?.pending.value).toBe('maybe');
  });

  it('відповіді «так»/«ні» мовами продукту та 1/0 розпізнаються', () => {
    for (const yes of ['true', 'TRUE', '1', 'yes', 'так', 'да']) expect(coerce(yes, 'Bool')).toBe(true);
    for (const no of ['false', '0', 'no', 'ні', 'нет']) expect(coerce(no, 'Bool')).toBe(false);
  });

  it('колонка Bool редагується переліком так/ні/порожньо і показує підпис, а не true/false', () => {
    const [grid] = gridColumns(slice(column({}), true), false, NoLocalFlags, {}, noRequiredInput);
    const template = grid?.cellTemplate as ((h: unknown, props: { value?: unknown }) => string) | undefined;

    // ✎ `X-13`: власні ключі сітки, а не ключі екрана порівняння версій.
    expect(template?.(null, { value: true })).toBe(t('grid.boolYes'));
    expect(template?.(null, { value: false })).toBe(t('grid.boolNo'));

    const factory = grid?.editor as
      | ((column: ColumnDataSchemaModel, save: (value?: unknown, preventFocus?: boolean) => void) => EditorBase)
      | undefined;
    expect(typeof factory).toBe('function');

    const save = vi.fn();
    const editor = factory?.({ value: true } as unknown as ColumnDataSchemaModel, save);
    const node = document.createElement('div');
    node.className = 'ecr-test-host';
    document.body.append(node);
    if (editor === undefined) throw new Error('немає редактора');

    editor.editCell = { val: true } as unknown as EditCell;
    editor.element = node;
    editor.componentDidRender?.();

    const values = [...document.querySelectorAll('[role="option"]')].map((item) => item.getAttribute('data-value'));
    expect(values).toEqual(['', 'true', 'false']);

    // Виділено поточне «так»; стрілка вниз — «ні», і лише Enter зберігає.
    const input = node.querySelector('input');
    input?.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }));
    expect(save).not.toHaveBeenCalled();

    input?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
    expect(save).toHaveBeenCalledWith('false', false);
    expect(coerce('false', 'Bool')).toBe(false);
  });
});

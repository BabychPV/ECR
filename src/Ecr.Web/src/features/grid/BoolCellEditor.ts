import type { ColumnDataSchemaModel, EditorBase, HyperFunc, VNode } from '@revolist/revogrid';
import { t } from '@/shared/i18n';

/**
 * Редактор логічної комірки (`CellDataType.Bool`, `V-07`).
 *
 * ⛔ Доти `Bool` редагувався звичайним текстовим полем, а `coerce` перетворював
 * будь-який нерозпізнаний текст на `false`: `maybe` мовчки перезаписував
 * `True`. Перелік із трьох варіантів не дає ввести те, чого не існує, —
 * помилку прибрано з дороги, а не лише перехоплено.
 *
 * ⚠ Три варіанти, а не два (і не прапорець): «не заповнювали» — окремий стан
 * (`coerce`: порожнє не стає `false`), і прапорець не вміє його показати.
 *
 * ⚠ Та сама форма, що й `LookupCellEditor` (функція-фабрика `EditorCtrCallable`),
 * і той самий шлях запису: `save(текст)` → `afteredit` → `coerce`. Вставка з
 * буфера цей редактор минає — її стереже `coerce` (нерозпізнане → `422`).
 */
export function createBoolCellEditor() {
  return (
    column: ColumnDataSchemaModel,
    save: (value?: unknown, preventFocus?: boolean) => void,
  ): EditorBase => {
    const current = boolOf(column.value);

    return {
      render(h: HyperFunc<VNode>): VNode {
        return h(
          'select',
          {
            class: 'ecr-bool-cell-editor',
            style: { width: '100%', height: '100%' },
            autoFocus: true,
            onChange: (event: Event) => {
              save((event.target as HTMLSelectElement).value);
            },
          },
          [
            h('option', { value: '', selected: current === null }, ''),
            h('option', { value: 'true', selected: current === true }, t('document.compareBoolYes')),
            h('option', { value: 'false', selected: current === false }, t('document.compareBoolNo')),
          ],
        );
      },
    };
  };
}

/**
 * Показ логічної комірки поза редагуванням: «Yes»/«No» мовою інтерфейсу.
 *
 * ⚠ Значення, яке не є логічним (нерозпізнаний текст, що чекає відмови
 * сервера), показується як є — оператор має бачити, що саме ввів.
 */
export function boolCellDisplay(value: unknown): string {
  const flag = boolOf(value);
  if (flag === true) return t('document.compareBoolYes');
  if (flag === false) return t('document.compareBoolNo');

  return value === null || value === undefined ? '' : String(value);
}

/** Логічне значення моделі; сітка може тримати і `true`, і `'true'`. */
function boolOf(value: unknown): boolean | null {
  if (typeof value === 'boolean') return value;
  if (value === 'true') return true;
  if (value === 'false') return false;

  return null;
}

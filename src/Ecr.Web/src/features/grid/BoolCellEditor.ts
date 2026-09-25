import { t } from '@/shared/i18n';
import { createListCellEditor, type ListOption } from './listCellEditor';

/**
 * Редактор логічної комірки (`CellDataType.Bool`, `V-07`, `X-13`).
 *
 * ⛔ `V-07`: доти `Bool` редагувався текстовим полем, а будь-який нерозпізнаний
 * текст ставав `false`. Перелік не дає ввести те, чого не існує.
 *
 * ✎ `X-13`: перелік був `<select>` зі збереженням на `onChange` — перша стрілка
 * одразу зберігала й закривала редактор. Тепер це спільний редактор-список
 * (`listCellEditor.ts`): стрілки переміщують, Enter чи клік підтверджує, друк
 * `y`/`n` шукає «Yes»/«No». Підписи — власні ключі сітки, а не ключі екрана
 * порівняння версій, звідки їх доти позичали.
 *
 * ⚠ Три варіанти, а не два: «не заповнювали» — окремий стан (`coerce`: порожнє
 * не стає `false`), і прапорець його не показав би.
 */
export function createBoolCellEditor() {
  return createListCellEditor(() => ({ options: boolOptions() }), t('grid.boolEditorLabel'), (value) => {
    const flag = boolOf(value);

    return flag === null ? null : String(flag);
  });
}

function boolOptions(): readonly ListOption[] {
  return [
    { value: null, label: t('grid.listClear') },
    { value: 'true', label: t('grid.boolYes') },
    { value: 'false', label: t('grid.boolNo') },
  ];
}

/**
 * Показ логічної комірки поза редагуванням: «Yes»/«No» мовою інтерфейсу.
 *
 * ⚠ Значення, яке не є логічним (нерозпізнаний текст, що чекає відмови
 * сервера), показується як є — оператор має бачити, що саме ввів.
 */
export function boolCellDisplay(value: unknown): string {
  const flag = boolOf(value);
  if (flag === true) return t('grid.boolYes');
  if (flag === false) return t('grid.boolNo');

  return value === null || value === undefined ? '' : String(value);
}

/** Логічне значення моделі; сітка може тримати і `true`, і `'true'`. */
function boolOf(value: unknown): boolean | null {
  if (typeof value === 'boolean') return value;
  if (value === 'true') return true;
  if (value === 'false') return false;

  return null;
}

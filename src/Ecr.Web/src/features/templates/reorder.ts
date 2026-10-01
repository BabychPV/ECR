/**
 * Перестановка колонок у конструкторі шаблону (`ФВ-2.6`): перетягуванням і
 * кнопками «вище/нижче» як доступною альтернативою (WCAG 2.5.7, 2.1.1).
 *
 * ⚠ Порядок колонки — поле презентаційного шару (`ColumnDef.Ordinal`,
 * `ChangeClassifier.PresentationFields`), тож запис іде тим самим
 * `PATCH …/presentation`, що й діалог «Оформлення» (`PresentationEditor.tsx`):
 * законний і в чернетці, і в опублікованій версії (`ФВ-7.2`), одним пакетом,
 * який сервер застосовує в одній транзакції. Нового ендпоінта перестановка не
 * вимагає.
 *
 * Рядки фіксованої таблиці переставляються так само (AN-15): `RowDef.Ordinal` у
 * білому списку презентаційного патча (`TemplateVersionStore.PresentationColumns`,
 * `("RowDef","Ordinal")`), патч не чіпає `LabelL10n`, тож переклади цілі. Різниця
 * лише в `entityType` (`orderPatch`).
 */

/** Одна зміна презентаційного шару, як її приймає `PATCH …/presentation`. */
export interface PresentationChange {
  readonly entityType: string;
  readonly entityId: number;
  readonly field: string;
  readonly value: string | null;
}

/** Колонка в тому порядку, в якому її показує структура версії. */
export interface Ordered {
  readonly id: number;
  readonly ordinal: number;
}

/** Копія `items`, де елемент із позиції `from` стоїть на позиції `to`. */
export function moveItem<T>(items: readonly T[], from: number, to: number): T[] {
  const result = [...items];
  if (from === to || from < 0 || to < 0 || from >= items.length || to >= items.length) return result;

  const [moved] = result.splice(from, 1);
  if (moved !== undefined) result.splice(to, 0, moved);

  return result;
}

/**
 * Нові значення `Ordinal` після переміщення `from → to` — ЛИШЕ для колонок,
 * у яких воно справді змінюється.
 *
 * ⚠ Позиції отримують ТОЙ САМИЙ набір значень, що вже був (відсортований):
 * «дірки» в нумерації (0, 10, 20) і місце таблиці відносно інших лишаються
 * як були, а змінюються лише колонки між `from` і `to`. Патч із зайвими
 * рядками писав би в аудит зміни, яких не було (див. `PresentationEditor`).
 *
 * ⛔ Якщо значення повторюються, та сама схема дала б двом колонкам однаковий
 * порядок — і перестановка між ними була б непомітною. Тоді нумерація
 * вирівнюється до `0…n−1`.
 */
export function ordinalChanges(
  items: readonly Ordered[],
  from: number,
  to: number,
): { readonly id: number; readonly ordinal: number }[] {
  const moved = moveItem(items, from, to);
  const sorted = items.map((item) => item.ordinal).sort((a, b) => a - b);
  const unique = new Set(sorted).size === sorted.length;

  const changes: { id: number; ordinal: number }[] = [];
  moved.forEach((item, index) => {
    const ordinal = unique ? (sorted[index] ?? index) : index;
    if (ordinal !== item.ordinal) changes.push({ id: item.id, ordinal });
  });

  return changes;
}

/** Сутності, порядок яких міняє `PATCH …/presentation`. */
export type OrderedEntity = 'ColumnDef' | 'RowDef';

/** Пакет `PATCH …/presentation` для перестановки колонок або рядків. */
export function orderPatch(
  entityType: OrderedEntity,
  changes: readonly { readonly id: number; readonly ordinal: number }[],
): PresentationChange[] {
  return changes.map((change) => ({
    entityType,
    entityId: change.id,
    field: 'Ordinal',
    value: String(change.ordinal),
  }));
}

/** Пакет `PATCH …/presentation` для перестановки колонок. */
export function columnOrderPatch(
  changes: readonly { readonly id: number; readonly ordinal: number }[],
): PresentationChange[] {
  return orderPatch('ColumnDef', changes);
}

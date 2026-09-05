import type { ColumnDto, TableSliceDto } from '@/api/types';

/**
 * Права по комірках приходять **із сервера** і показуються, а не вгадуються.
 *
 * ⚠ Клієнт не повторює правил доступу. Спроба порахувати їх тут дала б другу
 * реалізацію RBAC, стану періоду і статусу документа — і рано чи пізно вона
 * показала б комірку редагованою там, де сервер відмовить, або сірою там, де
 * дозволить. Обидва випадки виглядають як помилка системи, а не як права.
 */

/** Чому комірку не можна редагувати. */
export type DenyReason =
  | 'Calculated'
  | 'ReadOnlyColumn'
  | 'NoPermission'
  | 'PeriodClosed'
  | 'DocumentSubmitted'
  | 'Orphaned';

/** Рішення про комірку. */
export interface CellDecision {
  editable: boolean;
  reason: DenyReason | null;
  /** Текст для підказки; порожній, якщо редагування дозволене. */
  hint: string;
}

const Editable: CellDecision = { editable: true, reason: null, hint: '' };

/**
 * Тексти причин.
 *
 * ⚠ Підказка називає ПРИЧИНУ, а не «недоступно». Користувач, який бачить сіру
 * комірку без пояснення, іде до адміністратора, а той — до розробника; це
 * дорожче за будь-який рядок тексту.
 */
const Hints: Record<DenyReason, string> = {
  Calculated: 'Комірку рахує система: значення зміниться при наступному перерахунку.',
  ReadOnlyColumn: 'Колонка доступна лише для читання за описом шаблону.',
  NoPermission: 'Немає права редагувати цю комірку.',
  PeriodClosed: 'Період закрито: зміни потребують окремого погодження.',
  DocumentSubmitted: 'Документ подано: спершу потрібне повернення в роботу.',
  Orphaned: 'Рядок посилається на запис реєстру, що втратив чинність.',
};

/** Ключ комірки у словнику прав, який віддає сервер. */
export function cellKey(rowKey: string, columnCode: string): string {
  return `${rowKey}:${columnCode}`;
}

/**
 * Рішення про комірку.
 *
 * Порядок перевірок значущий: спершу те, що не залежить від користувача
 * (обчислена колонка), потім те, що залежить. Інакше власник усіх прав бачив
 * би «немає права» на комірці, яку не може редагувати ніхто.
 */
export function decide(slice: TableSliceDto, rowKey: string, column: ColumnDto): CellDecision {
  if (column.dataType === 'Formula' || column.dataType === 'Calculated') {
    return deny('Calculated');
  }

  if (column.isReadOnly) return deny('ReadOnlyColumn');

  const permission = slice.cellPermissions[cellKey(rowKey, column.code)];

  // ⚠ Відсутність запису — це ДОЗВІЛ. Сервер віддає лише відхилення: словник
  // на 500×60 із дозволами на кожну комірку важив би більше за самі дані.
  if (permission === undefined || permission === 'Allow') return Editable;

  return deny(reasonOf(permission));
}

/** Причина, яку віддав сервер; невідома трактується як відсутність права. */
function reasonOf(permission: string): DenyReason {
  switch (permission) {
    case 'PeriodClosed':
      return 'PeriodClosed';
    case 'DocumentSubmitted':
      return 'DocumentSubmitted';
    case 'Calculated':
      return 'Calculated';
    case 'Orphaned':
      return 'Orphaned';
    case 'ReadOnlyColumn':
      return 'ReadOnlyColumn';
    default:
      // ⛔ Невідома причина НЕ означає «можна». Нова причина на сервері
      // інакше відкривала б редагування там, де його щойно заборонили.
      return 'NoPermission';
  }
}

function deny(reason: DenyReason): CellDecision {
  return { editable: false, reason, hint: Hints[reason] };
}

/**
 * Сторож для вставки: повертає причину відмови або <c>null</c>.
 *
 * Це той самий предикат, який використовує планувальник вставки — щоб
 * «комірка сіра» і «сюди не вставиться» ніколи не розходилися.
 */
export function guardOf(slice: TableSliceDto): (rowKey: string, columnCode: string) => string | null {
  const columns = new Map(slice.columns.map((column) => [column.code, column]));

  return (rowKey, columnCode) => {
    const column = columns.get(columnCode);
    if (column === undefined) return 'Колонки немає в цій таблиці.';

    const decision = decide(slice, rowKey, column);

    return decision.editable ? null : decision.hint;
  };
}

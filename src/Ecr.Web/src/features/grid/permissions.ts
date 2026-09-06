import type { ColumnDto, TableSliceDto } from '@/api/types';
import { t } from '@/shared/i18n';

/**
 * Права по комірках приходять **із сервера** і показуються, а не вгадуються.
 *
 * ⚠ Клієнт не повторює правил доступу. Спроба порахувати їх тут дала б другу
 * реалізацію RBAC, стану періоду і статусу документа — і рано чи пізно вона
 * показала б комірку редагованою там, де сервер відмовить, або сірою там, де
 * дозволить. Обидва випадки виглядають як помилка системи, а не як права.
 */

/**
 * Чому комірку не можна редагувати.
 *
 * ⛔ Перелік **дослівно повторює** `Ecr.Domain.Enums.EditDenyReason`, і це
 * перевіряє архітектурний тест
 * `Кожна_причина_заборони_має_підказку_на_клієнті`. До аудиту (`A7-02`) тут
 * було п'ять власних назв, з яких три не збігалися з серверними
 * (`ReadOnlyColumn` проти `ColumnReadOnly`, `Calculated` проти
 * `CalculatedCell`, вигаданий `Orphaned`). Наслідок: **жодна** причина не
 * розпізнавалася, усі падали в запасний варіант, і користувач на кожну сіру
 * комірку бачив «немає права» — навіть коли причина була в закритому періоді
 * або в поданому документі.
 */
export type DenyReason =
  | 'NoGrant'
  | 'PeriodNotOpenYet'
  | 'PeriodClosed'
  | 'OutOfAccessWindow'
  | 'DocumentSubmitted'
  | 'DocumentApproved'
  | 'ColumnReadOnly'
  | 'RowReadOnly'
  | 'CalculatedCell'
  | 'ProjectArchived'
  | 'ArchivingInProgress'
  | 'BusinessRule'
  | 'SimulationReadOnly'
  | 'OutsidePermitWindow';

/** Рішення про комірку. */
export interface CellDecision {
  editable: boolean;
  reason: DenyReason | null;
  /** Текст для підказки; порожній, якщо редагування дозволене. */
  hint: string;
}

const Editable: CellDecision = { editable: true, reason: null, hint: '' };

/**
 * Ключі текстів причин.
 *
 * ⚠ Підказка називає ПРИЧИНУ, а не «недоступно». Користувач, який бачить сіру
 * комірку без пояснення, іде до адміністратора, а той — до розробника; це
 * дорожче за будь-який рядок тексту.
 *
 * ⛔ Тут КЛЮЧІ, а не тексти. До `A7-12` таблиця містила готові українські
 * рядки — тобто словник у бандлі, який D-95 забороняє прямо: додати мову
 * означало б перезібрати клієнт. Значення приходять із каталогу разом з
 * рештою інтерфейсу.
 */
const Hints: Record<DenyReason, string> = {
  NoGrant: 'deny.NoGrant',
  PeriodNotOpenYet: 'deny.PeriodNotOpenYet',
  PeriodClosed: 'deny.PeriodClosed',
  OutOfAccessWindow: 'deny.OutOfAccessWindow',
  DocumentSubmitted: 'deny.DocumentSubmitted',
  DocumentApproved: 'deny.DocumentApproved',
  ColumnReadOnly: 'deny.ColumnReadOnly',
  RowReadOnly: 'deny.RowReadOnly',
  CalculatedCell: 'deny.CalculatedCell',
  ProjectArchived: 'deny.ProjectArchived',
  ArchivingInProgress: 'deny.ArchivingInProgress',
  BusinessRule: 'deny.BusinessRule',
  SimulationReadOnly: 'deny.SimulationReadOnly',
  OutsidePermitWindow: 'deny.OutsidePermitWindow',
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
    return deny('CalculatedCell');
  }

  if (column.isReadOnly) return deny('ColumnReadOnly');

  const permission = slice.cellPermissions[cellKey(rowKey, column.code)];

  // ⚠ Відсутність запису — це ДОЗВІЛ. Сервер віддає лише відхилення: словник
  // на 500×60 із дозволами на кожну комірку важив би більше за самі дані.
  if (permission === undefined || permission === 'None') return Editable;

  const reason = reasonOf(permission);

  return reason === null
    ? { editable: false, reason: null, hint: t('deny.Unknown', { reason: permission }) }
    : deny(reason);
}

/**
 * Причина, яку віддав сервер.
 *
 * ⛔ Невідома назва НЕ означає «можна» і не означає «немає права»: це нова
 * причина, якої клієнт ще не знає. Показати замість неї «немає права» —
 * збрехати користувачеві; тому запасний варіант має власний текст, а
 * архітектурний тест не дає переліку розійтися з сервером.
 */
function reasonOf(permission: string): DenyReason | null {
  return permission in Hints ? (permission as DenyReason) : null;
}

function deny(reason: DenyReason): CellDecision {
  // ⚠ Текст береться в момент рішення, а не при завантаженні модуля: каталог
  // приходить із сервера пізніше за імпорти, і таблиця, обчислена наперед,
  // назавжди зафіксувала б самі ключі.
  return { editable: false, reason, hint: t(Hints[reason]) };
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
    if (column === undefined) return t('grid.unknownColumn', { column: columnCode });

    const decision = decide(slice, rowKey, column);

    return decision.editable ? null : decision.hint;
  };
}

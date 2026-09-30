import { t } from '@/shared/i18n';

/**
 * Людські підписи значень переліків структури шаблону (X-16, четвертий раунд
 * UX): тип даних колонки, вид і спосіб формування рядків, вид і клас зміни в
 * порівнянні версій, вид і поведінка правила доступу до періоду.
 *
 * ⛔ Доти екрани друкували значення переліку як є — `Decimal`, `Fixed`,
 * `Breaking`, `EditablePeriodOnly`: слово з коду розробника, однакове для
 * будь-якої мови інтерфейсу.
 *
 * ⚠ Ключі — літерали в кожній гілці, а не шаблон зі значенням: так їх бачить
 * сторож каталогу (`EndpointCoverageTests`) і вимагає рядок сіду — той самий
 * вибір, що в `UsageKindLabel`, `TableEditor.rowModeLabel`.
 *
 * ⚠ Невідоме значення (новий член переліку на сервері раніше за клієнт) —
 * саме значення, а не порожньо: людина має бачити, ЩО там стоїть.
 */

/** Тип даних колонки / поля (`CellDataType`). */
export function dataTypeLabel(value: string): string {
  switch (value) {
    case 'String':
      return t('enum.dataType.String');
    case 'Int':
      return t('enum.dataType.Int');
    case 'Decimal':
      return t('enum.dataType.Decimal');
    case 'Bool':
      return t('enum.dataType.Bool');
    case 'Date':
      return t('enum.dataType.Date');
    case 'Lookup':
      return t('enum.dataType.Lookup');
    case 'Unit':
      return t('enum.dataType.Unit');
    case 'Formula':
      return t('enum.dataType.Formula');
    case 'Calculated':
      return t('enum.dataType.Calculated');
    default:
      return value;
  }
}

/** Спосіб формування рядків таблиці (`TableRowMode`) — наявні ключі `TableEditor`. */
export function rowModeLabel(value: string): string {
  switch (value) {
    case 'Fixed':
      return t('tableDef.rowModeFixed');
    case 'Dynamic':
      return t('tableDef.rowModeDynamic');
    case 'Mixed':
      return t('tableDef.rowModeMixed');
    default:
      return value;
  }
}

/** Вид рядка (`RowKind`). */
export function rowKindLabel(value: string): string {
  switch (value) {
    case 'Group':
      return t('enum.rowKind.Group');
    case 'Item':
      return t('enum.rowKind.Item');
    case 'Balance':
      return t('enum.rowKind.Balance');
    case 'Note':
      return t('enum.rowKind.Note');
    case 'Header':
      return t('enum.rowKind.Header');
    default:
      return value;
  }
}

/** Вид зміни в порівнянні версій (`TemplateChangeDto.kind`). */
export function diffKindLabel(value: string): string {
  switch (value) {
    case 'Added':
      return t('enum.diffKind.Added');
    case 'Removed':
      return t('enum.diffKind.Removed');
    case 'Modified':
      return t('enum.diffKind.Modified');
    case 'Presentation':
      return t('enum.diffKind.Presentation');
    default:
      return value;
  }
}

/** Клас ризику зміни (`ChangeClass`). */
export function changeClassLabel(value: string): string {
  switch (value) {
    case 'Safe':
      return t('enum.changeClass.Safe');
    case 'Presentation':
      return t('enum.changeClass.Presentation');
    case 'Guarded':
      return t('enum.changeClass.Guarded');
    case 'Breaking':
      return t('enum.changeClass.Breaking');
    default:
      return value;
  }
}

/** Вид правила доступу до періоду (`PeriodAccessRuleKind`). */
export function periodRuleKindLabel(value: string): string {
  switch (value) {
    case 'AlwaysReadOnly':
      return t('enum.periodRuleKind.AlwaysReadOnly');
    case 'HeaderRows':
      return t('enum.periodRuleKind.HeaderRows');
    case 'EditablePeriodOnly':
      return t('enum.periodRuleKind.EditablePeriodOnly');
    case 'RelativeWindow':
      return t('enum.periodRuleKind.RelativeWindow');
    case 'SourceWindow':
      return t('enum.periodRuleKind.SourceWindow');
    case 'Expression':
      return t('enum.periodRuleKind.Expression');
    default:
      return value;
  }
}

/** Поведінка поза вікном доступу (`OutOfWindowBehavior`). */
export function outOfWindowLabel(value: string): string {
  switch (value) {
    case 'ReadOnly':
      return t('enum.outOfWindow.ReadOnly');
    case 'Warn':
      return t('enum.outOfWindow.Warn');
    case 'AllowWithConfirmation':
      return t('enum.outOfWindow.AllowWithConfirmation');
    case 'Hide':
      return t('enum.outOfWindow.Hide');
    default:
      return value;
  }
}

import { t } from '@/shared/i18n';

/*
 * Підписи порівняння «PI каже / у комірці» і причин (`D-465`, `PiValueComparison` / `PiValueReason` сервера).
 *
 * ⛔ Кожна гілка — ЛІТЕРАЛ ключа: сторож каталогу бачить лише літерали, ключ із шаблону пройшов би повз нього.
 */

/** Порівняння значення мовою інтерфейсу; невідоме — як є. */
export function comparisonLabel(comparison: string): string {
  switch (comparison) {
    case 'Match':
      return t('piEvents.compareMatch');
    case 'ManualEdit':
      return t('piEvents.compareManualEdit');
    case 'NotWritten':
      return t('piEvents.compareNotWritten');
    case 'NotUpdated':
      return t('piEvents.compareNotUpdated');
    case 'NoSourceValue':
      return t('piEvents.compareNoSourceValue');
    case 'NotComparable':
      return t('piEvents.compareNotComparable');
    case 'Hidden':
      return t('piEvents.compareHidden');
    default:
      return comparison;
  }
}

/** Колір бейджа порівняння (семантичні кольори теми — обидві теми). */
export function comparisonColor(comparison: string): string {
  switch (comparison) {
    case 'Match':
      return 'statusSuccess';
    case 'ManualEdit':
    case 'NotUpdated':
    case 'NotWritten':
      return 'statusWarning';
    default:
      return 'gray';
  }
}

/** Причина мовою інтерфейсу; невідома — як є. */
export function reasonLabel(reason: string): string {
  switch (reason) {
    case 'Open':
      return t('piEvents.reasonOpen');
    case 'PeriodClosed':
      return t('piEvents.reasonPeriodClosed');
    case 'PeriodNotOpen':
      return t('piEvents.reasonPeriodNotOpen');
    case 'SheetLocked':
      return t('piEvents.reasonSheetLocked');
    case 'RowLimit':
      return t('piEvents.reasonRowLimit');
    case 'Unmapped':
      return t('piEvents.reasonUnmapped');
    case 'PeriodChanged':
      return t('piEvents.reasonPeriodChanged');
    case 'OpenAtPeriodClose':
      return t('piEvents.reasonOpenAtPeriodClose');
    case 'KeptManual':
      return t('piEvents.reasonKeptManual');
    case 'Missing':
      return t('piEvents.reasonMissing');
    case 'Pending':
      return t('piEvents.reasonPending');
    default:
      return reason;
  }
}

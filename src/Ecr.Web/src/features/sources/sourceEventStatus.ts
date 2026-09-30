import { t } from '@/shared/i18n';
import type { SourceEventLinkStatus } from './sourceEventsApi';

/**
 * Стани зв'язку «подія джерела ↔ рядок документа» (`ext.SourceEventLink.Status`, FEATURE-HSE301-VIEW §4.7.4) у
 * порядку показу у фільтрі.
 *
 * ⚠ Той самий перелік, що `SourceEventLinkStatus` у `schema.d.ts`: `satisfies` нижче не дасть пропустити новий
 * стан мовчки — компілятор вимагатиме його і тут, і в обох `switch`.
 */
export const SourceEventStatuses = [
  'Synced',
  'Open',
  'Missing',
  'PeriodClosed',
  'PeriodChanged',
  'PeriodNotOpen',
  'Unmapped',
  'RowLimit',
] as const satisfies readonly SourceEventLinkStatus[];

/**
 * Назва стану мовою інтерфейсу.
 *
 * ⛔ Кожна гілка — ЛІТЕРАЛ ключа, а не `t(\`sourceEvents.status.${status}\`)`: сторож
 * `Кожен_рядок_якого_просить_клієнт_є_в_каталозі` бачить лише літерали, а ключ, зібраний шаблоном, пройшов би
 * повз нього без рядка в сіді.
 */
export function statusLabel(status: SourceEventLinkStatus): string {
  switch (status) {
    case 'Synced':
      return t('sourceEvents.status.Synced');
    case 'Open':
      return t('sourceEvents.status.Open');
    case 'Missing':
      return t('sourceEvents.status.Missing');
    case 'PeriodClosed':
      return t('sourceEvents.status.PeriodClosed');
    case 'PeriodChanged':
      return t('sourceEvents.status.PeriodChanged');
    case 'PeriodNotOpen':
      return t('sourceEvents.status.PeriodNotOpen');
    case 'Unmapped':
      return t('sourceEvents.status.Unmapped');
    case 'RowLimit':
      return t('sourceEvents.status.RowLimit');
  }
}

/** Що стан означає для рядка документа і що з ним робити (підказка бейджа). */
export function statusExplanation(status: SourceEventLinkStatus): string {
  switch (status) {
    case 'Synced':
      return t('sourceEvents.statusHint.Synced');
    case 'Open':
      return t('sourceEvents.statusHint.Open');
    case 'Missing':
      return t('sourceEvents.statusHint.Missing');
    case 'PeriodClosed':
      return t('sourceEvents.statusHint.PeriodClosed');
    case 'PeriodChanged':
      return t('sourceEvents.statusHint.PeriodChanged');
    case 'PeriodNotOpen':
      return t('sourceEvents.statusHint.PeriodNotOpen');
    case 'Unmapped':
      return t('sourceEvents.statusHint.Unmapped');
    case 'RowLimit':
      return t('sourceEvents.statusHint.RowLimit');
  }
}

/**
 * Колір бейджа: лише токени теми (`W4.2`) і нейтральний сірий.
 *
 * ⚠ `Missing`, `PeriodChanged`, `Unmapped` — увага людини (рядок лишився, але розійшовся з PI); `RowLimit` —
 * подію не записано взагалі; `Open`, `PeriodNotOpen`, `PeriodClosed` — очікуваний стан, який дії не просить.
 */
export function statusColor(status: SourceEventLinkStatus): string {
  switch (status) {
    case 'Synced':
      return 'statusSuccess';
    case 'Missing':
    case 'PeriodChanged':
    case 'Unmapped':
      return 'statusWarning';
    case 'RowLimit':
      return 'statusError';
    case 'Open':
    case 'PeriodNotOpen':
    case 'PeriodClosed':
      return 'gray';
  }
}

import { displayPaddingOf, formatDate, formatDecimal } from '@/shared/format';
import { localized, type LocalizedText } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import { isOpaqueRowKey } from '@/features/grid/rowKeyText';

/** Типи колонок, чиє значення — число (`U-05`: одне правило подачі числа). */
export const NumericTypes: readonly string[] = ['Decimal', 'Int', 'Formula', 'Calculated'];

/**
 * Значення журналу — за правилом показу, а не у форматі сховища (`X-35`).
 *
 * ⛔ Журнал показував «Was 53.1771000000000000»: `aud.CellChange` зберігає
 * число з повним масштабом колонки сховища (`decimal(34,16)`, `D-148`).
 * Людина набирала `53.1771` і шукає в журналі саме його. Хвостові нулі
 * прибирає той самий `formatDecimal`, що й сітка (`U-05`/`U-24`), розряди —
 * мовою інтерфейсу.
 *
 * ⚠ Нерозібране значення показується ЯК Є: журнал — доказ, і сховати дивне
 * значення за «—» означало б сховати саму розбіжність.
 *
 * ⛔ `scale` — масштаб колонки (`columnScale` відповіді): НЕ МЕНШЕ стількох знаків
 * після коми — те саме правило й той самий виклик `formatDecimal(…, scale)`, що й
 * `cellDisplay` сітки (`U-05`). Без нього комірка показувала `12.50`, а журнал — `12.5`.
 * Довший дріб не обрізається (нижня межа, як у сітці).
 */
export function auditValueText(
  value: string | null | undefined,
  dataType: string | null | undefined,
  scale?: number | null,
  /**
   * ✎ DOCUX (огляд UI 27.11): довідникова колонка — назва запису від сервера (`oldValueLabelL10n`/`newValueLabelL10n`), як у
   * самій комірці; id запису лишається в підказці викликача. Немає назви (запис видалено) — як доти, сирий id.
   */
  label?: LocalizedText | null,
): string {
  if (value === null || value === undefined) return '—';
  const named = label === undefined || label === null ? '' : localized(label);
  if (named.length > 0) return named;
  if (dataType === null || dataType === undefined) return value;

  if (NumericTypes.includes(dataType)) {
    return formatDecimal(value, undefined, undefined, displayPaddingOf(scale)) ?? value;
  }

  if (dataType === 'Date') {
    const day = /^\d{4}-\d{2}-\d{2}/.exec(value)?.[0];
    const shown = day === undefined ? '' : formatDate(day);

    return shown.length > 0 ? shown : value;
  }

  return value;
}

/**
 * ✎ audit-filters-followup AC-12: рядок комірки для людини. Джерело мітки — `cfg.RowDef.LabelL10n` фіксованого рядка
 * шаблону (`rowLabelL10n` рядка журналу); у динамічного (доданого людиною) рядка мітки немає ніде, а його ключ —
 * GUID. Тоді «Row» + початок ключа (повний — у підказці), а не 32 символи. Короткі коди («LTD-A02») — як були.
 * ✎ AC-13: для такого рядка — його номер на екрані «Row N» (`rowOrdinal`, `doc.TableRow.Ordinal`); номера немає (рядок
 * видалено, період в архіві) — як раніше, «Row» + початок ключа.
 */
export function rowText(change: { readonly rowKey: string; readonly rowLabelL10n?: LocalizedText | null; readonly rowOrdinal?: number | null }): string {
  const label = localized(change.rowLabelL10n);
  if (label.length > 0) return label;
  const ordinal = change.rowOrdinal;
  // ✎ DOCUX (огляд UI 27.11): рядок без мітки — «Row N», ЯК У РЯДКУ ФОРМУЛИ й інспекторі, для будь-якого ключа: згенерований
  // ключ динамічного рядка («p1-T3_00-0020») — теж не код для людини. Ключ лишається в підказці рядка.
  if (typeof ordinal === 'number') return `${t('grid.rowLabelHeader')} ${String(ordinal)}`;
  if (isOpaqueRowKey(change.rowKey)) return `${t('grid.rowLabelHeader')} ${change.rowKey.slice(0, 8)}…`;

  return change.rowKey;
}

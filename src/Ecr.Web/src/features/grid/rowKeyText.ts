import { t } from '@/shared/i18n';

/** Ключ рядка, доданого користувачем: GUID без дефісів (32 hex) — людині нічого не каже. */
export const OpaqueRowKey = /^[0-9a-f]{24,}$/i;

/** Чи це службовий ключ (GUID), а не короткий код рядка шаблону («LTD-A01»). */
export function isOpaqueRowKey(rowKey: string): boolean {
  return OpaqueRowKey.test(rowKey);
}

/**
 * ✎ integ-row-label-messages: рядок для людини за ключем. Короткий код («LTD-A02») — як є; службовий GUID — «Row N»
 * (номер на екрані, 1-based), а без номера — «this row»: сирі 32 символи в тексті не показуємо ніколи.
 */
export function rowKeyText(rowKey: string, ordinal?: number | null): string {
  if (!isOpaqueRowKey(rowKey)) return rowKey;

  return typeof ordinal === 'number' ? `${t('grid.rowLabelHeader')} ${String(ordinal)}` : t('permits.history.rowUnknown');
}

/**
 * ✎ DOCUX: підпис рядка на ПОЧАТКУ рядка чи чипа — з великої літери («Row 18»). Той самий ключ «row {n}» стоїть і посеред
 * речення (історія дозволів), тож каталог лишається малим, а великою робить місце показу.
 */
export function atLineStart(text: string): string {
  return text.length === 0 ? text : text.charAt(0).toLocaleUpperCase() + text.slice(1);
}

/** Підстановки серверних повідомлень, у яких лежать ключі рядків (`RowKeyText.KeyParameters` сервера). */
const RowKeyParams: readonly string[] = ['rowKey', 'rowKeys', 'sourceRow', 'row'];

/** Службовий ключ усередині тексту (перелік через кому): межі — не літера/цифра. */
const OpaqueRowKeyInText = /(?<![0-9A-Za-z])[0-9a-f]{24,}(?![0-9A-Za-z])/gi;

/**
 * Підстановки для шаблону каталогу: службові ключі рядків у `rowKey`/`rowKeys`/`sourceRow` замінено позначкою
 * «(added by a user)» (`row.addedByUser`) — як це робить сервер (`RowKeyText.Humanize`); решта — без змін.
 */
export function humanRowKeyParams(params: Record<string, string>): Record<string, string> {
  let result: Record<string, string> | null = null;
  for (const name of RowKeyParams) {
    const value = params[name];
    if (value === undefined || !new RegExp(OpaqueRowKeyInText.source, 'i').test(value)) continue;

    result ??= { ...params };
    result[name] = value.replace(OpaqueRowKeyInText, t('row.addedByUser'));
  }

  return result ?? params;
}

import { parseDateOnly } from '@/shared/format/dateOnly';
import { formatDate } from '@/shared/format/datetime';
import { formatNumber } from '@/shared/format/number';
import { formatPeriodKey } from '@/shared/format/periodKey';
import { t } from '@/shared/i18n';
import type { PermitUsage, PermitUsageItem } from '../api/usagesApi';
import { localName } from '../permit/names';

/*
 * Чиста модель вкладки «Used in» картки дозволу (PERMIT-USAGE, п. 3 плану): адреса місця в документі, підписи
 * «де» і «яке поле», варіанти фільтра періоду. Нічого з React і мережі — перевіряється без рендера.
 */

/**
 * Вид місця з контракту (`PermitUsageKinds`): поле шапки, комірка рядка або запис історії дозволів (B128c: сегмент «з дати»
 * поля шапки чи рядка, частина поділеного місяця; `tableCode === null` — запис поля шапки).
 */
export const UsageKind = { Header: 'header', Cell: 'cell', History: 'history' } as const;

/** Місце в рядку таблиці: комірка або запис історії рядка (не шапки). */
function isRowPlace(item: PermitUsageItem): boolean {
  return item.kind === UsageKind.Cell || (item.kind === UsageKind.History && item.tableCode !== null);
}

/** Параметр адреси вкладки з фільтром періоду (`?tab=usage&period=202606`). */
export const UsagePeriodParam = 'period';

/** Ключ рядка таблиці: документ + вид + період + аркуш/таблиця/рядок + поле (одне місце - один рядок). */
export function usageKey(item: PermitUsageItem): string {
  return [
    item.documentId,
    item.kind,
    item.periodKey ?? '',
    item.sheetCode ?? '',
    item.tableCode ?? '',
    item.rowKey ?? '',
    item.fieldCode,
    item.validFrom ?? '',
  ].join('|');
}

/**
 * Адреса місця в документі. Шапка належить документу цілком - лише документ. Комірка - період, аркуш і таблиця
 * в адресі (`ФВ-14.29`: посилання без них відкриває інший період і інший аркуш, ніж той, про який ішлося).
 *
 * ⛔ Літерал, а не `generatePath`: `features` не імпортують `app` (`ClientLayerRulesTests`).
 */
export function usageHref(item: PermitUsageItem): string {
  const base = `/documents/${String(item.documentId)}`;
  if (!isRowPlace(item)) return base;

  const params = new URLSearchParams();
  if (item.periodKey !== null) params.set('periodKey', String(item.periodKey));
  if (item.sheetCode !== null) params.set('sheet', item.sheetCode);
  if (item.tableCode !== null) params.set('table', item.tableCode);

  return params.size > 0 ? `${base}?${params.toString()}` : base;
}

/** «Де»: «Document header» для шапки; «аркуш · таблиця» для комірки (назви мовою користувача, інакше коди). */
export function wherePrimary(item: PermitUsageItem): string {
  if (!isRowPlace(item)) return t('permits.usage.header');

  const sheet = (item.sheetNameL10n !== null ? localName(item.sheetNameL10n) : '') || (item.sheetCode ?? '');
  const table = (item.tableNameL10n !== null ? localName(item.tableNameL10n) : '') || (item.tableCode ?? '');

  return [sheet, table].filter((part) => part !== '').join(' · ');
}

/**
 * Другий рядок «де»: номер рядка, як його рахує людина (`Row 1` для першого); для шапки - нічого.
 * ⚠ `rowOrdinal` сервера рахується від 0 (живий стенд: перший рядок динамічної таблиці - 0), тож показується +1.
 */
export function whereSecondary(item: PermitUsageItem): string | null {
  if (item.kind === UsageKind.History) {
    const row = item.tableCode === null ? null : rowText(item);

    return [row, historyFromText(item)].filter((part): part is string => part !== null && part !== '').join(' · ');
  }
  if (item.kind !== UsageKind.Cell) return null;

  return rowText(item);
}

function rowText(item: PermitUsageItem): string | null {
  if (item.rowOrdinal !== null) return t('permits.usage.row', { row: item.rowOrdinal + 1 });

  return item.rowKey;
}

/** B128c: «в історії з <дата>» для запису історії дозволів; дату, яку не вдалося розібрати, показуємо як є. */
export function historyFromText(item: PermitUsageItem): string | null {
  if (item.kind !== UsageKind.History || item.validFrom === null || item.validFrom === undefined) return null;
  const parsed = parseDateOnly(item.validFrom);

  return t('permits.usage.historyFrom', { date: parsed === null ? item.validFrom : formatDate(parsed) });
}

/** Підпис поля шапки чи колонки мовою користувача; без підпису - код. */
export function fieldLabel(item: PermitUsageItem): string {
  return localName(item.fieldLabelL10n) || item.fieldCode;
}

/** Людська назва періоду; ключ, який форматер не розпізнав (не місяць), показується як є. */
export function periodLabel(periodKey: number): string {
  return formatPeriodKey(periodKey) || String(periodKey);
}

/** Варіанти фільтра: періоди, що є в переліку місць, від нового до старого. */
export function periodOptions(items: readonly PermitUsageItem[]): { value: string; label: string }[] {
  const keys = new Set<number>();
  for (const item of items) {
    if (item.periodKey !== null) keys.add(item.periodKey);
  }

  return [...keys].sort((a, b) => b - a).map((key) => ({ value: String(key), label: periodLabel(key) }));
}

/** Період з адреси: лише ціле `YYYYNN` (інакше фільтра немає, а не запит із `NaN`). */
export function parsePeriodParam(raw: string | null): number | null {
  if (raw === null || !/^\d{5,6}$/.test(raw)) return null;

  return Number(raw);
}

/**
 * Підсумок над таблицею: документи (B56 — документи, не сегменти історії: сервер рахує документ один раз, хоч би скільки
 * місць чи сегментів у ньому було) і місця. Якщо дозвіл стоїть в історії дозволів документів — окремою часткою, бо ці
 * посилання в переліку місць не перелічуються.
 */
/** B128c: усі місця переліку — поля й комірки плюс записи історії дозволів. */
export function placeCount(data: PermitUsage): number {
  return data.linkCount + data.historyLinkCount;
}

export function summaryText(data: PermitUsage): string {
  const summary = t('permits.usage.summary', {
    documents: formatNumber(data.documentCount),
    links: formatNumber(placeCount(data)),
  });
  if (data.historyDocumentCount <= 0) return summary;

  return `${summary} · ${t('permits.usage.summaryHistory', { history: formatNumber(data.historyDocumentCount) })}`;
}

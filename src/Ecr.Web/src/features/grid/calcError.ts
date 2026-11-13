import type { TableSliceDto } from '@/api/types';
import type { components } from '@/api/schema';
import { t } from '@/shared/i18n';
import { formatDateTime } from '@/shared/format/datetime';

/**
 * ✎ C-1 (D-475): стан помилки формули комірки (сервер: `slice.calcErrors`, ключ `"{rowKey}:{columnCode}"`).
 *
 * ⛔ Значення комірки при помилці НЕ змінюється — сітка показує останнє правильне, а позначка каже, що воно не
 * оновлене: власна помилка (`Error`) або «не оновлено через помилку в …» (`Blocked`). Причина й час — словами.
 */
export type CellCalcError = components['schemas']['CellCalcErrorDto'];

/** Стан помилки комірки; `null` — помилки немає. */
export function calcErrorOf(
  slice: Pick<TableSliceDto, 'calcErrors'>,
  rowKey: string,
  columnCode: string,
): CellCalcError | null {
  return slice.calcErrors?.[`${rowKey}:${columnCode}`] ?? null;
}

/** Причина словами; ключі — літерали (сторож `EndpointCoverageTests` не приймає ключів, зібраних зі змінних). */
export function calcErrorCause(code: string): string {
  switch (code) {
    case '#DIV/0':
      return t('grid.calcError.cause.divideByZero');
    case '#REF':
      return t('grid.calcError.cause.badReference');
    case '#VALUE':
      return t('grid.calcError.cause.badValue');
    case '#UNIT':
      return t('grid.calcError.cause.badUnit');
    case '#BUDGET':
      return t('grid.calcError.cause.budget');
    case '#N/A':
      return t('grid.calcError.cause.notAvailable');
    case '#MULTI':
      return t('grid.calcError.cause.multipleMatches');
    default:
      return t('grid.calcError.cause.other', { code });
  }
}

/** Адреса комірки-джерела словами: «таблиця · колонка (рядок)». */
export function calcErrorSourceText(source: components['schemas']['FormulaErrorSourceDto']): string {
  return `${source.tableCode} · ${source.columnCode} (${source.rowKey})`;
}

/** Текст підказки (`title`): стан — і словами, не лише лінією. */
export function calcErrorHint(error: CellCalcError): string {
  const when = formatDateTime(error.since);
  if (error.kind === 'Blocked') {
    const source = error.source;
    return source === null || source === undefined
      ? t('grid.calcError.blockedHidden', { when })
      : t('grid.calcError.blocked', { source: calcErrorSourceText(source), when });
  }

  return t('grid.calcError.error', { cause: calcErrorCause(error.errorCode), when });
}

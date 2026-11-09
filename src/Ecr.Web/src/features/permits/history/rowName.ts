import type { TableSliceDto } from '@/api/types';
import { t } from '@/shared/i18n';

/**
 * Feature (j), fresh eyes 09.11: how OUR texts name a row — never the technical row key (a hex GUID for dynamic rows).
 * The row's own label; otherwise «row N» (its 1-based position in the table) and, when known, the row's permit number.
 * The server names rows the same way in its refusals and audit labels.
 */
export function rowDisplayName(slice: TableSliceDto | undefined, rowKey: string, permit?: string | null): string {
  const index = slice?.rows.findIndex((row) => row.rowKey === rowKey) ?? -1;
  const label = index >= 0 ? slice?.rows[index]?.label : null;
  if (typeof label === 'string' && label.trim().length > 0) return label;

  const position = index >= 0 ? t('permits.history.rowPosition', { n: String(index + 1) }) : t('permits.history.rowUnknown');
  return typeof permit === 'string' && permit.length > 0 ? `${position} · ${permit}` : position;
}

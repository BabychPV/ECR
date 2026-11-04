import type { JSX } from 'react';
import { Box, Group, Table, UnstyledButton, VisuallyHidden } from '@mantine/core';
import { directionOf, nextAuditSort, type AuditSortColumn } from '@/features/audit/auditSort';
import type { AuditSort } from '@/features/audit/api';
import { t } from '@/shared/i18n';

/**
 * Заголовок колонки журналу, що сортує НА СЕРВЕРІ (клієнтське сортування однієї сторінки було б хибним між
 * сторінками). `aria-sort` стоїть на активній колонці; кнопка має підпис «Sort by …» і стан словом (піктограма
 * сама не читається ні оком новачка, ні читалкою).
 */
export function SortableTh({
  label,
  column,
  sort,
  onSort,
}: {
  readonly label: string;
  readonly column: AuditSortColumn;
  readonly sort: AuditSort;
  readonly onSort: (next: AuditSort) => void;
}): JSX.Element {
  const direction = directionOf(sort, column);

  return (
    <Table.Th
      aria-sort={direction === null ? 'none' : direction === 'asc' ? 'ascending' : 'descending'}
      data-audit-sort-column={column}
    >
      <UnstyledButton
        onClick={() => onSort(nextAuditSort(sort, column))}
        title={t('audit.sortBy', { column: label })}
        fz="inherit"
        fw="inherit"
        c="inherit"
      >
        <Group gap="xs" wrap="nowrap">
          <span>{label}</span>
          <Box component="span" display="inline-flex" opacity={direction === null ? 0.35 : 1}>
            <svg width={10} height={10} viewBox="0 0 10 10" aria-hidden="true" focusable="false">
              {/* ▲ у зростанні, ▼ у спаданні, ◆ (двобічна) — коли за колонкою не сортується. */}
              {direction === 'asc' && <path d="M5 2 9 8H1z" fill="currentColor" />}
              {direction === 'desc' && <path d="M5 8 1 2h8z" fill="currentColor" />}
              {direction === null && <path d="M5 1 8 4H2zM5 9 2 6h6z" fill="currentColor" />}
            </svg>
          </Box>
          {direction !== null && (
            <VisuallyHidden>{direction === 'asc' ? t('audit.sortedAsc') : t('audit.sortedDesc')}</VisuallyHidden>
          )}
        </Group>
      </UnstyledButton>
    </Table.Th>
  );
}

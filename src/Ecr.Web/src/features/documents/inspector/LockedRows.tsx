import { Fragment, lazy, Suspense, useState, type JSX } from 'react';
import { Button, Group, Text } from '@mantine/core';
import { useQueries } from '@tanstack/react-query';
import type { ValidationFindingDto } from '@/api/types';
import type { LockedRowItem } from '@/features/grid/rowAttentionStore';
import { getDocumentPeriodReopen, periodReopenKey } from '@/features/documents/periodReopen/api';
import { formatMonthName } from '@/shared/format/period';
import { formatPeriodKey } from '@/shared/format/periodKey';
import { t } from '@/shared/i18n';

/** Діалог відкриття місяця для документа — окремим чанком, як у банері закритого періоду (`D-132`). */
const DocumentPeriodReopenDialog = lazy(async () => ({
  default: (await import('@/features/documents/periodReopen/DocumentPeriodReopenDialog')).DocumentPeriodReopenDialog,
}));

/** «January», «January and April», «January, April and July» — назви з каталогу місяців, сполучник — з каталогу. */
export function monthsText(months: readonly number[]): string {
  const names = months.map(formatMonthName).filter((name) => name !== '');
  const last = names[names.length - 1];
  if (last === undefined) return '';
  if (names.length === 1) return last;

  return t('inspector.attention.monthList', { list: names.slice(0, -1).join(', '), last });
}

/** «LTD-A01 (Gasoline blow torch)»; без назви — код. */
export function rowTitle(item: Pick<LockedRowItem, 'rowKey' | 'rowName'>): string {
  return item.rowName === null ? item.rowKey : `${item.rowKey} (${item.rowName})`;
}

/**
 * ✎ 12.11 (людина, doc 96 T3_00): одне коротке речення на рядок — рядок (код і назва) і місяці назвами, без сирих
 * заголовків колонок («Row LTD-A01 (Gasoline blow torch): the January, April and July columns are in closed months
 * and can't be edited here.»). Колонки — лише в розгортанні.
 */
export function lockedRowSentence(item: LockedRowItem): string {
  const months = monthsText(item.months);
  const params = { row: rowTitle(item), months: months === '' ? item.columns : months };

  return item.months.length === 1
    ? t('inspector.attention.lockedRowOne', params)
    : t('inspector.attention.lockedRowMany', params);
}

export interface LockedRowsProps {
  readonly documentId: number;
  readonly tableDefId: number | undefined;
  readonly rows: readonly LockedRowItem[];
  /** Перехід у рядок — той самий шлях, що й пункт перевірки (`onSelectFinding` сторінки). */
  readonly onSelect: (finding: ValidationFindingDto) => void;
}

/**
 * Пункти «закритий місяць» по рядках однієї таблиці і, під ними, дія «Reopen <місяць> for this document…» —
 * ЛИШЕ тому, кому сервер її дозволяє (`GET …/period-reopen` → `canOpen`, той самий запит і кеш, що й у банері
 * закритого періоду). Без права — речення, хто може (`document.reopen.noPermission`).
 */
export function LockedRows({ documentId, tableDefId, rows, onSelect }: LockedRowsProps): JSX.Element | null {
  const [reopening, setReopening] = useState<{ periodKey: number; defaultOpenUntil: string } | null>(null);
  const periodKeys = [...new Set(rows.flatMap((row) => row.periodKeys))].sort((a, b) => a - b);
  const states = useQueries({
    queries: periodKeys.map((periodKey) => ({
      queryKey: periodReopenKey(documentId, periodKey),
      queryFn: () => getDocumentPeriodReopen(documentId, periodKey),
    })),
  });

  if (rows.length === 0) return null;

  const openable = periodKeys.flatMap((periodKey, index) => {
    const state = states[index]?.data;
    return state !== undefined && state.active === null && state.canOpen ? [{ periodKey, defaultOpenUntil: state.defaultOpenUntil }] : [];
  });
  const denied = states.some((query) => query.data?.active === null && query.data.denial === 'NoPermission');

  return (
    <>
      {rows.map((row) => (
        <Fragment key={row.key}>
          <button
            type="button"
            className="ecr-insp-issue"
            data-severity="Warning"
            data-inspector-attention={row.key}
            data-inspector-locked-row={row.key}
            title={t('document.validationGoTo')}
            onClick={() => {
              if (tableDefId === undefined) return;
              onSelect({
                tableDefId,
                tableInstanceId: row.tableInstanceId,
                rowKey: row.rowKey,
                columnCode: row.columnCode === '' ? null : row.columnCode,
                ruleCode: 'PERIOD-LOCKED',
                severity: 'Warning',
                message: '',
                blocksSave: false,
              } as ValidationFindingDto);
            }}
          >
            <span className="ecr-insp-sev">
              <svg width={16} height={16} viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth={2}>
                <path d="M7 11V8a5 5 0 0 1 10 0v3M5 11h14v10H5z" />
              </svg>
            </span>
            <span>{lockedRowSentence(row)}</span>
          </button>
          {row.columns !== '' && (
            <details className="ecr-insp-details" data-inspector-locked-columns={row.key}>
              <summary>{t('inspector.attention.lockedColumns')}</summary>
              <Text size="xs" c="dimmed">
                {row.columns}
              </Text>
            </details>
          )}
        </Fragment>
      ))}
      {openable.length > 0 && (
        <Group gap="xs" wrap="wrap" data-inspector-locked-reopen="">
          {openable.map((month) => (
            <Button
              key={month.periodKey}
              size="xs"
              variant="light"
              data-inspector-reopen={month.periodKey}
              onClick={() => setReopening(month)}
            >
              {t('document.reopen.action', { period: formatPeriodKey(month.periodKey) })}
            </Button>
          ))}
        </Group>
      )}
      {openable.length === 0 && denied && (
        <Text size="sm" data-inspector-reopen-denied="">
          {t('document.reopen.noPermission')}
        </Text>
      )}
      {reopening !== null && (
        <Suspense fallback={null}>
          <DocumentPeriodReopenDialog
            documentId={documentId}
            periodKey={reopening.periodKey}
            defaultOpenUntil={reopening.defaultOpenUntil}
            onClose={() => setReopening(null)}
          />
        </Suspense>
      )}
    </>
  );
}

import { useState, type JSX } from 'react';
import { Alert, Anchor, Badge, Button, Group, List, Stack, Text } from '@mantine/core';
import type { DocumentTableDto } from '@/api/types';
import { formatMonthYear } from '@/shared/format/period';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import { HeaderSplitDialog, lineKey, useLineSlices } from './HeaderSplitDialog';
import { headerSplitLinesOf, orderedLines, pendingRowsByTable } from './headerSplitModel';
import { rowDisplayName } from './rowName';
import { dayMonth, dottedDate, monthOf } from './splitModel';
import { usePermitHistory } from './usePermitHistory';

export interface HeaderSplitBannerProps {
  readonly documentId: number;
  readonly periodKey: number;
  /** The page's tables of the period (table instances: their names and ids for the save). */
  readonly tables: readonly DocumentTableDto[] | undefined;
  readonly readOnly: boolean;
  /** «Show rows»: a listed row goes to its cell in the grid. */
  readonly onReveal?: ((tableInstanceId: number, rowKey: string) => void) | undefined;
}

/**
 * Feature (j) Ship 2, jh2 (mockup light-1335-jh2): after a mid-month HEADER permit change — «February 2026 must be split in
 * 7 rows (header permit changed on 15.02.2026)», the old → new permit, the rows to split by table, «Show rows» and
 * «Split all…» (jh3). Rows with their own permit column are not affected. Nothing to split — no banner.
 */
export function HeaderSplitBanner({ documentId, periodKey, tables, readOnly, onReveal }: HeaderSplitBannerProps): JSX.Element | null {
  const history = usePermitHistory(documentId, periodKey);
  const found = headerSplitLinesOf(history.data, tables);
  const slices = useLineSlices(documentId, periodKey, found.some((line) => line.needsSplit) ? found : []);
  const lines = orderedLines(found, tables, slices);
  const pending = lines.filter((line) => line.needsSplit);
  const [showRows, setShowRows] = useState(false);
  const [splitting, setSplitting] = useState(false);

  const first = pending[0] ?? lines[0];
  if (first === undefined || (pending.length === 0 && !splitting)) return null;

  const { year, month } = monthOf(first.month.month);
  const monthLabel = formatMonthYear(year, month);
  const [before, after] = first.month.parts;
  const rowCount = new Set(
    pending.map((line) => `${String(line.row.tableDefId)}#${String(line.row.sheetCopyId ?? '')}:${line.row.rowKey}`),
  ).size;
  const byTable = pendingRowsByTable(lines);
  const tableName = (table: DocumentTableDto | null, code: string): string =>
    table === null ? code : localized(table.tableNameL10n) || table.tableCode;

  return (
    <>
      {pending.length > 0 && (
        <Alert
          color="statusWarning"
          variant="light"
          data-header-split-banner=""
          title={t('permits.header.bannerTitle', {
            month: monthLabel,
            count: rowCount,
            date: after === undefined ? '' : dottedDate(after.dayFrom),
          })}
        >
          <Stack gap="xs">
            <Group justify="space-between" align="flex-start" wrap="wrap" gap="sm">
              <Text size="sm" style={{ flex: '1 1 320px', minWidth: 0 }}>
                {t('permits.header.bannerText', {
                  old: before?.permit ?? '—',
                  until: before === undefined ? '' : dayMonth(before.dayTo),
                  next: after?.permit ?? '—',
                  from: after === undefined ? '' : dayMonth(after.dayFrom),
                })}
              </Text>
              <Group gap="xs">
                <Button variant="default" size="xs" data-header-split-show="" onClick={() => setShowRows((value) => !value)}>
                  {showRows ? t('permits.header.hideRows') : t('permits.header.showRows')}
                </Button>
                {!readOnly && (
                  <Button size="xs" data-header-split-all="" onClick={() => setSplitting(true)}>
                    {t('permits.header.splitAll')}
                  </Button>
                )}
              </Group>
            </Group>
            <Group gap="xs">
              {byTable.map((entry) => (
                <Badge
                  key={`${String(entry.tableDefId)}#${String(entry.sheetCopyId ?? '')}`}
                  variant="outline"
                  color="statusWarning"
                  tt="none"
                >
                  {t('permits.header.bannerTable', { table: tableName(entry.table, String(entry.tableDefId)), count: entry.rows })}
                </Badge>
              ))}
            </Group>
            {showRows && (
              <List size="sm" spacing="xs" data-header-split-rows="">
                {pending.map((line) => {
                  const slice = slices.get(line.table?.tableInstanceId ?? 0);
                  const name = `${tableName(line.table, line.row.tableCode)} — ${rowDisplayName(slice, line.row.rowKey, line.row.permits[0]?.permit)}`;

                  return (
                    <List.Item key={lineKey(line)}>
                      {onReveal === undefined || line.table === null ? (
                        name
                      ) : (
                        <Anchor
                          component="button"
                          type="button"
                          size="sm"
                          onClick={() => onReveal(line.table?.tableInstanceId ?? 0, line.row.rowKey)}
                        >
                          {name}
                        </Anchor>
                      )}
                    </List.Item>
                  );
                })}
              </List>
            )}
          </Stack>
        </Alert>
      )}
      {splitting && (
        <HeaderSplitDialog documentId={documentId} periodKey={periodKey} lines={lines} slices={slices} onClose={() => setSplitting(false)} />
      )}
    </>
  );
}

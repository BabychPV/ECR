import type { JSX } from 'react';
import { Button, Group, Modal, ScrollArea, Table, Text } from '@mantine/core';
import { useInfiniteQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import { formatNumber } from '@/shared/format';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { t } from '@/shared/i18n';

type SnapshotRowsPage = components['schemas']['SnapshotRowsPage'];

/** Рядків на сторінку: перші сто, далі — «показати ще». */
const PageSize = 100;

/**
 * Рядки зрізу в застосунку (`D-52a`): другий споживач `rpt.*` поруч із SSRS.
 *
 * ⚠ Колонки приходять із сервера — з опису версії, за якою зріз побудовано, —
 * тож таблиця нічого не знає про конкретний звіт. Заголовок колонки — її код:
 * назв мовами каталогу в описі ще немає.
 */
export default function SnapshotRowsModal(props: {
  snapshotId: number | null;
  onClose: () => void;
}): JSX.Element {
  const { snapshotId } = props;

  const pages = useInfiniteQuery({
    queryKey: ['snapshot-rows', snapshotId],
    queryFn: ({ pageParam }) =>
      apiFetch<SnapshotRowsPage>(
        `/api/v1/reports/snapshots/${snapshotId ?? 0}/rows?limit=${PageSize}` +
          (pageParam === null ? '' : `&cursor=${pageParam}`),
      ),
    initialPageParam: null as number | null,
    getNextPageParam: (last) => last.nextCursor,
    enabled: snapshotId !== null,
  });

  const columns = pages.data?.pages[0]?.columns ?? [];
  const rows = (pages.data?.pages ?? []).flatMap((page) => page.rows);

  return (
    <Modal
      opened={snapshotId !== null}
      onClose={props.onClose}
      title={t('snapshots.rowsTitle')}
      size="xl"
    >
      <AsyncBoundary<typeof rows>
        isPending={pages.isPending}
        error={pages.error}
        data={pages.data === undefined ? undefined : rows}
        isEmpty={(all) => all.length === 0}
        emptyTitle={t('snapshots.rowsEmpty')}
        skeleton="table"
        onRetry={() => void pages.refetch()}
      >
        {(all) => (
          <ScrollArea type="auto" offsetScrollbars>
            <Table striped>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>#</Table.Th>
                  {columns.map((column) => (
                    <Table.Th key={column.code} ta={column.kind === 'number' ? 'right' : undefined}>
                      {column.code}
                    </Table.Th>
                  ))}
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {all.map((row) => (
                  <Table.Tr key={row.rowNo}>
                    <Table.Td>{row.rowNo}</Table.Td>
                    {columns.map((column) => (
                      <Table.Td key={column.code} ta={column.kind === 'number' ? 'right' : undefined}>
                        {cellText(row.cells[column.code], column.code)}
                      </Table.Td>
                    ))}
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </ScrollArea>
        )}
      </AsyncBoundary>

      {pages.hasNextPage && (
        <Group justify="center" mt="md">
          <Button
            size="xs"
            variant="default"
            loading={pages.isFetchingNextPage}
            onClick={() => void pages.fetchNextPage()}
          >
            {t('snapshots.rowsMore')}
          </Button>
        </Group>
      )}

      {pages.data !== undefined && (
        <Text size="xs" c="dimmed" mt="xs">
          {formatNumber(rows.length)}
        </Text>
      )}
    </Modal>
  );
}

/**
 * Значення комірки текстом.
 *
 * ⚠ Ідентифікатори (`…Id`, `PeriodKey`) — числа лише за типом: роздільники
 * розрядів зробили б із `202603` «202 603».
 */
function cellText(value: unknown, code: string): string {
  if (value === null || value === undefined) return '—';

  if (typeof value === 'number') {
    return /Id$|^PeriodKey$/.test(code)
      ? String(value)
      : formatNumber(value, { maximumFractionDigits: 10 });
  }

  return String(value);
}

import type { JSX } from 'react';
import { Button, Group, Table, Text } from '@mantine/core';
import { useInfiniteQuery } from '@tanstack/react-query';
import { getRegistryEntryHistory } from '@/features/registries/rows/api';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { Timestamp } from '@/shared/ui/Timestamp';
import { entryHistoryKey, historyChangeLabel, historyValueText } from './entryHistory';
import type { RegistryField } from './rowModel';

/** Розмір сторінки журналу. */
export const EntryHistoryPageSize = 50;

export interface EntryHistoryLogProps {
  readonly registryCode: string;
  readonly entryId: number;
  readonly fields: readonly RegistryField[];
}

/**
 * Журнал змін запису довідника (RT-15, FEATURE-REGISTRY-TABLES §8.5): хто, коли й що змінив, «було» →
 * «стало», від найновішого, сторінками за курсором.
 *
 * ⚠ `export default` — вимога `React.lazy()`: журнал вантажиться окремим чанком лише тоді, коли
 * вкладку «Історія» відкрили.
 *
 * ⚠ Джерело — системні версії запису на сервері, тож тут видно будь-який шлях запису: форма, пакет,
 * CSV, синк. Автор `null` — фонова задача без автора.
 */
export default function EntryHistoryLog({ registryCode, entryId, fields }: EntryHistoryLogProps): JSX.Element {
  const history = useInfiniteQuery({
    queryKey: entryHistoryKey(registryCode, entryId),
    queryFn: ({ pageParam }) =>
      getRegistryEntryHistory(registryCode, entryId, { cursor: pageParam, limit: EntryHistoryPageSize }),
    initialPageParam: null as string | null,
    getNextPageParam: (page) => page.nextCursor ?? undefined,
    refetchOnWindowFocus: false,
  });

  const items = history.data?.pages.flatMap((page) => page.items) ?? [];

  if (history.error !== null) {
    return <ErrorAlert error={history.error} onRetry={() => void history.refetch()} />;
  }

  if (history.isPending) {
    return (
      <Text size="sm" c="dimmed">
        {t('common.loading')}
      </Text>
    );
  }

  if (items.length === 0) {
    return (
      <Text size="sm" c="dimmed" data-testid="entry-history-empty">
        {t('registries.entryHistory.empty')}
      </Text>
    );
  }

  return (
    <>
      <Table withTableBorder striped aria-label={t('registries.entryHistory.title')} data-testid="entry-history">
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{t('registries.entryHistory.when')}</Table.Th>
            <Table.Th>{t('registries.entryHistory.author')}</Table.Th>
            <Table.Th>{t('registries.entryHistory.change')}</Table.Th>
            <Table.Th>{t('registries.entryHistory.before')}</Table.Th>
            <Table.Th>{t('registries.entryHistory.after')}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {items.map((item, index) => (
            // Ключ — позиція: дві зміни одного поля в один момент сервер віддає окремими рядками.
            <Table.Tr key={`${item.at}|${item.kind}|${item.field ?? ''}|${String(index)}`}>
              <Table.Td>
                <Timestamp value={item.at} precise />
              </Table.Td>
              <Table.Td>{item.byDisplayName ?? t('registries.entryHistory.unknownAuthor')}</Table.Td>
              <Table.Td>{historyChangeLabel(item, fields)}</Table.Td>
              <Table.Td>{historyValueText(item, 'old')}</Table.Td>
              <Table.Td>{historyValueText(item, 'new')}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
      {history.hasNextPage && (
        <Group justify="center" mt="xs">
          <Button
            variant="default"
            loading={history.isFetchingNextPage}
            onClick={() => void history.fetchNextPage()}
          >
            {t('registries.entryHistory.loadMore')}
          </Button>
        </Group>
      )}
    </>
  );
}

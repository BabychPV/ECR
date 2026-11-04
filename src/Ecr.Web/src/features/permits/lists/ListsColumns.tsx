import { Box, Group, Text } from '@mantine/core';
import { t } from '@/shared/i18n';
import type { DataTableColumn } from '@/shared/ui/DataTable';
import { kindLabel, previewText, UsedShown, usedMoreText, useText, valuesText, type ListRow } from './listsModel';

/**
 * Стовпці переліку списків (макет: List · Kind · Values · Used by). Назва - один рядок (де лежать значення, каже «Kind»).
 * «Values» - лічильник словами («12 values», для дерева НАЕ «N nodes»), під ним - перші значення, коли сервер їх віддає.
 * «Used by» - перші два стовпці-споживачі й «+N»; шеврон наприкінці рядка показує, що рядок відкривається.
 * ⚠ Без `num`: числова колонка тулить значення до правого краю широкої таблиці, і лічильник виглядав порожнім.
 */
export function listColumns(): readonly DataTableColumn<ListRow>[] {
  return [
    {
      key: 'name',
      label: t('permits.lists.col.list'),
      minWidth: 200,
      sortValue: (row) => row.name.toLocaleLowerCase(),
      render: (row) => <Text size="sm" fw={500}>{row.name}</Text>,
    },
    {
      key: 'kind',
      label: t('permits.lists.col.kind'),
      minWidth: 180,
      sortValue: (row) => row.kind,
      render: (row) => kindLabel(row.kind),
    },
    {
      key: 'values',
      label: t('permits.lists.col.values'),
      minWidth: 160,
      sortValue: (row) => row.entries ?? -1,
      render: (row) => {
        const preview = previewText(row);

        return (
          <Box data-list-values="">
            <Text size="sm">{valuesText(row)}</Text>
            {preview !== '' && (
              <Text size="xs" c="dimmed" data-list-preview="">
                {preview}
              </Text>
            )}
          </Box>
        );
      },
    },
    {
      key: 'used',
      label: t('permits.lists.col.used'),
      minWidth: 260,
      sortValue: (row) => row.used.length,
      render: (row) => {
        const shown = row.used.slice(0, UsedShown);

        return (
          <Group justify="space-between" wrap="nowrap" gap="xs" data-list-used-cell="">
            <Box style={{ minWidth: 0 }}>
              {shown.length === 0 && (
                <Text size="sm" c="dimmed">
                  {t('permits.lists.used.none')}
                </Text>
              )}
              {shown.map((use) => (
                <Text size="sm" key={`${use.typeCode}:${use.tableCode}:${use.columnCode}`}>
                  {useText(use)}
                </Text>
              ))}
              {row.used.length > UsedShown && (
                <Text size="xs" c="dimmed">
                  {usedMoreText(row.used.length)}
                </Text>
              )}
            </Box>
            <Text aria-hidden="true" c="dimmed" data-list-chevron="">
              ›
            </Text>
          </Group>
        );
      },
    },
  ];
}

import { t } from '@/shared/i18n';
import type { DataTableColumn } from '@/shared/ui/DataTable';
import { TwoLine } from '@/shared/ui/TwoLine';
import { kindLabel, usedCountText, type ListRow } from './listsModel';

/** Другий рядок під назвою: де лежать значення списку. */
export function keptIn(row: ListRow): string {
  return row.kind === 'shared' ? t('permits.lists.keptIn.registries') : t('permits.lists.keptIn.type', { owner: row.owner });
}

/**
 * Стовпці переліку списків (макет: List · Kind · Values · Used by). «Values» для списку-таблиці дозволу —
 * словами, а не числом: рядки різні в кожного дозволу.
 */
export function listColumns(): readonly DataTableColumn<ListRow>[] {
  return [
    {
      key: 'name',
      label: t('permits.lists.col.list'),
      minWidth: 240,
      sortValue: (row) => row.name.toLocaleLowerCase(),
      render: (row) => <TwoLine primary={row.name} secondary={keptIn(row)} />,
    },
    {
      key: 'kind',
      label: t('permits.lists.col.kind'),
      sortValue: (row) => row.kind,
      render: (row) => kindLabel(row.kind),
    },
    {
      key: 'values',
      label: t('permits.lists.col.values'),
      num: true,
      sortValue: (row) => row.entries ?? -1,
      render: (row) =>
        row.kind === 'permit' ? t('permits.lists.values.permit') : row.entries === null ? '' : String(row.entries),
    },
    {
      key: 'used',
      label: t('permits.lists.col.used'),
      sortValue: (row) => row.used.length,
      render: (row) => usedCountText(row.used.length),
    },
  ];
}

import type { JSX } from 'react';
import { Badge } from '@mantine/core';
import { language, t } from '@/shared/i18n';
import type { DataTableColumn } from '@/shared/ui/DataTable';
import { Timestamp } from '@/shared/ui/Timestamp';
import { TwoLine } from '@/shared/ui/TwoLine';
import type { PermitGroupDto } from './groupsApi';
import { groupDescription, groupName, typesText, usageText } from './groupsModel';

/** Стан групи словом (`ФВ-14.18`: не лише колір); нейтральний тон - колір лишається для того, що не так. */
export function GroupStateBadge({ group }: { readonly group: PermitGroupDto }): JSX.Element {
  return (
    <Badge
      size="sm"
      variant="transparent"
      radius="xs"
      c="dimmed"
      tt="none"
      fw={400}
      miw="fit-content"
      data-group-state={group.isActive ? 'active' : 'inactive'}
    >
      {group.isActive ? t('permits.groups.state.active') : t('permits.groups.state.inactive')}
    </Badge>
  );
}

/**
 * Стовпці переліку груп: Group (назва + код) · Description · Permit types · Used in · Status · Updated. Рядок
 * відкривається клавіатурою (`rowFocusable` на сторінці); клік по ньому веде до шторки.
 */
export function groupColumns(): readonly DataTableColumn<PermitGroupDto>[] {
  const lang = language();

  return [
    {
      key: 'name',
      label: t('permits.groups.col.group'),
      minWidth: 220,
      sortValue: (group) => groupName(group, lang).toLocaleLowerCase(),
      render: (group) => <TwoLine primary={groupName(group, lang)} secondary={group.code} mono />,
    },
    {
      key: 'description',
      label: t('permits.groups.col.description'),
      minWidth: 220,
      sortValue: (group) => groupDescription(group, lang).toLocaleLowerCase(),
      render: (group) => groupDescription(group, lang),
    },
    {
      key: 'types',
      label: t('permits.groups.col.types'),
      minWidth: 240,
      sortValue: (group) => group.types.length,
      render: (group) => (group.types.length === 0 ? t('permits.groups.noTypes') : typesText(group, lang)),
    },
    {
      key: 'used',
      label: t('permits.groups.col.used'),
      sortValue: (group) => group.usedInTemplates,
      render: (group) => usageText(group),
    },
    {
      key: 'state',
      label: t('permits.groups.col.state'),
      sortValue: (group) => (group.isActive ? 0 : 1),
      render: (group) => <GroupStateBadge group={group} />,
    },
    {
      key: 'updated',
      label: t('permits.groups.col.updated'),
      sortValue: (group) => group.updatedAt,
      render: (group) => <Timestamp value={group.updatedAt} dateOnly />,
    },
  ];
}

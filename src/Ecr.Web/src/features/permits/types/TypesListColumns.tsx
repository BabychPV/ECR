import type { JSX } from 'react';
import { Anchor, Badge } from '@mantine/core';
import { Link } from 'react-router-dom';
import { language, t } from '@/shared/i18n';
import type { DataTableColumn } from '@/shared/ui/DataTable';
import { Timestamp } from '@/shared/ui/Timestamp';
import { TwoLine } from '@/shared/ui/TwoLine';
import type { PermitTypeListItem } from '../api/types';
import { typeEditorPath } from '../shell/permitPaths';
import { categoryLabel, hasDraftChanges, tablesText, typeName, typeSubtitle } from './typesListModel';

/**
 * Стан типу (макет: `v3` для опублікованого, `Draft changes` для типу зі змінами). Підпис — словом, а не
 * кольором (`ФВ-14.18`); нейтральний тон: колір у таблиці лише для того, що не так.
 */
export function TypeStateBadge({ item }: { readonly item: PermitTypeListItem }): JSX.Element {
  const draft = hasDraftChanges(item);

  return (
    <Badge
      size="sm"
      variant="transparent"
      radius="xs"
      c="dimmed"
      tt="none"
      fw={400}
      miw="fit-content"
      data-type-state={draft ? 'draft' : 'published'}
    >
      {draft ? t('permits.types.state.draft') : t('permits.types.state.published', { version: item.version })}
    </Badge>
  );
}

/**
 * Стовпці переліку типів (макет `screens-permits.js`, семи не перевищено): Type · Category · Tables ·
 * Permits · Updated · State. Назва типу — посилання на редактор, тож рядок досяжний клавіатурою; клік по
 * решті рядка веде туди ж (`onRowClick` сторінки).
 */
export function typeColumns(): readonly DataTableColumn<PermitTypeListItem>[] {
  const lang = language();

  return [
    {
      key: 'name',
      label: t('permits.types.col.type'),
      minWidth: 220,
      sortValue: (item) => typeName(item, lang).toLocaleLowerCase(),
      render: (item) => (
        <TwoLine
          primary={
            <Anchor
              component={Link}
              to={typeEditorPath(item.code)}
              size="sm"
              data-type-open={item.code}
              onClick={(event) => {
                // Перехід робить саме посилання; без цього спрацював би ще й клік рядка.
                event.stopPropagation();
              }}
            >
              {typeName(item, lang)}
            </Anchor>
          }
          secondary={typeSubtitle(item, lang)}
        />
      ),
    },
    {
      key: 'category',
      label: t('permits.types.col.category'),
      render: (item) => categoryLabel(item.category),
      sortValue: (item) => item.category,
    },
    {
      key: 'tables',
      label: t('permits.types.col.tables'),
      minWidth: 240,
      sortValue: (item) => item.tables.length,
      render: (item) => tablesText(item, lang),
    },
    {
      key: 'permits',
      label: t('permits.types.col.permits'),
      num: true,
      sortValue: (item) => item.permitCount,
      render: (item) => String(item.permitCount),
    },
    {
      key: 'updated',
      label: t('permits.types.col.updated'),
      sortValue: (item) => item.updatedAt,
      render: (item) => (
        <TwoLine primary={<Timestamp value={item.updatedAt} dateOnly />} secondary={item.updatedByDisplayName} />
      ),
    },
    {
      key: 'state',
      label: t('permits.types.col.state'),
      sortValue: (item) => (hasDraftChanges(item) ? 0 : item.version),
      render: (item) => <TypeStateBadge item={item} />,
    },
  ];
}

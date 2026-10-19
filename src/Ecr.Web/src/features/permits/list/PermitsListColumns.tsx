import type { JSX } from 'react';
import { ActionIcon, Anchor, Menu } from '@mantine/core';
import { Link } from 'react-router-dom';
import { language, t } from '@/shared/i18n';
import { formatDate } from '@/shared/format';
import type { DataTableColumn } from '@/shared/ui/DataTable';
import { TwoLine } from '@/shared/ui/TwoLine';
import type { PermitListItem } from '../api/types';
import { PermitStateBadge } from '../ui/PermitStateBadge';
import {
  actionLabel,
  actionsFor,
  isEndingSoon,
  permitHref,
  permitTypeName,
  readByText,
  rowsText,
  type PermitAction,
} from './permitsListModel';

/** Підпис під діапазоном чинності: «Ends in 40 days» або «Ended early 01.06.2026». */
function validitySecondary(item: PermitListItem): string | null {
  if (isEndingSoon(item) && item.endsInDays !== null) return t('permits.list.endsIn', { days: item.endsInDays });
  if (item.actualEnd !== null && item.actualEnd !== item.validTo) {
    return t('permits.list.endedEarly', { date: formatDate(item.actualEnd) });
  }

  return null;
}

function validityText(item: PermitListItem): string {
  if (item.validFrom === null || item.validTo === null) return '';

  return `${formatDate(item.validFrom)} - ${formatDate(item.validTo)}`;
}

/** Меню дій рядка: лише ті, що сервер допускає зі станом; клік не відкриває рядок. */
function RowMenu({
  item,
  onAction,
}: {
  readonly item: PermitListItem;
  readonly onAction: (item: PermitListItem, action: PermitAction) => void;
}): JSX.Element | null {
  const actions = actionsFor(item.status);
  if (actions.length === 0) return null;

  return (
    <Menu position="bottom-end" withinPortal>
      <Menu.Target>
        <ActionIcon
          variant="subtle"
          color="gray"
          aria-label={t('permits.list.rowMenu', { number: item.number })}
          data-permit-menu={item.id}
          onClick={(event) => {
            event.stopPropagation();
          }}
        >
          <span aria-hidden="true">⋯</span>
        </ActionIcon>
      </Menu.Target>
      <Menu.Dropdown>
        {actions.map((action) => (
          <Menu.Item
            key={action}
            data-permit-action={action}
            {...(action === 'delete' || action === 'end' ? { color: 'statusError' } : {})}
            onClick={(event) => {
              event.stopPropagation();
              onAction(item, action);
            }}
          >
            {actionLabel(action)}
          </Menu.Item>
        ))}
      </Menu.Dropdown>
    </Menu>
  );
}

/**
 * Стовпці переліку дозволів (макет `screens-permits.js` §1, не більше семи — `L5`): Permit · Type · Validity ·
 * Rows · Read by · State, плюс колонка дій. Номер — посилання на сторінку дозволу (клавіатурою), клік по решті
 * рядка веде туди ж (`onRowClick` сторінки).
 *
 * `onAction === undefined` (немає права писати) — колонки дій немає взагалі: прихована, а не вимкнена.
 */
export function permitColumns(
  onAction?: (item: PermitListItem, action: PermitAction) => void,
): readonly DataTableColumn<PermitListItem>[] {
  const lang = language();
  const columns: DataTableColumn<PermitListItem>[] = [
    {
      key: 'number',
      label: t('permits.list.col.permit'),
      minWidth: 200,
      sortValue: (item) => item.number.toLocaleLowerCase(),
      render: (item) => (
        <TwoLine
          primary={
            <Anchor
              component={Link}
              to={permitHref(item.id)}
              size="sm"
              data-permit-open={item.id}
              onClick={(event) => {
                // Перехід робить саме посилання; без цього спрацював би ще й клік рядка.
                event.stopPropagation();
              }}
            >
              {item.number}
            </Anchor>
          }
          secondary={item.location}
        />
      ),
    },
    {
      key: 'type',
      label: t('permits.list.col.type'),
      minWidth: 180,
      sortValue: (item) => permitTypeName(item, lang).toLocaleLowerCase(),
      render: (item) => <TwoLine primary={permitTypeName(item, lang)} secondary={item.typeCode} />,
    },
    {
      key: 'validity',
      label: t('permits.list.col.validity'),
      minWidth: 200,
      sortValue: (item) => item.validFrom,
      render: (item) => <TwoLine primary={validityText(item)} secondary={validitySecondary(item)} />,
    },
    {
      key: 'rows',
      label: t('permits.list.col.rows'),
      num: true,
      sortValue: (item) => item.rowCount,
      render: (item) => rowsText(item),
    },
    {
      key: 'readBy',
      label: t('permits.list.col.readBy'),
      sortValue: (item) => item.boundSheetCount,
      render: (item) => readByText(item),
    },
    {
      key: 'state',
      label: t('permits.list.col.state'),
      sortValue: (item) => item.status,
      render: (item) => <PermitStateBadge state={item.status} />,
    },
  ];

  if (onAction !== undefined) {
    columns.push({
      key: 'actions',
      label: t('permits.list.col.actions'),
      sortable: false,
      render: (item) => <RowMenu item={item} onAction={onAction} />,
    });
  }

  return columns;
}

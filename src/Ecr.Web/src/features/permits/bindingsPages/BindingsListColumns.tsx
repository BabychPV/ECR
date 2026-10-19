import { Anchor } from '@mantine/core';
import { Link } from 'react-router-dom';
import { t } from '@/shared/i18n';
import type { DataTableColumn } from '@/shared/ui/DataTable';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { TwoLine } from '@/shared/ui/TwoLine';
import { modeLabel, ruleLabel } from './bindingLabels';
import { bindingDetailPath, type BindingRow } from './scanModel';

/**
 * Стовпці переліку прив'язок (макет `screens-permits.js` §5): Sheet table · Reads · How · Which permit · Template.
 * Код таблиці аркуша - посилання на екран прив'язки, тож рядок досяжний клавіатурою; клік по решті рядка веде туди ж.
 */
export function bindingColumns(): readonly DataTableColumn<BindingRow>[] {
  return [
    {
      key: 'target',
      label: t('permits.bindingsList.col.target'),
      minWidth: 220,
      sortValue: (row) => row.binding.sheetTableCode.toLocaleLowerCase(),
      render: (row) => (
        <TwoLine
          primary={
            <Anchor
              component={Link}
              to={bindingDetailPath(row)}
              size="sm"
              data-binding-open={row.binding.code}
              // Перехід робить саме посилання; без цього спрацював би ще й клік рядка.
              onClick={(event) => event.stopPropagation()}
            >
              {row.binding.sheetTableCode}
            </Anchor>
          }
          secondary={row.binding.code}
          mono
        />
      ),
    },
    {
      key: 'reads',
      label: t('permits.bindingsList.col.reads'),
      minWidth: 200,
      sortValue: (row) => `${row.binding.permitTypeCode} ${row.binding.permitTableCode}`.toLocaleLowerCase(),
      render: (row) => <TwoLine primary={row.binding.permitTableCode} secondary={row.binding.permitTypeCode} mono />,
    },
    {
      key: 'mode',
      label: t('permits.bindingsList.col.how'),
      sortValue: (row) => row.binding.mode,
      render: (row) => (
        <TwoLine
          primary={modeLabel(row.binding.mode)}
          secondary={row.binding.isActive ? undefined : t('permits.bindingsList.inactive')}
        />
      ),
    },
    {
      key: 'rule',
      label: t('permits.bindingsList.col.pick'),
      minWidth: 220,
      sortValue: (row) => row.binding.selectionRule,
      render: (row) => ruleLabel(row.binding.selectionRule),
    },
    {
      key: 'template',
      label: t('permits.bindingsList.col.template'),
      minWidth: 200,
      sortValue: (row) => `${row.source.templateCode} ${row.source.version}`.toLocaleLowerCase(),
      render: (row) => (
        <TwoLine
          primary={row.source.templateName.length > 0 ? row.source.templateName : row.source.templateCode}
          secondary={`${row.source.templateCode} · ${row.source.version}`}
          mono
        />
      ),
    },
    {
      key: 'state',
      label: t('permits.bindingsList.col.state'),
      sortValue: (row) => row.source.kind,
      render: (row) => <StatusBadge kind="version" state={row.source.kind} quiet />,
    },
  ];
}

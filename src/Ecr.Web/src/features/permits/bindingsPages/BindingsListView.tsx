import { useMemo, useState, type JSX } from 'react';
import { Box, Button, Stack, Text } from '@mantine/core';
import { useNavigate } from 'react-router-dom';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { Banner } from '@/shared/ui/Banner';
import { EmptyState } from '@/shared/ui/AsyncBoundary';
import { DataTable } from '@/shared/ui/DataTable';
import { FilterBar } from '@/shared/ui/FilterBar';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatStrip } from '@/shared/ui/StatStrip';
import { useUrlParamsSetter, useUrlState } from '@/shared/ui/useUrlState';
import { PermitsSubnav } from '../shell/PermitsSubnav';
import { modeLabel } from './bindingLabels';
import { bindingColumns } from './BindingsListColumns';
import { BindingModes, bindingDetailPath, filterRows, rowStats, ScanPageSize, type BindingRow } from './scanModel';
import { useBindingsScan } from './useBindingsScan';

/** Право читання прив'язок (сервер: `Template.View`, `PermitBindingsController`). */
export const BindingsViewPermission = 'Template.View';

/**
 * Перелік прив'язок аркушів до пермітів (`/admin/permits/bindings`, макет `screens-permits.js` §5).
 *
 * ⛔ Сервер не має переліку «усі прив'язки» - вони читаються по версіях шаблонів. Тому екран читає порціями
 * (`ScanPageSize` шаблонів, до 4 запитів одночасно) і ЧЕСНО каже, скільки шаблонів охоплено; решту додає кнопка
 * «Check more». Помилка читання окремої версії не валить перелік - вона виноситься в смугу над таблицею.
 * ⚠ Лише читання: створюються й змінюються прив'язки на вкладці Permit версії шаблону.
 */
export function BindingsListView(): JSX.Element {
  const navigate = useNavigate();
  const setParams = useUrlParamsSetter();
  const session = useSession();
  const [query] = useUrlState('q');
  const [mode] = useUrlState('mode');
  const [statParam, setStat] = useUrlState('stat');
  const [limit, setLimit] = useState(ScanPageSize);

  const sessionKnown = session.data !== undefined;
  const mayView = can(session.data, BindingsViewPermission);
  const scan = useBindingsScan(mayView, limit);
  const { outcome, plan } = scan;

  const stat = statParam === 'rows' || statParam === 'draft' ? statParam : null;
  const modeFilter = mode !== null && BindingModes.includes(mode) ? mode : '';
  const rows = useMemo<readonly BindingRow[] | undefined>(
    () =>
      plan === undefined
        ? undefined
        : filterRows(outcome.rows, { needle: query ?? '', mode: modeFilter, stat }),
    [plan, outcome.rows, query, modeFilter, stat],
  );
  const stats = useMemo(() => rowStats(outcome.rows), [outcome.rows]);
  const columns = useMemo(() => bindingColumns(), []);

  const header = (
    <>
      <PageHeader title={t('permits.bindings.title')} />
      <PermitsSubnav current="bindings" />
    </>
  );

  // ⛔ Без права ні запитів, ні таблиці: сервер відповів би 403 на кожну версію. Поки сеанс не завантажено - скелет.
  if (sessionKnown && !mayView) {
    return (
      <Stack gap="md" data-list-page="">
        {header}
        <EmptyState
          title={t('permits.bindingsList.noAccess.title')}
          hint={t('permits.bindingsList.noAccess.hint')}
        />
      </Stack>
    );
  }

  const pending = !sessionKnown || scan.planning || (plan !== undefined && !outcome.done && outcome.rows.length === 0);
  const hasRows = outcome.rows.length > 0;
  const moreTemplates = plan !== undefined && plan.templatesChecked < plan.templatesTotal;
  const filtered = (query ?? '').trim().length > 0 || modeFilter.length > 0 || stat !== null;

  return (
    <Stack gap="md" data-list-page="">
      {header}
      <Text size="sm" c="dimmed">
        {t('permits.bindingsList.createHint')}
      </Text>

      {hasRows && (
        <StatStrip
          label={t('permits.bindingsList.stats.aria')}
          active={stat}
          onSelect={setStat}
          items={[
            { id: 'all', label: t('permits.bindingsList.stat.all'), value: stats.all, filter: false },
            { id: 'rows', label: t('permits.bindingsList.stat.rows'), value: stats.rows },
            { id: 'draft', label: t('permits.bindingsList.stat.draft'), value: stats.draft, tone: 'warning' },
          ]}
        />
      )}

      {hasRows && (
        <Box data-list-filters="">
          <FilterBar
            search={{
              label: t('permits.bindingsList.search.label'),
              placeholder: t('permits.bindingsList.search.placeholder'),
            }}
            filters={[
              {
                id: 'mode',
                label: t('permits.bindingsList.filter.mode'),
                options: BindingModes.map((value) => ({ value, label: modeLabel(value) })),
              },
            ]}
            clearLabel={t('filters.clear')}
          />
        </Box>
      )}

      {scan.planError === null && plan !== undefined && (
        <Text size="sm" c="dimmed" data-scan-note="">
          {scanNote(plan.templatesChecked, plan.templatesTotal, outcome.settled, outcome.total, scan.templatesTruncated)}
        </Text>
      )}

      {outcome.failed.length > 0 && (
        <Banner
          tone="warning"
          title={t('permits.bindingsList.failed.title', { count: outcome.failed.length })}
          text={outcome.failed
            .map(
              ({ source }) =>
                `${source.templateCode} ${source.version}`,
            )
            .join(', ')}
          actions={[{ label: t('permits.bindingsList.failed.retry'), onClick: scan.retryFailed }]}
          testId="bindings-failed"
        />
      )}

      <Box data-list-table="">
        <DataTable<BindingRow>
          columns={columns}
          rows={rows}
          rowKey={(row) => row.key}
          isPending={pending}
          error={scan.planError}
          onRetry={scan.retryPlan}
          filtered={filtered}
          emptyTitle={t('permits.bindingsList.empty.title')}
          emptyHint={t('permits.bindingsList.empty.hint')}
          noMatchTitle={t('permits.bindingsList.noMatches')}
          onClearFilters={() => setParams({ q: null, mode: null, stat: null })}
          clearFiltersLabel={t('filters.clear')}
          onRowClick={(row) => void navigate(bindingDetailPath(row))}
        />
      </Box>

      {moreTemplates && (
        <Button
          variant="default"
          onClick={() => setLimit((value) => value + ScanPageSize)}
          style={{ alignSelf: 'flex-start' }}
          data-scan-more=""
        >
          {t('permits.bindingsList.scan.more', { count: Math.min(ScanPageSize, plan.templatesTotal - plan.templatesChecked) })}
        </Button>
      )}
    </Stack>
  );
}

function scanNote(checked: number, total: number, settled: number, versions: number, truncated: boolean): string {
  const base =
    settled < versions
      ? t('permits.bindingsList.scan.progress', { checked, total, settled, versions })
      : t('permits.bindingsList.scan.done', { checked, total, versions });

  return truncated ? `${base} ${t('permits.bindingsList.scan.truncated')}` : base;
}

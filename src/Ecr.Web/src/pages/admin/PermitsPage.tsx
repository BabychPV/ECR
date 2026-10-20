import { Suspense, lazy, useMemo, useState, type JSX } from 'react';
import { Box, Button, Stack } from '@mantine/core';
import { useNavigate } from 'react-router-dom';
import { language, t } from '@/shared/i18n';
import { DataTable } from '@/shared/ui/DataTable';
import { FilterBar } from '@/shared/ui/FilterBar';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatStrip } from '@/shared/ui/StatStrip';
import { useUrlParamsSetter, useUrlState } from '@/shared/ui/useUrlState';
import type { PermitListItem } from '@/features/permits/api/types';
import type { PermitActionTarget } from '@/features/permits/list/PermitLifecycleDialogs';
import { permitColumns } from '@/features/permits/list/PermitsListColumns';
import { useMayWritePermits } from '@/features/permits/list/permitListRights';
import {
  EndingSoonDays,
  FilterStates,
  asPermitStat,
  filterPermits,
  listStats,
  permitHref,
  typeOptions,
  type PermitAction,
} from '@/features/permits/list/permitsListModel';
import { useAllPermits } from '@/features/permits/list/usePermits';
import { permitStateLabel } from '@/features/permits/shell/permitStatus';
import { PermitsSubnav } from '@/features/permits/shell/PermitsSubnav';

/*
 * ⚠ Майстер і діалоги життєвого циклу — лінивими чанками: `Modal` Mantine і поля форм не потрібні, доки людина
 * не натисне «New permit» чи дію рядка, а чанк маршруту стоїть під бюджетом D-132.
 */
const NewPermitWizard = lazy(async () => ({
  default: (await import('@/features/permits/list/NewPermitWizard')).NewPermitWizard,
}));
const PermitLifecycleDialogs = lazy(async () => ({
  default: (await import('@/features/permits/list/PermitLifecycleDialogs')).PermitLifecycleDialogs,
}));

/**
 * Перелік дозволів (`/admin/permits`, PM-07; макет `screens-permits.js` §1): шапка з дією «New permit», смуга
 * розділів, показники (permits · active · ending within 120 days · not read by any sheet), пошук і фільтри типу
 * та стану, таблиця; клік по рядку відкриває сторінку дозволу (перехід, не шухляда). Дії життєвого циклу — у меню
 * рядка, лише з правом запису.
 *
 * ⚠ Будова `ListPage` (шапка → смуга розділів → показники → фільтри → таблиця) зібрана без нього самого, як у
 * `PermitTypesPage`: смуга розділів Permits стоїть МІЖ шапкою і показниками. Пошук, тип, стан і показник живуть
 * в адресі (`?q=`, `?type=`, `?state=`, `?stat=`, ФВ-14.29).
 */
export function PermitsPage(): JSX.Element {
  const navigate = useNavigate();
  const setParams = useUrlParamsSetter();
  const permits = useAllPermits();
  const [query] = useUrlState('q');
  const [type] = useUrlState('type');
  const [state] = useUrlState('state');
  const [statParam, setStat] = useUrlState('stat');
  const [wizardOpen, setWizardOpen] = useState(false);
  const [wizardMounted, setWizardMounted] = useState(false);
  const [target, setTarget] = useState<PermitActionTarget | null>(null);
  const [dialogsMounted, setDialogsMounted] = useState(false);

  const stat = asPermitStat(statParam);
  const mayWrite = useMayWritePermits();
  const all = useMemo(() => permits.data ?? [], [permits.data]);
  const hasPermits = all.length > 0;
  const lang = language();
  // ⛔ `undefined` лишається `undefined`: «ще не приїхало» ≠ «дозволів немає».
  const rows = useMemo(
    () =>
      permits.data === undefined
        ? undefined
        : filterPermits(permits.data, { needle: query ?? '', type: type ?? '', state: state ?? '', stat }),
    [permits.data, query, type, state, stat],
  );
  const stats = useMemo(() => listStats(all), [all]);
  const typeFilter = useMemo(() => typeOptions(all, lang), [all, lang]);

  const openWizard = (): void => {
    setWizardMounted(true);
    setWizardOpen(true);
  };
  const runAction = (item: PermitListItem, action: PermitAction): void => {
    setDialogsMounted(true);
    setTarget({ item, action });
  };
  // ⛔ Без права запису колонки дій немає взагалі (прихована, не вимкнена).
  const columns = useMemo(() => permitColumns(mayWrite ? runAction : undefined), [mayWrite]);

  return (
    <Stack gap="md" data-list-page="">
      <PageHeader
        title={t('nav.permits')}
        primary={mayWrite ? { label: t('permits.empty.new'), onClick: openWizard } : undefined}
      />
      <PermitsSubnav current="permits" />

      {/* `D15-06`: без жодного дозволу ні показників, ні фільтрів — лише порожній стан таблиці. */}
      {hasPermits && (
        <StatStrip
          label={t('permits.list.stats.aria')}
          active={stat}
          onSelect={setStat}
          items={[
            { id: 'all', label: t('permits.list.stat.all'), value: stats.all, filter: false },
            { id: 'active', label: t('permits.list.stat.active'), value: stats.active },
            {
              id: 'ending',
              label: t('permits.list.stat.ending', { days: EndingSoonDays }),
              value: stats.ending,
              tone: 'warning',
            },
            { id: 'free', label: t('permits.list.stat.free'), value: stats.free },
          ]}
        />
      )}

      {hasPermits && (
        <Box data-list-filters="">
          <FilterBar
            search={{
              label: t('permits.list.search.label'),
              placeholder: t('permits.list.search.placeholder'),
            }}
            filters={[
              { id: 'type', label: t('permits.list.filter.type'), options: typeFilter },
              {
                id: 'state',
                label: t('permits.list.filter.state'),
                options: FilterStates.map((value) => ({ value, label: permitStateLabel(value) })),
              },
            ]}
            clearLabel={t('filters.clear')}
          />
        </Box>
      )}

      <Box data-list-table="">
        <DataTable<PermitListItem>
          columns={columns}
          rows={rows}
          rowKey={(item) => String(item.id)}
          rowLabel={(item) => item.number}
          isPending={permits.isPending}
          error={permits.error}
          onRetry={() => void permits.refetch()}
          filtered={
            (query ?? '').trim().length > 0 || (type ?? '').length > 0 || (state ?? '').length > 0 || stat !== null
          }
          emptyTitle={t('permits.empty.title')}
          emptyHint={t('permits.list.empty.hint')}
          emptyHeadingOrder={2}
          emptyAction={mayWrite ? <Button onClick={openWizard}>{t('permits.empty.new')}</Button> : undefined}
          noMatchTitle={t('permits.list.noMatches')}
          // ⛔ Один перехід на всі параметри: кілька сеттерів поспіль губили б зміни (`useUrlState.ts`).
          onClearFilters={() => setParams({ q: null, type: null, state: null, stat: null })}
          clearFiltersLabel={t('filters.clear')}
          onRowClick={(item) => void navigate(permitHref(item.id))}
        />
      </Box>

      {wizardMounted && (
        <Suspense fallback={null}>
          <NewPermitWizard opened={wizardOpen} onClose={() => setWizardOpen(false)} />
        </Suspense>
      )}
      {dialogsMounted && (
        <Suspense fallback={null}>
          <PermitLifecycleDialogs target={target} onClose={() => setTarget(null)} />
        </Suspense>
      )}
    </Stack>
  );
}

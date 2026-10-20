import { Suspense, lazy, useMemo, useState, type JSX } from 'react';
import { Box, Button, Stack } from '@mantine/core';
import { useNavigate } from 'react-router-dom';
import { t } from '@/shared/i18n';
import { DataTable } from '@/shared/ui/DataTable';
import { FilterBar } from '@/shared/ui/FilterBar';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatStrip } from '@/shared/ui/StatStrip';
import { useUrlParamsSetter, useUrlState } from '@/shared/ui/useUrlState';
import type { PermitTypeListItem } from '@/features/permits/api/types';
import { useMayEditPermitTypes } from '@/features/permits/permitRights';
import { EditorParams } from '@/features/permits/typeEditor/editorActions';
import { typeEditorPath } from '@/features/permits/shell/permitPaths';
import { PermitsSubnav } from '@/features/permits/shell/PermitsSubnav';
import { typeColumns } from '@/features/permits/types/TypesListColumns';
import { categoryLabel, categoryOptions, filterTypes, listStats } from '@/features/permits/types/typesListModel';
import { useAllPermitTypes } from '@/features/permits/types/useTypes';

/*
 * ⚠ Майстер — лінивим чанком: `Modal` Mantine і поля форми не потрібні, доки людина не натисне
 * «New type», а чанк маршруту стоїть під бюджетом D-132.
 */
const NewTypeWizard = lazy(async () => ({
  default: (await import('@/features/permits/types/NewTypeWizard')).NewTypeWizard,
}));

/**
 * Типи пермітів (`/admin/permits/types`, PM-08a; макет `screens-permits.js` §3): шапка з єдиною
 * дією «New type», смуга розділів, смуга показників (types · tables · with unpublished changes),
 * пошук і фільтр категорій, таблиця; клік по рядку відкриває редактор типу.
 *
 * ⚠ Будова `ListPage` (шапка → смуга → фільтри → таблиця), зібрана без нього самого, як у
 * `RegistriesList`: смуга розділів Permits стоїть МІЖ шапкою і показниками, а в `ListPage` для неї
 * немає місця; `data-list-*` ті самі. Пошук, категорія й показник живуть в адресі (`?q=`, `?category=`,
 * `?stat=`, ФВ-14.29).
 */
export function PermitTypesPage(): JSX.Element {
  const navigate = useNavigate();
  const setParams = useUrlParamsSetter();
  const types = useAllPermitTypes();
  const [query] = useUrlState('q');
  const [category] = useUrlState('category');
  const [statParam, setStat] = useUrlState('stat');
  const [wizardOpen, setWizardOpen] = useState(false);
  const [wizardMounted, setWizardMounted] = useState(false);

  const stat = statParam === 'draft' ? statParam : null;
  const all = useMemo(() => types.data ?? [], [types.data]);
  const hasTypes = all.length > 0;
  // ⛔ `undefined` лишається `undefined`: «ще не приїхало» ≠ «типів немає».
  const rows = useMemo(
    () =>
      types.data === undefined
        ? undefined
        : filterTypes(types.data, { needle: query ?? '', category: category ?? '', stat }),
    [types.data, query, category, stat],
  );
  const stats = useMemo(() => listStats(all), [all]);
  // ⛔ У симуляції сеанс лише читає (`isSimulation`): створення типу заблоковане, як і решта записів.
  const mayCreate = useMayEditPermitTypes();
  const columns = useMemo(
    () =>
      typeColumns(
        mayCreate
          ? (item) => void navigate(`${typeEditorPath(item.code)}?${EditorParams.dialog}=delete-type`)
          : undefined,
      ),
    [mayCreate, navigate],
  );
  const openWizard = (): void => {
    setWizardMounted(true);
    setWizardOpen(true);
  };

  return (
    <Stack gap="md" data-list-page="">
      <PageHeader
        title={t('permits.types.title')}
        description={t('permits.types.subtitle')}
        primary={mayCreate ? { label: t('permits.types.new'), onClick: openWizard } : undefined}
      />
      <PermitsSubnav current="types" />

      {/* `D15-06`: без жодного типу ні показників, ні фільтрів — лише порожній стан таблиці. */}
      {hasTypes && (
        <StatStrip
          label={t('permits.types.stats.aria')}
          active={stat}
          onSelect={setStat}
          items={[
            { id: 'all', label: t('permits.types.stat.all'), value: stats.types, filter: false },
            { id: 'tables', label: t('permits.types.stat.tables'), value: stats.tables, filter: false },
            { id: 'draft', label: t('permits.types.stat.draft'), value: stats.draft, tone: 'warning' },
          ]}
        />
      )}

      {hasTypes && (
        <Box data-list-filters="">
          <FilterBar
            search={{
              label: t('permits.types.search.label'),
              placeholder: t('permits.types.search.placeholder'),
            }}
            filters={[
              {
                id: 'category',
                label: t('permits.types.filter.category'),
                options: categoryOptions(all).map((value) => ({ value, label: categoryLabel(value) })),
              },
            ]}
            clearLabel={t('filters.clear')}
          />
        </Box>
      )}

      <Box data-list-table="">
        <DataTable<PermitTypeListItem>
          columns={columns}
          rows={rows}
          rowKey={(item) => item.code}
          isPending={types.isPending}
          error={types.error}
          onRetry={() => void types.refetch()}
          filtered={(query ?? '').trim().length > 0 || (category ?? '').length > 0 || stat !== null}
          emptyTitle={t('permits.types.empty.title')}
          emptyHint={t('permits.types.empty.hint')}
          emptyAction={mayCreate ? <Button onClick={openWizard}>{t('permits.types.new')}</Button> : undefined}
          noMatchTitle={t('permits.types.noMatches')}
          // ⛔ Один перехід на всі параметри: кілька сеттерів поспіль губили б зміни (`useUrlState.ts`).
          onClearFilters={() => setParams({ q: null, category: null, stat: null })}
          clearFiltersLabel={t('filters.clear')}
          onRowClick={(item) => void navigate(typeEditorPath(item.code))}
        />
      </Box>

      {wizardMounted && (
        <Suspense fallback={null}>
          <NewTypeWizard opened={wizardOpen} types={all} onClose={() => setWizardOpen(false)} />
        </Suspense>
      )}
    </Stack>
  );
}

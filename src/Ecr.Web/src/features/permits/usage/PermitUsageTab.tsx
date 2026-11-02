import { useMemo, type JSX } from 'react';
import { Anchor, Group, Select, Text, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { Link, useSearchParams } from 'react-router-dom';
import { formatNumber } from '@/shared/format/number';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { DataTable, type DataTableColumn } from '@/shared/ui/DataTable';
import { TwoLine } from '@/shared/ui/TwoLine';
import { getPermitUsages, type PermitUsageItem } from '../api/usagesApi';
import {
  fieldLabel,
  parsePeriodParam,
  periodLabel,
  periodOptions,
  usageHref,
  usageKey,
  UsagePeriodParam,
  wherePrimary,
  whereSecondary,
} from './usageModel';

/** Ключ кешу під `['permits', …]`: дії над дозволом скидають увесь піддомен (`permitKeys.scope`). */
export function permitUsagesKey(permitId: number, periodKey: number | null) {
  return ['permits', 'usages', permitId, periodKey ?? 'all'] as const;
}

function columns(): readonly DataTableColumn<PermitUsageItem>[] {
  return [
    {
      key: 'document',
      label: t('permits.usage.col.document'),
      minWidth: 180,
      sortValue: (item) => item.documentKey.toLocaleLowerCase(),
      render: (item) => (
        <TwoLine
          primary={
            <Anchor component={Link} to={usageHref(item)} size="sm" data-usage-open={item.documentId}>
              {item.documentKey}
            </Anchor>
          }
          secondary={item.projectCode}
          mono
        />
      ),
    },
    {
      key: 'period',
      label: t('permits.usage.col.period'),
      sortValue: (item) => item.periodKey,
      render: (item) => (item.periodKey === null ? null : periodLabel(item.periodKey)),
    },
    {
      key: 'where',
      label: t('permits.usage.col.where'),
      minWidth: 200,
      sortValue: (item) => [wherePrimary(item).toLocaleLowerCase(), item.rowOrdinal],
      render: (item) => <TwoLine primary={wherePrimary(item)} secondary={whereSecondary(item)} />,
    },
    {
      key: 'field',
      label: t('permits.usage.col.field'),
      minWidth: 160,
      sortValue: (item) => fieldLabel(item).toLocaleLowerCase(),
      render: (item) => <TwoLine primary={fieldLabel(item)} secondary={item.fieldCode} mono />,
    },
  ];
}

export interface PermitUsageTabProps {
  readonly permitId: number;
}

/**
 * Вкладка «Used in» картки дозволу (PERMIT-USAGE, п. 3 плану; макет `screens-data.js` — вкладка «Where used» запису
 * довідника з лічильником і переліком місць): документи, період, аркуш · таблиця · рядок або шапка, де дозвіл обрано
 * значенням. Дані — `GET /permits/{id}/usages`; фільтр періоду — `?period=` в адресі (сервер фільтрує сам, лічильники
 * рахують усе під фільтром).
 *
 * ⚠ Варіанти фільтра — періоди з переліку БЕЗ фільтра (окремий запит, той самий кеш, що й початковий показ): після
 * вибору періоду перелік містить лише його, і решта варіантів інакше зникла б. Якщо перелік обрізано (`truncated`),
 * варіанти можуть бути неповні — про це каже смуга над таблицею.
 */
export function PermitUsageTab({ permitId }: PermitUsageTabProps): JSX.Element {
  const [params, setParams] = useSearchParams();
  const periodKey = parsePeriodParam(params.get(UsagePeriodParam));

  const all = useQuery({
    queryKey: permitUsagesKey(permitId, null),
    queryFn: () => getPermitUsages(permitId),
  });
  const filteredQuery = useQuery({
    queryKey: permitUsagesKey(permitId, periodKey),
    queryFn: () => getPermitUsages(permitId, periodKey === null ? {} : { periodKey }),
    enabled: periodKey !== null,
  });
  const current = periodKey === null ? all : filteredQuery;

  const options = useMemo(() => {
    const known = periodOptions(all.data?.items ?? []);
    if (periodKey !== null && !known.some((option) => option.value === String(periodKey))) {
      known.unshift({ value: String(periodKey), label: periodLabel(periodKey) });
    }

    return [{ value: '', label: t('permits.usage.allPeriods') }, ...known];
  }, [all.data, periodKey]);

  const setPeriod = (value: string | null): void => {
    const next = new URLSearchParams(params);
    if (value === null || value === '') next.delete(UsagePeriodParam);
    else next.set(UsagePeriodParam, value);
    setParams(next, { replace: true });
  };

  const data = current.data;

  return (
    <section aria-labelledby="pp-usage-title" className="pp-section" data-permit-usage={data === undefined ? 'loading' : 'ready'}>
      <Group justify="space-between" align="flex-end" wrap="wrap" gap="sm">
        <div>
          <Title order={2} size="h4" id="pp-usage-title">
            {t('permits.usage.title')}
          </Title>
          <Text size="sm" c="dimmed">
            {t('permits.usage.note')}
          </Text>
        </div>
        <Select
          id="pp-usage-period"
          label={t('permits.usage.period')}
          data={options}
          value={periodKey === null ? '' : String(periodKey)}
          onChange={setPeriod}
          allowDeselect={false}
          miw={220}
          comboboxProps={{ withinPortal: true }}
        />
      </Group>

      {data !== undefined && (
        <Text size="sm" data-usage-summary="">
          {t('permits.usage.summary', {
            documents: formatNumber(data.documentCount),
            links: formatNumber(data.linkCount),
          })}
        </Text>
      )}

      {data?.truncated === true && (
        <Banner
          tone="info"
          title={t('permits.usage.truncatedTitle')}
          text={t('permits.usage.truncatedText', {
            shown: formatNumber(data.items.length),
            links: formatNumber(data.linkCount),
          })}
        />
      )}

      <DataTable<PermitUsageItem>
        columns={columns()}
        rows={data?.items}
        rowKey={usageKey}
        isPending={current.isPending}
        error={current.error}
        onRetry={() => void current.refetch()}
        caption={t('permits.usage.caption')}
        emptyTitle={t('permits.usage.emptyTitle')}
        emptyHint={t('permits.usage.emptyHint')}
        emptyHeadingOrder={3}
        filtered={periodKey !== null}
        noMatchTitle={t('permits.usage.noMatchTitle')}
        noMatchHint={t('permits.usage.noMatchHint')}
        onClearFilters={() => setPeriod(null)}
        clearFiltersLabel={t('permits.usage.showAll')}
      />
    </section>
  );
}

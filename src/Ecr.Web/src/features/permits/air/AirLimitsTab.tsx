import { Fragment, useMemo, useState, type JSX } from 'react';
import { Button, SegmentedControl, Text, TextInput, Title } from '@mantine/core';
import { Link } from 'react-router-dom';
import { t } from '@/shared/i18n';
import { formatNumber } from '@/shared/format/number';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { Banner } from '@/shared/ui/Banner';
import { permitImportPath } from '../shell/permitPaths';
import { cell, filterSources, num, sourceKey, sourceTypeLabel, sourceTypeShort } from './airCardModel';
import type { AirLimitsPermit, AirSource, AirTotal } from './airTypes';
import { useAirLimits } from './useAirLimits';
import './air.css';

type View = 'sources' | 'totals';

function SubstancesTable({ source, id }: { readonly source: AirSource; readonly id: string }): JSX.Element {
  return (
    <table className="pa-sub" id={id} aria-label={t('permits.air.substances.aria', { nae: source.naeCode })}>
      <thead>
        <tr>
          <th scope="col">{t('permits.air.substances.code')}</th>
          <th scope="col">{t('permits.air.substances.name')}</th>
          <th scope="col" className="pa-num">
            {t('permits.air.substances.gs')}
          </th>
          <th scope="col" className="pa-num">
            {t('permits.air.substances.mg')}
          </th>
          <th scope="col" className="pa-num">
            {t('permits.air.substances.ty')}
          </th>
        </tr>
      </thead>
      <tbody>
        {source.substances.map((substance) => (
          <tr key={substance.id}>
            <td className="pa-code">{substance.substanceCode}</td>
            <td>{cell(substance.substanceName)}</td>
            <td className="pa-num">{num(substance.gramsPerSecond)}</td>
            <td className="pa-num">{num(substance.mgPerNm3)}</td>
            <td className="pa-num">{num(substance.tonsPerYear)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function SourcesTable({ sources }: { readonly sources: readonly AirSource[] }): JSX.Element {
  const [open, setOpen] = useState<ReadonlySet<string>>(new Set());

  const toggle = (key: string): void =>
    setOpen((current) => {
      const next = new Set(current);
      if (next.has(key)) next.delete(key);
      else next.add(key);

      return next;
    });

  return (
    <div className="pa-scroll" role="region" aria-label={t('permits.air.sources.aria')} tabIndex={0}>
      <table className="pa-table">
        <thead>
          <tr>
            <th scope="col" className="pa-toggle">
              <span className="pa-sr">{t('permits.air.sources.toggle')}</span>
            </th>
            <th scope="col">{t('permits.air.sources.nae')}</th>
            <th scope="col">{t('permits.air.sources.source')}</th>
            <th scope="col">{t('permits.air.sources.point')}</th>
            <th scope="col">{t('permits.air.sources.type')}</th>
            <th scope="col" className="pa-num">
              {t('permits.air.sources.quantity')}
            </th>
            <th scope="col" className="pa-num">
              {t('permits.air.sources.hours')}
            </th>
            <th scope="col" className="pa-num">
              {t('permits.air.sources.height')}
            </th>
            <th scope="col" className="pa-num">
              {t('permits.air.sources.diameter')}
            </th>
            <th scope="col" className="pa-num">
              {t('permits.air.sources.velocity')}
            </th>
            <th scope="col" className="pa-num">
              {t('permits.air.sources.volume')}
            </th>
            <th scope="col" className="pa-num">
              {t('permits.air.sources.temperature')}
            </th>
            <th scope="col" className="pa-num">
              {t('permits.air.sources.substances')}
            </th>
          </tr>
        </thead>
        <tbody>
          {sources.map((source) => {
            const key = sourceKey(source);
            const expanded = open.has(key);
            const panel = `pa-sub-${key}`;

            return (
              <Fragment key={key}>
                <tr data-open={expanded} data-testid="air-source-row">
                  <td className="pa-toggle">
                    <button
                      type="button"
                      className="pa-expander"
                      aria-expanded={expanded}
                      aria-controls={expanded ? panel : undefined}
                      aria-label={t('permits.air.sources.expand', { nae: source.naeCode })}
                      onClick={() => toggle(key)}
                    >
                      <span aria-hidden="true">{expanded ? '▾' : '▸'}</span>
                    </button>
                  </td>
                  <th scope="row" className="pa-code">
                    {source.naeCode}
                  </th>
                  <td>{cell(source.sourceName)}</td>
                  <td>{cell(source.emissionPointName)}</td>
                  <td title={sourceTypeLabel(source.sourceType)}>{sourceTypeShort(source.sourceType)}</td>
                  <td className="pa-num">{num(source.quantity)}</td>
                  <td className="pa-num">{num(source.hoursPerYear)}</td>
                  <td className="pa-num">{num(source.height)}</td>
                  <td className="pa-num">{num(source.diameter)}</td>
                  <td className="pa-num">{num(source.velocity)}</td>
                  <td className="pa-num">{num(source.volume)}</td>
                  <td className="pa-num">{num(source.temperature)}</td>
                  <td className="pa-num">{formatNumber(source.substances.length)}</td>
                </tr>
                {expanded && (
                  <tr className="pa-subrow">
                    <td />
                    <td colSpan={12}>
                      <SubstancesTable source={source} id={panel} />
                    </td>
                  </tr>
                )}
              </Fragment>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function TotalsTable({ totals }: { readonly totals: readonly AirTotal[] }): JSX.Element {
  return (
    <div className="pa-scroll" role="region" aria-label={t('permits.air.totals.aria')} tabIndex={0}>
      <table className="pa-table">
        <thead>
          <tr>
            <th scope="col">{t('permits.air.substances.code')}</th>
            <th scope="col">{t('permits.air.substances.name')}</th>
            <th scope="col" className="pa-num">
              {t('permits.air.totals.sources')}
            </th>
            <th scope="col" className="pa-num">
              {t('permits.air.substances.gs')}
            </th>
            <th scope="col" className="pa-num">
              {t('permits.air.substances.ty')}
            </th>
          </tr>
        </thead>
        <tbody>
          {totals.map((total) => (
            <tr key={total.substanceCode}>
              <th scope="row" className="pa-code">
                {total.substanceCode}
              </th>
              <td>{cell(total.substanceName)}</td>
              <td className="pa-num">{formatNumber(total.sources)}</td>
              <td className="pa-num">{num(total.gramsPerSecond)}</td>
              <td className="pa-num">{num(total.tonsPerYear)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function Card({ data, permitId, mayImport }: { readonly data: AirLimitsPermit; readonly permitId: number; readonly mayImport: boolean }): JSX.Element {
  const [view, setView] = useState<View>('sources');
  const [query, setQuery] = useState('');
  const shown = useMemo(() => filterSources(data.sources, query), [data.sources, query]);
  const empty = data.sources.length === 0;

  return (
    <div className="pa-card">
      <div className="pa-head">
        <Title order={2} size="h4">
          {t('permits.air.title')}
        </Title>
        <Text size="sm" c="dimmed" data-testid="air-counts">
          {t('permits.air.counts', { sources: formatNumber(data.sources.length), rows: formatNumber(data.substanceRows) })}
        </Text>
        {mayImport && (
          <Button component={Link} to={permitImportPath(permitId)} className="pa-import">
            {t('permits.air.import')}
          </Button>
        )}
      </div>
      {empty ? (
        <Banner
          tone="info"
          title={t('permits.air.empty.title')}
          text={mayImport ? t('permits.air.empty.text') : t('permits.air.empty.readOnly')}
        />
      ) : (
        <>
          <div className="pa-tools">
            <SegmentedControl
              value={view}
              onChange={(value) => setView(value === 'totals' ? 'totals' : 'sources')}
              aria-label={t('permits.air.view.aria')}
              data={[
                { value: 'sources', label: t('permits.air.view.sources') },
                { value: 'totals', label: t('permits.air.view.totals') },
              ]}
            />
            {view === 'sources' && (
              <TextInput
                className="pa-filter"
                aria-label={t('permits.air.filter.label')}
                placeholder={t('permits.air.filter.placeholder')}
                value={query}
                onChange={(event) => setQuery(event.currentTarget.value)}
              />
            )}
          </div>
          {view === 'totals' ? (
            <TotalsTable totals={data.totals} />
          ) : shown.length === 0 ? (
            <Text size="sm" role="status">
              {t('permits.air.filter.none')}
            </Text>
          ) : (
            <SourcesTable sources={shown} />
          )}
        </>
      )}
    </div>
  );
}

export interface AirLimitsTabProps {
  readonly permitId: number;
  /** Право `Registry.EditData` і не симуляція: без нього кнопки імпорту немає (екран лише читає). */
  readonly mayImport: boolean;
}

/**
 * Вкладка «Air limits» картки дозволу (макет «Permit»): джерела (НАЕ) з розкривними речовинами, підсумки по речовинах,
 * пошук, «Import from Excel…».
 */
export function AirLimitsTab({ permitId, mayImport }: AirLimitsTabProps): JSX.Element {
  const query = useAirLimits(permitId);

  return (
    <AsyncBoundary<AirLimitsPermit>
      isPending={query.isPending}
      error={query.error}
      data={query.data}
      skeleton="table"
      onRetry={() => void query.refetch()}
    >
      {(data) => <Card data={data} permitId={permitId} mayImport={mayImport} />}
    </AsyncBoundary>
  );
}

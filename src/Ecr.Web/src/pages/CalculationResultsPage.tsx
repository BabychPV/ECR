import { useMemo, useState, type JSX } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { Badge, Button, Group, Select, Stack, Text, TextInput } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { DocumentSummary } from '@/api/types';
import {
  fetchCalculationYear,
  type CalculationYearMonth,
  type CalculationYearRow,
  type CalculationYearView,
} from '@/features/calcResults/api';
import { CalcResultsExport } from '@/features/calcResults/CalcResultsExport';
import {
  CalcResultsGrid,
  monthStateText,
  totalText,
  valueText,
  type ResultLabels,
} from '@/features/calcResults/CalcResultsGrid';
import {
  allGroupKeys,
  documentHref,
  filterRows,
  FormulaMethodology,
  gridItems,
  hasProblem,
  latestRunMonth,
  runCounts,
} from '@/features/calcResults/model';
import '@/features/calcResults/calcResults.css';
import { isYearLayout } from '@/features/documents/yearLayout';
import { formatDate, formatDateTime } from '@/shared/format';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { Banner } from '@/shared/ui/Banner';
import { DetailDrawer, useDetailPanel } from '@/shared/ui/DetailDrawer';
import { KeyValue } from '@/shared/ui/KeyValue';
import { PageHeader } from '@/shared/ui/PageHeader';
import { useUrlNumber, useUrlState } from '@/shared/ui/useUrlState';

/** Префікс `?panel=` шторки значення: `cell:<номер місяця>:<id виходу>`. */
const CellPanel = 'cell:';

/** Підписи рядків огляду мовою користувача. */
function labelsOf(view: CalculationYearView): ResultLabels {
  const monthNames = new Map<number, string>();
  const month = (m: number): string => {
    let name = monthNames.get(m);
    if (name === undefined) {
      name = formatDate(new Date(Date.UTC(view.year, m - 1, 15)), { month: 'short', timeZone: 'UTC' });
      monthNames.set(m, name);
    }
    return name;
  };
  const equipment = (row: CalculationYearRow): string => {
    const e = view.equipment[row.equipment];
    if (e === undefined) return '';
    if (e.name !== null && e.name !== undefined && e.name !== '') return e.name;
    const label = localized(e.labelL10n);
    if (label !== '') return label;
    return e.rowKey ?? t('calcResults.tableLevel');
  };
  const substanceOf = (row: CalculationYearRow) =>
    row.substance === null || row.substance === undefined ? undefined : view.substances[row.substance];
  const outputText = (row: CalculationYearRow): string => {
    if (row.column !== null && row.column !== undefined) {
      const column = view.columns[row.column];
      return localized(column?.headerL10n) || row.outputCode;
    }
    const substance = substanceOf(row);
    return substance === undefined
      ? row.outputCode
      : `${substance.code} · ${localized(substance.nameL10n) || substance.code}`;
  };
  return {
    month,
    equipment,
    outputText,
    output: (row) => {
      const substance = substanceOf(row);
      if ((row.column !== null && row.column !== undefined) || substance === undefined) {
        return (
          <>
            <span>{outputText(row)}</span> <span className="ecr-cr-muted">{row.outputCode}</span>
          </>
        );
      }
      return (
        <>
          <b>{substance.code}</b> <span className="ecr-cr-muted">{localized(substance.nameL10n)} · {row.outputCode}</span>
        </>
      );
    },
    unit: (row) => {
      const unit = row.unit === null || row.unit === undefined ? undefined : view.units[row.unit];
      return unit === undefined ? '' : localized(unit.symbolL10n) || unit.code;
    },
  };
}

/**
 * «Calculation results» — усі розраховані числа документа за рік, місяці поруч (`/documents/:id/results`, макет
 * `docs/design/hybrid/calc-results/screen-calc-results.js`, CALC-RESULTS.md).
 *
 * ⚠ Розкладка шаблону (D-474): `PerMonthCopies` — «огляд року», заголовок місяця відкриває документ на ньому;
 * `YearWithMonthColumns` — заголовок місяця обирає «місяць результатів» (`?month=`), і «до документа» веде на
 * `?periodKey` цього місяця: розраховані колонки сітки показують числа саме його.
 */
export function CalculationResultsPage(): JSX.Element {
  const documentId = Number(useParams().id ?? 0);
  const navigate = useNavigate();
  const [urlYear] = useUrlNumber('year');
  const year = urlYear ?? new Date().getFullYear();
  const [picked, setPicked] = useUrlNumber('month');
  const [query, setQuery] = useUrlState('q');
  const [sheet, setSheet] = useUrlState('sheet');
  const [methodology, setMethodology] = useUrlState('methodology');
  const [substance, setSubstance] = useUrlState('substance');
  const [problems, setProblems] = useUrlState('problems');
  const [dialog, setDialog] = useUrlState('dialog');
  const [panel, setPanel] = useDetailPanel();
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(new Set());

  const summary = useQuery({
    queryKey: ['document', documentId, 'base'],
    queryFn: () => apiFetch<DocumentSummary>(`/api/v1/documents/${String(documentId)}`),
  });
  const results = useQuery({
    queryKey: queryKeys.document.calculationYear(documentId, year),
    queryFn: () => fetchCalculationYear(documentId, year),
  });

  const yearLayout = isYearLayout(summary.data?.templateLayout);
  const documentKey = summary.data?.businessKey ?? `#${String(documentId)}`;
  const view = results.data;
  const labels = useMemo(() => (view === undefined ? null : labelsOf(view)), [view]);
  const filters = {
    query: query ?? '',
    sheet: sheet ?? '',
    methodology: methodology ?? '',
    substance: substance ?? '',
    problemsOnly: problems === '1',
  };
  // Фільтри — рядки адреси: ключ із них перераховує перелік лише тоді, коли вони справді змінилися.
  const filterKey = JSON.stringify(filters);
  const rows = useMemo(
    () => (view === undefined || labels === null ? [] : filterRows(view, JSON.parse(filterKey) as typeof filters, labels.equipment)),
    [view, labels, filterKey],
  );
  const items = useMemo(() => (view === undefined ? [] : gridItems(view, rows, collapsed)), [view, rows, collapsed]);
  const pickedMonth = view === undefined || !yearLayout ? null : (picked ?? latestRunMonth(view));
  const filteredAny = filters.query !== '' || filters.sheet !== '' || filters.methodology !== '' || filters.substance !== '' || filters.problemsOnly;

  const periodOf = (m: number): number => year * 100 + m;
  const backHref = `/documents/${String(documentId)}${pickedMonth === null ? '' : `?periodKey=${String(periodOf(pickedMonth))}`}`;

  const onMonth = (month: CalculationYearMonth): void => {
    if (yearLayout) setPicked(month.month);
    else void navigate(`/documents/${String(documentId)}?periodKey=${String(month.periodKey)}`);
  };

  const clearFilters = (): void => {
    setQuery(null);
    setSheet(null);
    setMethodology(null);
    setSubstance(null);
    setProblems(null);
  };

  const options = useMemo(() => {
    if (view === undefined) return { sheets: [], methodologies: [], substances: [] };
    const sheets = new Map<string, string>();
    for (const table of view.tables) {
      if (table.tableDefId !== 0) sheets.set(String(table.sheetDefId), localized(table.sheetNameL10n) || table.sheetCode);
    }
    const methodologies = view.methodologies.map((m) => ({ value: m.code, label: localized(m.nameL10n) || m.code }));
    if (view.rows.some((r) => r.column !== null && r.column !== undefined)) {
      methodologies.push({ value: FormulaMethodology, label: t('calcResults.templateFormula') });
    }
    return {
      sheets: [...sheets].map(([value, label]) => ({ value, label })),
      methodologies,
      substances: view.substances.map((s) => ({ value: s.code, label: `${s.code} · ${localized(s.nameL10n) || s.code}` })),
    };
  }, [view]);

  const panelCell = useMemo(() => {
    if (view === undefined || panel === null || !panel.startsWith(CellPanel)) return null;
    const rest = panel.slice(CellPanel.length);
    const colon = rest.indexOf(':');
    const month = Number(rest.slice(0, colon));
    const row = view.rows.find((r) => r.id === rest.slice(colon + 1));
    return row === undefined || !Number.isInteger(month) || month < 1 || month > 12 ? null : { row, month };
  }, [view, panel]);

  const counts = view === undefined ? null : runCounts(view);
  const problemCount = view === undefined ? 0 : rows.filter((r) => hasProblem(view, r)).length;

  return (
    <Stack gap="sm">
      <PageHeader
        back={{ label: documentKey, href: backHref }}
        title={t('calcResults.title')}
        description={t('calcResults.subtitle', { document: documentKey, year })}
        primary={{ label: t('calcResults.export'), onClick: () => setDialog('export-results'), disabled: view === undefined }}
        more={[
          { label: t('calcResults.collapseAll'), onClick: () => setCollapsed(view === undefined ? new Set() : allGroupKeys(view, rows)) },
          { label: t('calcResults.expandAll'), onClick: () => setCollapsed(new Set()) },
        ]}
      />

      {yearLayout && pickedMonth !== null && labels !== null && (
        <Banner
          title={t('calcResults.yearNoteTitle')}
          text={t('calcResults.yearNote', { month: `${labels.month(pickedMonth)} ${String(year)}` })}
          testId="cr-year-note"
        />
      )}

      <AsyncBoundary<CalculationYearView>
        isPending={results.isPending}
        error={results.error}
        data={results.data}
        isEmpty={(data) => data.rows.length === 0}
        emptyTitle={t('calcResults.emptyTitle')}
        emptyHint={t('calcResults.emptyHint', { year })}
        emptyAction={
          <Button component={Link} to={`/documents/${String(documentId)}`} variant="default">
            {t('calcResults.openDocument')}
          </Button>
        }
        skeleton="table"
        onRetry={() => void results.refetch()}
      >
        {(data) => (
          <Stack gap="xs">
            <Group gap="xs" align="flex-end" wrap="wrap">
              <TextInput
                label={t('calcResults.search')}
                placeholder={t('calcResults.searchPlaceholder')}
                value={query ?? ''}
                onChange={(event) => setQuery(event.currentTarget.value === '' ? null : event.currentTarget.value)}
                size="xs"
                miw={220}
                data-testid="cr-search"
              />
              <Select label={t('calcResults.sheets')} data={options.sheets} value={sheet} onChange={setSheet} clearable size="xs" miw={200} />
              <Select label={t('calcResults.methodologies')} data={options.methodologies} value={methodology} onChange={setMethodology} clearable size="xs" miw={220} />
              <Select label={t('calcResults.substances')} data={options.substances} value={substance} onChange={setSubstance} clearable searchable size="xs" miw={200} />
              <Button
                size="xs"
                variant={filters.problemsOnly ? 'light' : 'default'}
                aria-pressed={filters.problemsOnly}
                onClick={() => setProblems(filters.problemsOnly ? null : '1')}
                data-testid="cr-problems"
              >
                {t('calcResults.problemsOnly')}
              </Button>
            </Group>

            <div className="ecr-cr-legend" data-testid="cr-legend">
              <span><i className="ecr-cr-sw ecr-cr-sw--stale" />{t('calcResults.legend.outOfDate')}</span>
              <span><i className="ecr-cr-sw ecr-cr-sw--none" />{t('calcResults.legend.noValue')}</span>
              {yearLayout && <span><i className="ecr-cr-sw ecr-cr-sw--pick" />{t('calcResults.legend.picked')}</span>}
              <span>{t('calcResults.legend.additive')}</span>
              {data.freshnessHidden && <span>{t('calcResults.legend.freshnessHidden')}</span>}
            </div>

            {rows.length === 0 ? (
              <Stack align="flex-start" gap="xs" data-testid="cr-no-match">
                <Text fw={600}>{t('calcResults.noMatchTitle')}</Text>
                <Text size="sm" c="dimmed">{t('calcResults.noMatchHint')}</Text>
                <Button size="xs" variant="default" onClick={clearFilters}>{t('calcResults.clearFilters')}</Button>
              </Stack>
            ) : (
              labels !== null && (
                <CalcResultsGrid
                  view={data}
                  items={items}
                  collapsed={collapsed}
                  labels={labels}
                  pickedMonth={pickedMonth}
                  onToggle={(key) =>
                    setCollapsed((current) => {
                      const next = new Set(current);
                      if (next.has(key)) next.delete(key);
                      else next.add(key);
                      return next;
                    })
                  }
                  onMonth={onMonth}
                  onValue={(row, i) => setPanel(`${CellPanel}${String(i + 1)}:${row.id}`)}
                />
              )
            )}

            <div className="ecr-cr-foot" data-testid="cr-foot">
              <span>{t('calcResults.footShown', { shown: rows.length, total: data.rows.length })}</span>
              <span>{t('calcResults.footProblems', { count: problemCount })}</span>
              <span className="ecr-cr-sp" />
              {counts !== null && (
                <span>{t('calcResults.footRuns', { current: counts.current, outOfDate: counts.outOfDate, none: counts.none })}</span>
              )}
            </div>
          </Stack>
        )}
      </AsyncBoundary>

      {view !== undefined && labels !== null && panelCell !== null && (
        <ValueDrawer view={view} labels={labels} row={panelCell.row} month={panelCell.month} onClose={() => setPanel(null)} />
      )}

      <CalcResultsExport
        opened={dialog === 'export-results'}
        onClose={() => setDialog(null)}
        documentId={documentId}
        documentKey={documentKey}
        year={year}
        filteredRowIds={rows.map((r) => r.id)}
        filtered={filteredAny}
      />
    </Stack>
  );
}

/** Шторка значення: звідки число і перехід до документа на його аркуші/таблиці/місяці. */
function ValueDrawer({
  view,
  labels,
  row,
  month,
  onClose,
}: {
  readonly view: CalculationYearView;
  readonly labels: ResultLabels;
  readonly row: CalculationYearRow;
  readonly month: number;
  readonly onClose: () => void;
}): JSX.Element {
  const info = view.months[month - 1];
  const value = valueText(row.values[month - 1]);
  const unit = labels.unit(row);
  const methodology = row.methodology === null || row.methodology === undefined ? null : view.methodologies[row.methodology];
  const unitDto = row.unit === null || row.unit === undefined ? undefined : view.units[row.unit];
  const total = totalText(row);
  const monthText = `${labels.month(month)} ${String(view.year)}`;
  const equipment = view.equipment[row.equipment];

  return (
    <DetailDrawer
      panelId={`${CellPanel}${String(month)}:${row.id}`}
      title={`${labels.outputText(row)} · ${monthText}`}
      subtitle={`${labels.equipment(row)}${equipment?.idCode ? ` · ${equipment.idCode}` : ''}`}
      closeLabel={t('calcResults.close')}
      onClose={onClose}
      footer={
        <Button component={Link} to={documentHref(view, info?.periodKey ?? view.year * 100 + month, row)} data-testid="cr-open-source">
          {t('calcResults.openSource')}
        </Button>
      }
    >
      <Stack gap="md">
        {info?.runState === 'outOfDate' && (
          <Banner tone="warning" title={t('calcResults.outOfDateTitle')} text={t('calcResults.outOfDateText')} />
        )}
        <KeyValue
          items={[
            {
              label: t('calcResults.drawer.value'),
              value: value === null ? '—' : `${value} ${unit}`.trim(),
              mono: true,
            },
            { label: t('calcResults.drawer.month'), value: <>{monthText} {info !== undefined && <Badge variant="light" size="sm">{monthStateText(info)}</Badge>}</> },
            {
              label: t('calcResults.drawer.methodology'),
              value: methodology === null || methodology === undefined ? t('calcResults.templateFormula') : localized(methodology.nameL10n) || methodology.code,
              hint: methodology === null || methodology === undefined ? undefined : t('calcResults.drawer.version', { version: methodology.version }),
            },
            { label: t('calcResults.drawer.output'), value: row.outputCode, mono: true },
            {
              label: t('calcResults.drawer.calculated'),
              value: info?.calculatedAt === null || info?.calculatedAt === undefined ? '—' : formatDateTime(info.calculatedAt),
            },
            {
              label: t('calcResults.drawer.yearTotal'),
              value:
                unitDto?.additive === true && total !== null
                  ? t('calcResults.drawer.yearTotalValue', { value: `${total} ${unit}`.trim(), n: row.monthsIncluded })
                  : t('calcResults.drawer.notAdditive'),
            },
          ]}
        />
      </Stack>
    </DetailDrawer>
  );
}

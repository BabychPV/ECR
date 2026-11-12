import { useCallback, useEffect, useMemo, useRef, useState, type JSX } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { Badge, Button, Group, Select, Stack, Text, TextInput } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { keepPreviousData, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { DocumentSummary } from '@/api/types';
import {
  fetchCalculationYearRows,
  fetchCalculationYearSummary,
  filterParams,
  FormulaMethodology,
  isFiltered,
  isStaleCursor,
  PageSize,
  type CalculationYearMonth,
  type CalculationYearRows,
  type CalculationYearSummary,
  type ResultFilters,
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
  documentHref,
  gridItems,
  latestRunMonth,
  pageCursors,
  resolvePage,
  runCounts,
  sheetKey,
  type LoadedTable,
  type ResultRow,
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

/** Пауза пошуку перед запитом (мс): зведення й сторінки перераховуються сервером. */
const SearchDelay = 300;

/** Підписи рядків огляду мовою користувача. */
function labelsOf(year: number): ResultLabels {
  const monthNames = new Map<number, string>();
  const month = (m: number): string => {
    let name = monthNames.get(m);
    if (name === undefined) {
      name = formatDate(new Date(Date.UTC(year, m - 1, 15)), { month: 'short', timeZone: 'UTC' });
      monthNames.set(m, name);
    }
    return name;
  };
  const equipment = (row: ResultRow): string => {
    const e = row.equipment;
    if (e.name !== null && e.name !== undefined && e.name !== '') return e.name;
    const label = localized(e.labelL10n);
    if (label !== '') return label;
    return e.rowKey ?? t('calcResults.tableLevel');
  };
  const outputText = (row: ResultRow): string => {
    if (row.column !== null) return localized(row.column.headerL10n) || row.outputCode;
    const substance = row.substance;
    return substance === null ? row.outputCode : `${substance.code} · ${localized(substance.nameL10n) || substance.code}`;
  };
  return {
    month,
    equipment,
    outputText,
    output: (row) => {
      const substance = row.substance;
      if (row.column !== null || substance === null) {
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
    unit: (row) => (row.unit === null ? '' : localized(row.unit.symbolL10n) || row.unit.code),
  };
}

/** Сторінки таблиць, запитані за поточних фільтрів (зміна фільтрів скидає дочитане). */
interface PageState {
  readonly filterKey: string;
  readonly pages: ReadonlyMap<string, number>;
}

/**
 * «Calculation results» — усі розраховані числа документа за рік, місяці поруч (`/documents/:id/results`, макет
 * `docs/design/hybrid/calc-results/screen-calc-results.js`, CALC-RESULTS.md).
 *
 * ⚠ Зведення першим (місяці, таблиці з лічильниками — кілобайти), рядки — сторінками по 200 лише розгорнутих таблиць
 * (за замовчуванням розгорнута перша); рядок «ще» в кінці таблиці дочитує наступну сторінку, щойно потрапляє у видиму
 * область. Фільтри застосовує сервер — те саме правило для зведення, сторінок і експорту «як відфільтровано».
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
  const [openState, setOpenState] = useState<ReadonlySet<string> | null>(null);
  const [debouncedQuery] = useDebouncedValue(query ?? '', SearchDelay);

  const filters: ResultFilters = {
    query: debouncedQuery,
    sheet: sheet ?? '',
    methodology: methodology ?? '',
    substance: substance ?? '',
    problemsOnly: problems === '1',
  };
  // Ключ кешу й скидання сторінок — з параметрів запиту: той самий набір фільтрів дає той самий ключ.
  const filterKey = filterParams(filters).toString();
  const filtered = isFiltered(filters);

  const documentQuery = useQuery({
    queryKey: ['document', documentId, 'base'],
    queryFn: () => apiFetch<DocumentSummary>(`/api/v1/documents/${String(documentId)}`),
  });
  const summary = useQuery({
    queryKey: queryKeys.document.calculationYearSummary(documentId, year, filterKey),
    queryFn: () => fetchCalculationYearSummary(documentId, year, filters),
    // Зміна фільтра не гасить сітку до відповіді: видно попереднє зведення.
    placeholderData: keepPreviousData,
  });

  const data = summary.data;
  const tables = useMemo(
    () => (data === undefined ? [] : filtered ? data.tables.filter((table) => table.matched > 0) : data.tables),
    [data, filtered],
  );
  const open = useMemo<ReadonlySet<string>>(
    () => openState ?? new Set(tables[0] === undefined ? [] : [tables[0].key]),
    [openState, tables],
  );

  const [pageState, setPageState] = useState<PageState>({ filterKey, pages: new Map() });
  const pages = pageState.filterKey === filterKey ? pageState.pages : new Map<string, number>();
  // Ключ сторінки — її позиція (i × PageSize), а запит — за курсором попередньої: сторінка i з'являється в запитах,
  // лише коли i − 1 прочитано й вона не остання (рендер після її відповіді бачить її в кеші).
  const queryClient = useQueryClient();
  const rowsKey = (table: string, index: number) =>
    queryKeys.document.calculationYearRows(documentId, year, table, filterKey, index * PageSize);
  const requests = tables
    .filter((table) => open.has(table.key))
    .flatMap((table) =>
      pageCursors(pages.get(table.key) ?? 1, (i) => queryClient.getQueryData<CalculationYearRows>(rowsKey(table.key, i))).map(
        (cursor, index) => ({ table, index, cursor }),
      ),
    );
  const pageQueries = useQueries({
    queries: requests.map(({ table, index, cursor }) => ({
      queryKey: rowsKey(table.key, index),
      queryFn: () => fetchCalculationYearRows(documentId, year, table.key, filters, cursor),
      enabled: data !== undefined && !summary.isPlaceholderData,
    })),
  });

  // Рядки таблиці — сторінки підряд від початку; на першій ще не прочитаній — зупинка (і «ще» в кінці таблиці).
  const loaded = new Map<string, LoadedTable>();
  const loading = new Set<string>();
  const failed = new Map<string, () => void>();
  const stale: string[] = [];
  const stopped = new Set<string>();
  requests.forEach(({ table, cursor }, i) => {
    const result = pageQueries[i];
    if (result === undefined || stopped.has(table.key)) return;
    if (result.data === undefined) {
      stopped.add(table.key);
      // Курсор, виданий до перерахунку, сервер відхилив — таблиця читається з початку (ефект нижче), а не «помилка».
      if (result.isError && isStaleCursor(result.error, cursor)) {
        stale.push(table.key);
        loading.add(table.key);
      } else if (result.isError) failed.set(table.key, () => void result.refetch());
      else loading.add(table.key);
      return;
    }
    loaded.set(table.key, {
      rows: [...(loaded.get(table.key)?.rows ?? []), ...resolvePage(table, result.data)],
      total: result.data.total,
    });
  });
  const failedRef = useRef(failed);
  failedRef.current = failed;

  // Перезапуск таблиці із застарілим курсором: лишається одна сторінка, наступні сторінки прибираються з кешу (їхні
  // курсори з того самого старого набору), перша читається заново, зведення (лічильники) — теж.
  const staleKey = stale.join('\n');
  useEffect(() => {
    if (staleKey === '') return;
    const restarted = staleKey.split('\n');
    setPageState((state) => {
      const next = new Map(state.filterKey === filterKey ? state.pages : []);
      for (const table of restarted) next.set(table, 1);
      return { filterKey, pages: next };
    });
    for (const table of restarted) {
      const first = queryKeys.document.calculationYearRows(documentId, year, table, filterKey, 0);
      queryClient.removeQueries({
        queryKey: first.slice(0, -1),
        predicate: (query) => query.queryKey[first.length - 1] !== 0,
      });
      void queryClient.resetQueries({ queryKey: first, exact: true });
    }
    void queryClient.invalidateQueries({ queryKey: queryKeys.document.calculationYearSummary(documentId, year, filterKey) });
  }, [staleKey, filterKey, documentId, year, queryClient]);

  const onMore = useCallback(
    (table: string, count: number) => {
      // Сторінка, що не прочиталася, — повтор цієї ж сторінки, а не наступна.
      const retry = failedRef.current.get(table);
      if (retry !== undefined) {
        retry();
        return;
      }
      setPageState((state) => {
        const base = state.filterKey === filterKey ? state.pages : new Map<string, number>();
        // Ще одна сторінка за прочитаними (сервер віддає повні сторінки, крім останньої; коротша — теж «прочитана»).
        const want = Math.ceil(count / PageSize) + 1;
        if ((base.get(table) ?? 1) >= want) return state.filterKey === filterKey ? state : { filterKey, pages: base };
        return { filterKey, pages: new Map(base).set(table, want) };
      });
    },
    [filterKey],
  );

  const yearLayout = isYearLayout(documentQuery.data?.templateLayout);
  const documentKey = documentQuery.data?.businessKey ?? `#${String(documentId)}`;
  const labels = useMemo(() => labelsOf(year), [year]);
  const items = gridItems(tables, loaded, collapsed, open);
  const pickedMonth = data === undefined || !yearLayout ? null : (picked ?? latestRunMonth(data.months));
  const allRows = [...loaded.values()].flatMap((table) => table.rows);

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

  const toggleIn = (set: ReadonlySet<string>, key: string): ReadonlySet<string> => {
    const next = new Set(set);
    if (next.has(key)) next.delete(key);
    else next.add(key);
    return next;
  };

  const options = useMemo(() => {
    if (data === undefined) return { sheets: [], methodologies: [], substances: [] };
    const sheets = new Map<string, string>();
    for (const table of data.tables) {
      if (table.tableDefId !== 0) sheets.set(String(table.sheetDefId), localized(table.sheetNameL10n) || table.sheetCode);
    }
    const methodologies = data.methodologies.map((m) => ({ value: m.code, label: localized(m.nameL10n) || m.code }));
    if (data.hasFormulas) methodologies.push({ value: FormulaMethodology, label: t('calcResults.templateFormula') });
    return {
      sheets: [...sheets].map(([value, label]) => ({ value, label })),
      methodologies,
      substances: data.substances.map((s) => ({ value: s.code, label: `${s.code} · ${localized(s.nameL10n) || s.code}` })),
    };
  }, [data]);

  // Шторка — лише для виходу із завантаженої сторінки (адреса з `?panel=` на незавантажений рядок шторки не відкриває).
  const panelCell = ((): { row: ResultRow; month: number } | null => {
    if (panel === null || !panel.startsWith(CellPanel)) return null;
    const rest = panel.slice(CellPanel.length);
    const colon = rest.indexOf(':');
    const month = Number(rest.slice(0, colon));
    const id = rest.slice(colon + 1);
    const row = allRows.find((r) => r.id === id);
    return row === undefined || !Number.isInteger(month) || month < 1 || month > 12 ? null : { row, month };
  })();

  const counts = data === undefined ? null : runCounts(data.months);

  return (
    <Stack gap="sm">
      <PageHeader
        back={{ label: documentKey, href: backHref }}
        title={t('calcResults.title')}
        description={t('calcResults.subtitle', { document: documentKey, year })}
        primary={{ label: t('calcResults.export'), onClick: () => setDialog('export-results'), disabled: data === undefined }}
        more={[
          {
            label: t('calcResults.collapseAll'),
            onClick: () => {
              setOpenState(new Set());
              setCollapsed(new Set(tables.map(sheetKey)));
            },
          },
          {
            label: t('calcResults.expandAll'),
            onClick: () => {
              setCollapsed(new Set());
              setOpenState(new Set(tables.map((table) => table.key)));
            },
          },
        ]}
      />

      {yearLayout && pickedMonth !== null && (
        <Banner
          title={t('calcResults.yearNoteTitle')}
          text={t('calcResults.yearNote', { month: `${labels.month(pickedMonth)} ${String(year)}` })}
          testId="cr-year-note"
        />
      )}

      <AsyncBoundary<CalculationYearSummary>
        isPending={summary.isPending}
        error={summary.error}
        data={data}
        isEmpty={(view) => view.totalRows === 0}
        emptyTitle={t('calcResults.emptyTitle')}
        emptyHint={t('calcResults.emptyHint', { year })}
        emptyAction={
          <Button component={Link} to={`/documents/${String(documentId)}`} variant="default">
            {t('calcResults.openDocument')}
          </Button>
        }
        skeleton="table"
        onRetry={() => void summary.refetch()}
      >
        {(view) => (
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
              {view.freshnessHidden && <span>{t('calcResults.legend.freshnessHidden')}</span>}
            </div>

            {view.matchedRows === 0 ? (
              <Stack align="flex-start" gap="xs" data-testid="cr-no-match">
                <Text fw={600}>{t('calcResults.noMatchTitle')}</Text>
                <Text size="sm" c="dimmed">{t('calcResults.noMatchHint')}</Text>
                <Button size="xs" variant="default" onClick={clearFilters}>{t('calcResults.clearFilters')}</Button>
              </Stack>
            ) : (
              <CalcResultsGrid
                months={view.months}
                year={view.year}
                items={items}
                collapsed={collapsed}
                open={open}
                loading={loading}
                labels={labels}
                pickedMonth={pickedMonth}
                onToggle={(key) => setCollapsed((current) => toggleIn(current, key))}
                onToggleTable={(key) => setOpenState(toggleIn(open, key))}
                onMore={onMore}
                onMonth={onMonth}
                onValue={(row, i) => setPanel(`${CellPanel}${String(i + 1)}:${row.id}`)}
              />
            )}

            <div className="ecr-cr-foot" data-testid="cr-foot">
              <span>{t('calcResults.footShown', { shown: view.matchedRows, total: view.totalRows })}</span>
              <span>{t('calcResults.footProblems', { count: view.problemRows })}</span>
              <span className="ecr-cr-sp" />
              {counts !== null && (
                <span>{t('calcResults.footRuns', { current: counts.current, outOfDate: counts.outOfDate, none: counts.none })}</span>
              )}
            </div>
          </Stack>
        )}
      </AsyncBoundary>

      {data !== undefined && panelCell !== null && (
        <ValueDrawer
          documentId={documentId}
          months={data.months}
          year={year}
          labels={labels}
          row={panelCell.row}
          month={panelCell.month}
          onClose={() => setPanel(null)}
        />
      )}

      <CalcResultsExport
        opened={dialog === 'export-results'}
        onClose={() => setDialog(null)}
        documentId={documentId}
        documentKey={documentKey}
        year={year}
        filters={filters}
        filtered={filtered}
      />
    </Stack>
  );
}

/** Шторка значення: звідки число і перехід до документа на його аркуші/таблиці/місяці. */
function ValueDrawer({
  documentId,
  months,
  year,
  labels,
  row,
  month,
  onClose,
}: {
  readonly documentId: number;
  readonly months: readonly CalculationYearMonth[];
  readonly year: number;
  readonly labels: ResultLabels;
  readonly row: ResultRow;
  readonly month: number;
  readonly onClose: () => void;
}): JSX.Element {
  const info = months[month - 1];
  const value = valueText(row.values[month - 1]);
  const unit = labels.unit(row);
  const methodology = row.methodology;
  const total = totalText(row);
  const monthText = `${labels.month(month)} ${String(year)}`;

  return (
    <DetailDrawer
      panelId={`${CellPanel}${String(month)}:${row.id}`}
      title={`${labels.outputText(row)} · ${monthText}`}
      subtitle={`${labels.equipment(row)}${row.equipment.idCode ? ` · ${row.equipment.idCode}` : ''}`}
      closeLabel={t('calcResults.close')}
      onClose={onClose}
      footer={
        <Button component={Link} to={documentHref(documentId, info?.periodKey ?? year * 100 + month, row)} data-testid="cr-open-source">
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
              value: methodology === null ? t('calcResults.templateFormula') : localized(methodology.nameL10n) || methodology.code,
              hint: methodology === null ? undefined : t('calcResults.drawer.version', { version: methodology.version }),
            },
            { label: t('calcResults.drawer.output'), value: row.outputCode, mono: true },
            {
              label: t('calcResults.drawer.calculated'),
              value: info?.calculatedAt === null || info?.calculatedAt === undefined ? '—' : formatDateTime(info.calculatedAt),
            },
            {
              label: t('calcResults.drawer.yearTotal'),
              value:
                row.unit?.additive === true && total !== null
                  ? t('calcResults.drawer.yearTotalValue', { value: `${total} ${unit}`.trim(), n: row.monthsIncluded })
                  : t('calcResults.drawer.notAdditive'),
            },
          ]}
        />
      </Stack>
    </DetailDrawer>
  );
}

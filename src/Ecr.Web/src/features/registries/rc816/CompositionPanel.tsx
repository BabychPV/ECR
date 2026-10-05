import { useEffect, useMemo, useRef, useState, type JSX } from 'react';
import {
  Badge,
  Button,
  Checkbox,
  Group,
  NativeSelect,
  Stack,
  Table,
  Text,
  TextInput,
  Title,
  VisuallyHidden,
} from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { keepPreviousData, useInfiniteQuery, useQuery, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import type { RegistryDefDto, RegistryDefinitionDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { Banner } from '@/shared/ui/Banner';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showDone } from '@/shared/ui/notify';
import { DisabledReason } from '@/features/common/DisabledReason';
import { getRegistryRows, saveBatch, type RegistryBatchResult } from '../rows/api';
import type { CompositionNode } from './compositionTree';
import {
  batchItems,
  dirtyCount,
  fromServer,
  isDirty,
  newRow,
  problemsByRow,
  setCode,
  setValue,
  toggleDelete,
  type PendingRow,
  type RowProblem,
} from './pendingRows';
import { DateOnlyInput } from './DateOnlyInput';
import { serverMessage } from './serverMessage';
import { SumIndicator } from './SumIndicator';

/** Ключі кешу редактора master-detail — під доменом `registries`, щоб загальна інвалідація їх зачіпала. */
export const compositionKeys = {
  rows: (code: string, parentId: number | null, asOf: string, q: string) =>
    ['registries', 'rc816', 'rows', code, parentId, asOf, q] as const,
  options: (code: string, asOf: string) => ['registries', 'rc816', 'options', code, asOf] as const,
  tree: (code: string) => ['registries', 'rc816', 'tree', code] as const,
};

/** Сторінка рядків панелі: частини одного батька вміщаються в одну. */
const PanelPage = 500;

/** Поле довідника, як його показує панель. */
type Field = RegistryDefinitionDto['fields'][number];

/** Варіанти вибору для поля `Lookup`: записи довідника-цілі на дату. */
function useLookupOptions(targetCode: string | null, asOf: string): { value: string; label: string }[] | null {
  const query = useQuery({
    queryKey: compositionKeys.options(targetCode ?? '', asOf),
    queryFn: () => getRegistryRows(targetCode ?? '', { asOf, limit: PanelPage }),
    enabled: targetCode !== null,
    staleTime: 60_000,
  });

  return useMemo(
    () =>
      query.data === undefined
        ? null
        : query.data.items.map((row) => ({ value: String(row.id), label: row.display === row.code ? row.code : `${row.display} (${row.code})` })),
    [query.data],
  );
}

/** Редактор однієї комірки за типом поля. */
function CellEditor({
  field,
  row,
  targetCode,
  asOf,
  readOnly,
  onChange,
}: {
  readonly field: Field;
  readonly row: PendingRow;
  readonly targetCode: string | null;
  readonly asOf: string;
  readonly readOnly: boolean;
  readonly onChange: (value: string) => void;
}): JSX.Element {
  const label = localized(field.nameL10n) || field.code;
  const value = row.values[field.code] ?? '';
  const options = useLookupOptions(field.dataType === 'Lookup' ? targetCode : null, asOf);
  const disabled = readOnly || row.deleted;

  if (field.dataType === 'Bool') {
    return (
      <Checkbox
        aria-label={label}
        disabled={disabled}
        checked={value === 'true'}
        onChange={(event) => onChange(event.currentTarget.checked ? 'true' : 'false')}
      />
    );
  }

  if (field.dataType === 'Lookup') {
    // ⚠ Поточне значення лишається серед варіантів, навіть якщо ціль уже не чинна на дату: інакше
    // select мовчки показав би «—», а збереження стерло б посилання, якого людина не чіпала.
    const known = options ?? [];
    const current =
      value !== '' && !known.some((option) => option.value === value)
        ? [{ value, label: row.displays[field.code] ?? value }]
        : [];
    return (
      <NativeSelect
        size="xs"
        aria-label={label}
        disabled={disabled || options === null}
        value={value}
        data={[{ value: '', label: '—' }, ...current, ...known]}
        onChange={(event) => onChange(event.currentTarget.value)}
      />
    );
  }

  if (field.dataType === 'Unit' || !['String', 'Int', 'Decimal', 'Date'].includes(field.dataType)) {
    // Одиниця й обчислювані види правляться в картці запису, не в сітці частин.
    return <Text size="sm">{row.displays[field.code] ?? (value || '—')}</Text>;
  }

  if (field.dataType === 'Date') {
    return <DateOnlyInput ariaLabel={label} value={value} disabled={disabled} clearable onChange={onChange} />;
  }

  return (
    <TextInput
      size="xs"
      aria-label={label}
      inputMode={field.dataType === 'String' ? 'text' : 'decimal'}
      disabled={disabled}
      value={value}
      onChange={(event) => onChange(event.currentTarget.value)}
    />
  );
}

function ProblemText({ problems }: { readonly problems: readonly RowProblem[] }): JSX.Element {
  return (
    <Stack gap="xs">
      {problems.map((problem, index) => (
        <Text key={`${problem.messageKey}:${index}`} size="xs" c="statusError">
          {problem.field !== null ? `${problem.field}: ` : ''}
          {serverMessage(problem.messageKey, problem.params)}
        </Text>
      ))}
    </Stack>
  );
}

/**
 * Одна панель редактора master-detail (`ФВ-8.16`, FEATURE-REGISTRY-TABLES §8.4): рядки довідника
 * (для частини — лише частини обраного батька), правка в таблиці, додавання й видалення, перевірка
 * і збереження ОДНИМ пакетом.
 *
 * ⛔ Поле композиції в панелі частин не показується й не вводиться: його значення — батько з
 * панелі вище, і саме так «кілька пов'язаних довідників редагуються як одна таблиця без введення
 * ідентифікаторів руками».
 */
export function CompositionPanel({
  node,
  parentId,
  parentLabel,
  asOf,
  readOnly,
  registries,
  selectedId,
  onSelect,
  selectionLocked,
  onDirty,
}: {
  readonly node: CompositionNode;
  /** Батько композиції; `null` — верхня панель. */
  readonly parentId: number | null;
  /** Підпис батька для заголовка панелі частин. */
  readonly parentLabel: string | null;
  readonly asOf: string;
  readonly readOnly: boolean;
  readonly registries: readonly RegistryDefDto[];
  /** Обраний рядок (його частини показує панель нижче); `undefined` — панель без частин. */
  readonly selectedId?: number | null | undefined;
  readonly onSelect?: ((id: number, label: string) => void) | undefined;
  /** Нижча панель має незбережені зміни — перемкнути батька зараз означало б їх загубити. */
  readonly selectionLocked?: boolean | undefined;
  readonly onDirty: (code: string, count: number) => void;
}): JSX.Element {
  const { definition, link } = node;
  const code = definition.code;
  const isPart = parentId !== null && link !== null;
  const queryClient = useQueryClient();

  const [search, setSearch] = useState('');
  // ⚠ Пошук — після паузи, як у сітці даних (rc812): кожна літера інакше давала окремий запит і
  // новий ключ, а таблиця на кожну літеру зникала до відповіді (L9-19).
  const [debouncedSearch] = useDebouncedValue(search, 300);
  const [edits, setEdits] = useState<PendingRow[] | null>(null);
  const [result, setResult] = useState<RegistryBatchResult | null>(null);
  const [failure, setFailure] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const counter = useRef(0);

  const rowsQuery = useInfiniteQuery({
    queryKey: compositionKeys.rows(code, parentId, asOf, isPart ? '' : debouncedSearch),
    queryFn: ({ pageParam }) =>
      getRegistryRows(
        code,
        isPart
          ? { asOf, parentEntryId: parentId, cursor: pageParam, limit: PanelPage }
          : { asOf, q: debouncedSearch, cursor: pageParam, limit: 100 },
      ),
    initialPageParam: null as string | null,
    getNextPageParam: (page) => page.nextCursor,
    // Попередні рядки лишаються на екрані, доки не прийшла відповідь на новий пошук.
    placeholderData: keepPreviousData,
    refetchOnWindowFocus: false,
  });

  const serverRows = useMemo(
    () => (rowsQuery.data?.pages ?? []).flatMap((page) => page.items).map(fromServer),
    [rowsQuery.data],
  );
  // ⚠ Правки — знімок рядків на момент першої правки; сторінки, довантажені «Показати ще» ПІСЛЯ
  // неї, доливаються до знімка (перед новими рядками), інакше кнопка «нічого не робила» (L9-09).
  const rows = useMemo(() => {
    if (edits === null) return serverRows;
    const known = new Set(edits.map((row) => row.key));
    const loaded = serverRows.filter((row) => !known.has(row.key));
    if (loaded.length === 0) return edits;
    return [...edits.filter((row) => row.id !== null), ...loaded, ...edits.filter((row) => row.id === null)];
  }, [edits, serverRows]);
  const dirty = dirtyCount(rows);
  const problems = useMemo(() => problemsByRow(result), [result]);

  useEffect(() => {
    onDirty(code, dirty);
  }, [code, dirty, onDirty]);
  useEffect(() => () => onDirty(code, 0), [code, onDirty]);

  const lookupTargets = useMemo(() => {
    const byId = new Map(registries.map((registry) => [registry.id, registry.code]));
    return (field: Field) => (field.lookupRegistryDefId === null ? null : byId.get(field.lookupRegistryDefId) ?? null);
  }, [registries]);

  const columns = definition.fields.filter((field) => !(isPart && field.code === link.fieldCode));
  const numeric = useMemo(
    () => new Set(definition.fields.filter((field) => field.dataType === 'Int' || field.dataType === 'Decimal').map((field) => field.code)),
    [definition.fields],
  );
  const manualCode = definition.codeMode !== 'Auto';
  const external = definition.sourceKind === 'External';
  const locked = readOnly || external;
  // ⚠ Поки пакет у дорозі, правки заблоковані: після успіху `setEdits(null)` знімає ВСІ правки,
  // і набране під час запиту зникло б мовчки, а сітка показала б серверне значення.
  const inputLocked = locked || busy;

  function edit(change: (all: readonly PendingRow[]) => PendingRow[]): void {
    setEdits(change(rows));
    setResult(null);
  }

  async function submit(dryRun: boolean): Promise<void> {
    const items = batchItems(rows, numeric);
    if (items.length === 0) return;

    setBusy(true);
    setFailure(null);
    try {
      const report = await saveBatch(code, items, dryRun);
      setResult(report);
      if (report.applied) {
        setEdits(null);
        showDone(t('registries.rc816.saved', { count: items.length }));
        // ⚠ Увесь домен, а не лише свої ключі: сітка даних того самого довідника (rc812), перелік
        // записів і варіанти `Lookup` інакше лишались зі старим `version` — правка там давала б
        // `entryChanged` на щойно збережений рядок. Сторінка впливу (RT-25) — окремий ключ поза
        // доменом (`RegistryImpactPage` `impactKey`), її перелік зачеплених теж застарів (L9-21).
        await Promise.all([
          queryClient.invalidateQueries({ queryKey: queryKeys.registries.all() }),
          queryClient.invalidateQueries({ queryKey: ['registry-impact', code] }),
        ]);
      }
    } catch (error) {
      // ⚠ `422 ECR-REG-4221` — порушене правило рівня `Error` після всього пакета: відмова по суті,
      // не аварія; правки лишаються на місці, щоб людина виправила Σ і зберегла ще раз.
      setFailure(error);
    } finally {
      setBusy(false);
    }
  }

  const heading = isPart
    ? t('registries.rc816.partsOf', { registry: localized(definition.nameL10n) || code, parent: parentLabel ?? '' })
    : localized(definition.nameL10n) || code;
  const violations = result?.rules ?? [];

  return (
    <Stack
      gap="xs"
      component="section"
      aria-label={heading}
      tabIndex={-1}
      data-rc816-panel={code}
    >
      <Group justify="space-between" align="center" wrap="wrap">
        <Group gap="xs">
          <Title order={2} size="h5">
            {heading}
          </Title>
          <Text size="xs" c="dimmed">
            {code}
          </Text>
          {isPart && (
            <Badge variant="light">
              {link.onParentDelete === 'Cascade'
                ? t('registries.rc816.policyCascade')
                : t('registries.rc816.policyRestrict')}
            </Badge>
          )}
        </Group>
        <Text size="xs" c="dimmed">
          {t('registries.rc816.rowCount', { count: rowsQuery.data?.pages[0]?.totalCount ?? 0 })}
          {dirty > 0 ? ` · ${t('registries.rc816.unsaved', { count: dirty })}` : ''}
        </Text>
      </Group>

      {external && <Banner tone="info" text={t('registries.rc816.readOnlyExternal')} />}

      {!isPart && (
        <TextInput
          size="xs"
          aria-label={t('registries.rc816.search')}
          placeholder={t('registries.rc816.search')}
          value={search}
          disabled={dirty > 0}
          onChange={(event) => setSearch(event.currentTarget.value)}
        />
      )}

      {rowsQuery.error !== null && (
        <ErrorAlert error={rowsQuery.error} onRetry={() => void rowsQuery.refetch()} />
      )}

      {rowsQuery.error === null && !rowsQuery.isPending && rows.length === 0 && (
        <Text size="sm" c="dimmed">
          {isPart ? t('registries.rc816.noParts') : t('registries.rc816.noRows')}
        </Text>
      )}

      {rows.length > 0 && (
        <Table.ScrollContainer minWidth={320}>
          <Table striped highlightOnHover aria-label={heading} data-rc816-table={code}>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('registries.code')}</Table.Th>
                {columns.map((field) => (
                  <Table.Th key={field.code}>{localized(field.nameL10n) || field.code}</Table.Th>
                ))}
                <Table.Th>
                  <VisuallyHidden>{t('registries.rc816.actions')}</VisuallyHidden>
                </Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {rows.map((row) => {
                const rowProblems = problems.get(row.key) ?? [];
                const selected = row.id !== null && row.id === selectedId;
                return (
                  <Table.Tr
                    key={row.key}
                    aria-selected={onSelect !== undefined ? selected : undefined}
                    data-rc816-row={row.key}
                    data-state={row.deleted ? 'deleted' : isDirty(row) ? 'dirty' : undefined}
                  >
                    <Table.Td>
                      {row.id === null && manualCode ? (
                        <TextInput
                          size="xs"
                          aria-label={t('registries.code')}
                          value={row.code}
                          disabled={inputLocked}
                          onChange={(event) => edit((all) => setCode(all, row.key, event.currentTarget.value))}
                        />
                      ) : onSelect !== undefined && row.id !== null ? (
                        // ⚠ Причина вголос: поки нижчий рівень не збережено, інший
                        // рядок не обрати, і без пояснення кнопка просто «мертва».
                        <DisabledReason
                          reason={selectionLocked === true && !selected ? t('registries.rc816.selectionLocked') : null}
                        >
                          <Button
                            size="compact-xs"
                            variant={selected ? 'light' : 'subtle'}
                            aria-pressed={selected}
                            onClick={() => onSelect(row.id as number, row.display || row.code)}
                          >
                            {row.code}
                          </Button>
                        </DisabledReason>
                      ) : (
                        <Text size="sm">{row.code || t('registries.rc816.autoCode')}</Text>
                      )}
                      {rowProblems.length > 0 && <ProblemText problems={rowProblems} />}
                    </Table.Td>
                    {columns.map((field) => (
                      <Table.Td key={field.code}>
                        <CellEditor
                          field={field}
                          row={row}
                          targetCode={lookupTargets(field)}
                          asOf={asOf}
                          readOnly={inputLocked}
                          onChange={(value) => edit((all) => setValue(all, row.key, field.code, value))}
                        />
                      </Table.Td>
                    ))}
                    <Table.Td>
                      {!locked && (
                        <Button
                          size="compact-xs"
                          variant="subtle"
                          color={row.deleted ? 'gray' : 'statusError'}
                          disabled={busy}
                          onClick={() => edit((all) => toggleDelete(all, row.key))}
                        >
                          {row.deleted ? t('registries.rc816.undoDelete') : t('registries.rc816.delete')}
                        </Button>
                      )}
                    </Table.Td>
                  </Table.Tr>
                );
              })}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}

      {rowsQuery.hasNextPage && (
        <Button size="xs" variant="default" disabled={rowsQuery.isFetchingNextPage} onClick={() => void rowsQuery.fetchNextPage()}>
          {t('registries.rc816.more')}
        </Button>
      )}

      {node.sums.map((rule) => (
        <SumIndicator key={rule.code} rule={rule} rows={rows} />
      ))}

      {violations.length > 0 && (
        <Stack gap="xs" data-rc816-violations={code}>
          {violations.map((violation, index) => (
            <Text
              key={`${violation.rule}:${violation.entryId}:${index}`}
              size="xs"
              c={violation.severity === 'Error' ? 'statusError' : 'dimmed'}
            >
              {violation.entryCode} · {serverMessage(violation.messageKey, violation.params)}
            </Text>
          ))}
        </Stack>
      )}

      <ErrorAlert error={failure} />

      {!locked && (
        <Group gap="xs">
          <Button
            size="xs"
            variant="default"
            disabled={busy}
            onClick={() => {
              counter.current += 1;
              edit((all) => [
                ...all,
                newRow(
                  `n${counter.current}`,
                  isPart ? { field: link.fieldCode, parentId } : null,
                ),
              ]);
            }}
          >
            {isPart ? t('registries.rc816.addPart') : t('registries.rc816.addRow')}
          </Button>
          <Button size="xs" variant="default" disabled={dirty === 0 || busy} onClick={() => void submit(true)}>
            {t('registries.rc816.check')}
          </Button>
          <Button size="xs" disabled={dirty === 0 || busy} loading={busy} onClick={() => void submit(false)}>
            {t('registries.rc816.save', { count: dirty })}
          </Button>
          {dirty > 0 && (
            <Button
              size="xs"
              variant="subtle"
              disabled={busy}
              onClick={() => {
                setEdits(null);
                setResult(null);
                setFailure(null);
              }}
            >
              {t('registries.rc816.discard')}
            </Button>
          )}
        </Group>
      )}
    </Stack>
  );
}

import { Suspense, lazy, useCallback, useEffect, useMemo, useRef, useState, type JSX } from 'react';
import { Button, Group, Stack, Text, TextInput, VisuallyHidden } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useParams } from 'react-router-dom';
import { queryKeys } from '@/api/queryKeys';
import { isExternalRegistry } from '@/features/registries/RegistryEntryEditor';
import { RegistryExportButton } from '@/features/registries/export/RegistryExportButton';
import { saveBatch, type RegistryBatchResult, type RegistryRow } from '@/features/registries/rows/api';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { Banner } from '@/shared/ui/Banner';
import { useDetailPanel } from '@/shared/ui/DetailDrawer';
import { showApiError } from '@/shared/ui/notify';
import { PageHeader } from '@/shared/ui/PageHeader';
import { registerUnsavedSource } from '@/shared/ui/unsavedSources';
import { useUrlState } from '@/shared/ui/useUrlState';
import {
  dateOfIso,
  isoOfDate,
  resolveLookup,
  todayIso,
  useRegistryDefinition,
  useRegistryList,
  useRegistryRows,
  useUnits,
} from './data';
import { EntryDrawer, entryPanelId } from './EntryDrawer';
import { RegistryDataGrid, type GridRow } from './RegistryDataGrid';
import {
  cellValue,
  draftOf,
  duplicateKeys,
  existingRowKey,
  isDirty,
  newDraft,
  parseBlock,
  pastedBool,
  planBlockPaste,
  problemsByRow,
  setCell,
  toBatch,
  validateCell,
  type CellProblem,
  type RegistryField,
  type RowDraft,
} from './rowModel';

const DateInput = lazy(async () => ({ default: (await import('@/shared/dates/DateInputWithStyles')).DateInput }));

/** Пауза перед живою перевіркою `dryRun` (§8.4). */
export const LiveCheckDelayMs = 600;

/**
 * Дані довідника — табличний редактор (`ФВ-8.12`, FEATURE-REGISTRY-TABLES §8.4):
 * `/admin/registries/:code/entries`.
 *
 * ⚠ Правки живуть у чернетках до `Ctrl+S`; одне збереження — один пакет
 * (`POST …/entries/batch`), і пакет пише все або нічого. Поки правки не збережені, їх щоразу
 * після паузи перевіряє той самий пакет із `dryRun=true`: помилки й дублі ключа видно до
 * збереження, у комірці, до якої вони належать.
 */
export function RegistryDataPage(): JSX.Element {
  const { code = '' } = useParams();
  const queryClient = useQueryClient();
  const session = useSession();
  const list = useRegistryList();
  const definition = useRegistryDefinition(code);
  const registry = list.data?.find((r) => r.code === code);
  const temporal = definition.data?.isTemporal ?? registry?.isTemporal ?? false;

  const [asOfParam, setAsOf] = useUrlState('asOf');
  const asOf = temporal ? (asOfParam ?? todayIso()) : null;
  const [search, setSearch] = useUrlState('q');
  const [debouncedSearch] = useDebouncedValue(search ?? '', 300);
  const rows = useRegistryRows(code, asOf, debouncedSearch, definition.isSuccess);

  const [drafts, setDrafts] = useState<ReadonlyMap<string, RowDraft>>(new Map());
  const [newKeys, setNewKeys] = useState<readonly string[]>([]);
  const [problems, setProblems] = useState<ReadonlyMap<string, readonly CellProblem[]>>(new Map());
  const [checkSummary, setCheckSummary] = useState<string>('');
  const [savedAt, setSavedAt] = useState<Date | null>(null);
  const [unmatched, setUnmatched] = useState(0);
  const seq = useRef(0);
  const [panel, setPanel] = useDetailPanel();

  const fields = useMemo<readonly RegistryField[]>(() => definition.data?.fields ?? [], [definition.data]);
  const keys = useMemo(() => definition.data?.keys ?? [], [definition.data]);
  const loaded = useMemo<readonly RegistryRow[]>(() => rows.data?.pages.flatMap((p) => p.items) ?? [], [rows.data]);
  const byKey = useMemo(() => new Map(loaded.map((row) => [existingRowKey(row.id), row])), [loaded]);
  const totalCount = rows.data?.pages[0]?.totalCount ?? loaded.length;
  const units = useUnits(fields.some((f) => f.dataType === 'Unit'));

  const readOnly = !can(session.data, 'Registry.EditData') || isExternalRegistry(registry);
  const manualCode = definition.data?.codeMode !== 'Auto';

  const gridRows = useMemo<GridRow[]>(
    () => [
      ...loaded.map((row) => {
        const rowKey = existingRowKey(row.id);
        return { rowKey, row, draft: drafts.get(rowKey) };
      }),
      ...newKeys.map((rowKey) => ({ rowKey, row: undefined, draft: drafts.get(rowKey) })),
    ],
    [loaded, newKeys, drafts],
  );

  const dirty = useMemo(() => [...drafts.values()].filter(isDirty), [drafts]);
  const locallyInvalid = dirty.some(
    (draft) => !draft.deleted && fields.some((f) => validateCell(f, cellValue(byKey.get(draft.rowKey), draft, f.code)) !== null && (draft.id === null || f.code in draft.values)),
  );

  const duplicates = useMemo(
    () =>
      duplicateKeys(
        gridRows
          .filter((g) => g.draft?.deleted !== true)
          .map((g) => ({
            rowKey: g.rowKey,
            values: Object.fromEntries(fields.map((f) => [f.code, cellValue(g.row, g.draft, f.code)])),
          })),
        keys,
        fields,
      ),
    [gridRows, keys, fields],
  );

  const lookupCodeOf = useCallback(
    (field: RegistryField): string | null =>
      list.data?.find((r) => r.id === field.lookupRegistryDefId)?.code ?? null,
    [list.data],
  );

  const update = (rowKey: string, change: (draft: RowDraft) => RowDraft): void => {
    setDrafts((current) => {
      const row = byKey.get(rowKey);
      const base = current.get(rowKey) ?? (row === undefined ? undefined : draftOf(row));
      if (base === undefined) return current;
      const next = new Map(current);
      next.set(rowKey, change(base));
      return next;
    });
    setSavedAt(null);
  };

  const onEdit = (rowKey: string, field: string, value: string | null, display?: string): void =>
    update(rowKey, (draft) => setCell(draft, byKey.get(rowKey), field, value, display));

  const addRow = (): string => {
    seq.current += 1;
    const draft = newDraft(seq.current);
    setDrafts((current) => new Map(current).set(draft.rowKey, draft));
    setNewKeys((current) => [...current, draft.rowKey]);
    return draft.rowKey;
  };

  const onToggleDelete = (rowKey: string): void => {
    if (rowKey.startsWith('n:')) {
      setNewKeys((current) => current.filter((k) => k !== rowKey));
      setDrafts((current) => {
        const next = new Map(current);
        next.delete(rowKey);
        return next;
      });
      return;
    }
    update(rowKey, (draft) => ({ ...draft, deleted: !draft.deleted }));
  };

  /** Вставка блоку: нові рядки за краєм, `Lookup` — за кодом або назвою цілі. */
  const onPaste = async (rowIndex: number, columnIndex: number, text: string): Promise<void> => {
    const cells = planBlockPaste(parseBlock(text), rowIndex, columnIndex, fields.map((f) => f.code));
    const keysByIndex = gridRows.map((g) => g.rowKey);
    let misses = 0;

    for (const cell of cells) {
      while (keysByIndex.length <= cell.rowIndex) keysByIndex.push(addRow());
      const rowKey = keysByIndex[cell.rowIndex];
      const field = fields.find((f) => f.code === cell.field);
      if (rowKey === undefined || field === undefined) continue;
      if (cell.text === '') {
        onEdit(rowKey, field.code, null);
        continue;
      }

      if (field.dataType === 'Lookup') {
        const target = lookupCodeOf(field);
        const hit = target === null ? null : await resolveLookup(target, cell.text, asOf);
        if (hit === null) misses += 1;
        else onEdit(rowKey, field.code, hit.id, hit.display);
      } else if (field.dataType === 'Unit') {
        const unit = units.data?.find((u) => u.code.toLowerCase() === cell.text.toLowerCase());
        if (unit === undefined) misses += 1;
        else onEdit(rowKey, field.code, String(unit.id), unit.code);
      } else if (field.dataType === 'Bool') {
        const value = pastedBool(cell.text);
        if (value === null) misses += 1;
        else onEdit(rowKey, field.code, value);
      } else {
        onEdit(rowKey, field.code, cell.text);
      }
    }
    setUnmatched(misses);
  };

  // Жива перевірка: пакет із dryRun після паузи. Відповідь, що запізнилася, не перетирає свіжішу.
  const checkRun = useRef(0);
  useEffect(() => {
    const items = toBatch(dirty);
    if (items.length === 0 || readOnly) {
      setProblems(new Map());
      setCheckSummary('');
      return undefined;
    }
    const run = ++checkRun.current;
    const timer = setTimeout(() => {
      saveBatch(code, items, true)
        .then((result) => {
          if (run !== checkRun.current) return;
          setProblems(problemsByRow(result));
          setCheckSummary(summaryOf(result));
        })
        .catch(() => {
          // Перевірка — підказка; відмову покаже збереження.
        });
    }, LiveCheckDelayMs);
    return () => clearTimeout(timer);
  }, [dirty, code, readOnly]);

  const save = useMutation({
    mutationFn: () => saveBatch(code, toBatch(dirty), false),
    onSuccess: (result) => {
      checkRun.current += 1;
      if (!result.applied) {
        setProblems(problemsByRow(result));
        setCheckSummary(summaryOf(result));
        return;
      }
      setDrafts(new Map());
      setNewKeys([]);
      setProblems(new Map());
      setCheckSummary('');
      setUnmatched(0);
      setSavedAt(new Date());
      void queryClient.invalidateQueries({ queryKey: queryKeys.registries.all() });
    },
    onError: showApiError,
  });

  const canSave = !readOnly && dirty.length > 0 && !locallyInvalid && duplicates.size === 0 && !save.isPending;
  const doSave = (): void => {
    if (canSave) save.mutate();
  };

  // Ctrl+S — зберегти (§8.8). Ref: обробник вікна бачить свіжий стан без перевішування.
  const saveRef = useRef(doSave);
  saveRef.current = doSave;
  useEffect(() => {
    const onKey = (event: KeyboardEvent): void => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
        event.preventDefault();
        saveRef.current();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  // Вихід із незбереженим — спитає `UnsavedGuard` (у `AppLayout`).
  const dirtyRef = useRef(dirty.length);
  dirtyRef.current = dirty.length;
  useEffect(
    () =>
      registerUnsavedSource('registry-data', {
        hasUnsaved: () => dirtyRef.current > 0,
        unsavedCount: () => dirtyRef.current,
      }),
    [],
  );

  const name = registry ? localized(registry.nameL10n) : code;
  const openRow = panel?.startsWith('entry-') ? loaded.find((r) => entryPanelId(r.id) === panel) : undefined;
  const status = savedAt !== null
    ? t('registries.data.saved', { time: savedAt.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) })
    : dirty.length > 0
      ? t('registries.data.unsaved', { count: dirty.length })
      : '';

  return (
    <Stack gap="md">
      <PageHeader
        title={name}
        badge={<Text size="sm" ff="monospace" c="dimmed">{code}</Text>}
        count={totalCount}
        back={{ label: t('registries.data.back'), href: `/admin/registries?code=${encodeURIComponent(code)}` }}
        secondary={[{ label: t('registries.constructor'), href: `/admin/registries/${encodeURIComponent(code)}/definition` }]}
        primary={
          readOnly
            ? undefined
            : {
                label: dirty.length > 0 ? t('registries.data.saveChanges', { count: dirty.length }) : t('common.save'),
                onClick: doSave,
                disabled: !canSave,
              }
        }
      />

      {readOnly && registry !== undefined && (
        <Banner
          tone="info"
          text={isExternalRegistry(registry) ? t('registries.data.readOnlyExternal') : t('registries.data.readOnly')}
        />
      )}
      {unmatched > 0 && (
        <Banner
          tone="warning"
          text={t('registries.data.unmatchedPaste', { count: unmatched })}
          dismiss={{ label: t('common.close'), onDismiss: () => setUnmatched(0) }}
        />
      )}

      <Group gap="sm" align="end" justify="space-between">
        <Group gap="sm" align="end">
          <TextInput
            size="xs"
            label={t('registries.search')}
            placeholder={t('registries.searchPlaceholder')}
            value={search ?? ''}
            onChange={(event) => setSearch(event.currentTarget.value)}
          />
          {temporal && (
            <Suspense fallback={null}>
              <DateInput
                size="xs"
                label={t('registries.data.asOf')}
                valueFormat="YYYY-MM-DD"
                value={dateOfIso(asOf)}
                onChange={(day) => setAsOf(isoOfDate(day))}
              />
            </Suspense>
          )}
          {!readOnly && (
            <Button size="xs" variant="default" onClick={() => addRow()}>
              {t('registries.newEntry')}
            </Button>
          )}
          {/* RT-16: експорт — записи, чинні на ту саму дату, що й сітка. */}
          <RegistryExportButton registryCode={code} asOf={asOf} />
        </Group>
        <Text size="sm" c="dimmed" aria-live="polite" data-testid="registry-data-status">
          {[status, checkSummary].filter((s) => s !== '').join(' · ')}
        </Text>
      </Group>

      <AsyncBoundary
        isPending={definition.isPending || rows.isPending}
        error={definition.error ?? rows.error}
        data={gridRows}
        isEmpty={(data) => data.length === 0}
        emptyTitle={debouncedSearch !== '' ? t('registries.searchNoMatches') : t('registries.data.emptyTitle')}
        emptyHint={debouncedSearch !== '' ? undefined : t('registries.data.emptyText')}
        emptyAction={
          !readOnly && debouncedSearch === '' ? (
            <Button size="xs" onClick={() => addRow()}>
              {t('registries.newEntry')}
            </Button>
          ) : undefined
        }
        skeleton="table"
        onRetry={() => void rows.refetch()}
      >
        {() => (
          <>
            <RegistryDataGrid
              caption={name}
              fields={fields}
              rows={gridRows}
              totalCount={totalCount + newKeys.length}
              readOnly={readOnly}
              manualCode={manualCode}
              problems={problems}
              duplicates={duplicates}
              lookupCodeOf={lookupCodeOf}
              asOf={asOf}
              onEdit={onEdit}
              onEditCode={(rowKey, value) => update(rowKey, (draft) => ({ ...draft, code: value }))}
              onToggleDelete={onToggleDelete}
              onOpen={(rowKey) => {
                const row = byKey.get(rowKey);
                if (row !== undefined) setPanel(entryPanelId(row.id));
              }}
              onAddRow={() => void addRow()}
              onPaste={(r, c, text) => void onPaste(r, c, text)}
            />
            {rows.hasNextPage && (
              <Group justify="center">
                <Button size="xs" variant="default" loading={rows.isFetchingNextPage} onClick={() => void rows.fetchNextPage()}>
                  {t('registries.data.loadMore', { shown: loaded.length, total: totalCount })}
                </Button>
              </Group>
            )}
            <VisuallyHidden>{t('registries.data.keyboardHint')}</VisuallyHidden>
          </>
        )}
      </AsyncBoundary>

      {openRow !== undefined && registry !== undefined && (
        <EntryDrawer registry={registry} row={openRow} fields={fields} asOf={asOf} readOnly={readOnly} />
      )}
    </Stack>
  );
}

/** Підсумок перевірки словами для `aria-live`: «2 errors, 1 warning». */
function summaryOf(result: RegistryBatchResult): string {
  const errors = result.rows.reduce((sum, row) => sum + row.errors.length, 0);
  const warnings = (result.rules ?? []).length;
  if (errors === 0 && warnings === 0) return t('registries.data.checkOk');
  return t('registries.data.checkSummary', { errors, warnings });
}

import { lazy, Suspense, useId, useMemo, useState, type JSX } from 'react';
import { Button, Skeleton, Stack, Text, VisuallyHidden } from '@mantine/core';
import { t } from '@/shared/i18n';
import { AsyncBoundary, EmptyState } from '@/shared/ui/AsyncBoundary';
import { useUrlState } from '@/shared/ui/useUrlState';
import type { Permit, PermitTable, PermitType } from '../api/types';
import type { PermitRow } from '../lib/columns';
import { PermitDefTable } from '../table/PermitDefTable';
import { localName, totalSums, type LoadedRow } from './model';
import type { PermitCapabilities } from './permitRules';
import { usePermitRows, useTableFields } from './usePermitData';
import './permit.css';

// Шторка рядка й діалог вставки — важка частина екрана, а на першому показі їх немає: окремі чанки, що вантажаться,
// лише коли їх відкрито (бюджет маршруту, D-132).
const RowDrawer = lazy(() => import('./RowDrawer').then((module) => ({ default: module.RowDrawer })));
const PasteDialog = lazy(() => import('./PasteDialog').then((module) => ({ default: module.PasteDialog })));

/** Параметр адреси відкритої шторки: `<код таблиці>:<id рядка>` або `<код таблиці>:__new`. */
export const PanelParam = 'panel';
export const NewRowId = '__new';

export interface TableTabProps {
  readonly permit: Permit;
  readonly table: PermitTable;
  readonly type: PermitType;
  readonly units: ReadonlyMap<number, string>;
  readonly caps: PermitCapabilities;
}

/**
 * Вкладка однієї таблиці дозволу (мокап `tableTab()`): рядок підказки, «Add row» · «Paste from Excel…» ·
 * «Import file…», широка таблиця `PermitDefTable` з підсумком, шторка рядка в `?panel=`. Закінчений дозвіл (або
 * користувач без права запису) бачить таблицю й рядок лише для читання.
 */
export function TableTab({ permit, table, type, units, caps }: TableTabProps): JSX.Element {
  const fields = useTableFields(type, table.code, units);
  const { query, rows } = usePermitRows(table.registryCode, permit.id, fields, fields.length > 0);
  const [panel, setPanel] = useUrlState(PanelParam);
  const [paste, setPaste] = useState<'paste' | 'file' | null>(null);
  // Лічильник перезбирає шторку «Save and add next»: та сама адреса `__new`, але форма має бути порожньою.
  const [drawerSerial, setDrawerSerial] = useState(0);
  const name = localName(table.nameL10n) || table.code;
  const columns = useMemo(() => fields.map((field) => field.column), [fields]);

  const [panelTable, panelRow] = (panel ?? '').split(':');
  const drawerHere = panelTable === table.code && panelRow !== undefined && panelRow !== '';
  const loadedRow: LoadedRow | null | undefined =
    !drawerHere || rows === undefined
      ? undefined
      : panelRow === NewRowId
        ? null
        : rows.find((candidate) => candidate.row.id === panelRow);

  const openRow = (id: string): void => setPanel(`${table.code}:${id}`);
  const readOnlyReason = caps.editRows ? undefined : t('permits.page.table.readOnlyReason');
  const tableRows: PermitRow[] = rows?.map((loaded) => loaded.row) ?? [];

  // Вимкнені кнопки пов'язані з причиною через aria-describedby (а не лише `title`, якого скрінрідер не читає).
  const reasonId = useId();
  const describedBy = readOnlyReason === undefined ? undefined : reasonId;

  const tools = (
    <div className="pp-tools">
      {readOnlyReason !== undefined && <VisuallyHidden id={reasonId}>{readOnlyReason}</VisuallyHidden>}
      <Button disabled={!caps.editRows} title={readOnlyReason} aria-describedby={describedBy} onClick={() => openRow(NewRowId)}>
        {t('permits.page.table.addRow')}
      </Button>
      <Button variant="default" disabled={!caps.editRows} title={readOnlyReason} aria-describedby={describedBy} onClick={() => setPaste('paste')}>
        {t('permits.page.table.paste')}
      </Button>
      <Button variant="default" disabled={!caps.editRows} title={readOnlyReason} aria-describedby={describedBy} onClick={() => setPaste('file')}>
        {t('permits.page.table.importFile')}
      </Button>
    </div>
  );

  return (
    <Stack gap="md">
      <Text size="sm" c="dimmed">
        {t('permits.page.table.note', { name, columns: columns.length, rows: table.rowCount })}
      </Text>
      {tools}
      <AsyncBoundary<LoadedRow[]>
        isPending={fields.length === 0 ? false : query.isPending}
        error={query.error}
        data={fields.length === 0 ? [] : rows}
        skeleton="table"
        onRetry={() => void query.refetch()}
      >
        {(loaded) =>
          loaded.length === 0 ? (
            <EmptyState
              title={t('permits.page.table.emptyTitle', { name })}
              headingOrder={2}
              hint={caps.editRows ? t('permits.page.table.emptyHint') : t('permits.page.table.emptyReadOnly')}
            />
          ) : (
            <PermitDefTable
              columns={columns}
              rows={tableRows}
              caption={name}
              totals={
                table.total === null
                  ? undefined
                  : {
                      label: t('permits.page.table.sumAll', { count: table.rowCount }),
                      sums: totalSums(table.totalColumnCode, table.total),
                    }
              }
              onRowClick={(row) => openRow(row.id)}
            />
          )
        }
      </AsyncBoundary>

      {drawerHere && loadedRow !== undefined && (
        <Suspense fallback={<Skeleton height={160} role="status" aria-label={t('common.loading')} />}>
          <RowDrawer
            key={`${panel ?? ''}-${String(drawerSerial)}`}
            permit={permit}
            registryCode={table.registryCode}
            tableName={name}
            fields={fields}
            rows={rows ?? []}
            loaded={loadedRow}
            readOnly={!caps.editRows}
            onAddNext={() => {
              setDrawerSerial((value) => value + 1);
              openRow(NewRowId);
            }}
            onClose={() => setPanel(null)}
          />
        </Suspense>
      )}
      {paste !== null && (
        <Suspense fallback={<Skeleton height={160} role="status" aria-label={t('common.loading')} />}>
          <PasteDialog
            permit={permit}
            registryCode={table.registryCode}
            tableName={name}
            fields={fields}
            fromFile={paste === 'file'}
            onClose={() => setPaste(null)}
          />
        </Suspense>
      )}
    </Stack>
  );
}

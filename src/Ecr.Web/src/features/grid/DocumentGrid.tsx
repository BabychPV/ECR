import { useCallback, useEffect, useMemo, useRef, useState, type JSX } from 'react';
import { Alert, Button, Group, List, Modal, Stack, Text } from '@mantine/core';
import { RevoGrid } from '@revolist/react-datagrid';
import type { ColumnRegular } from '@revolist/revogrid';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { parseClipboard, parseNumber, planPaste, toClipboard, type PasteRejection } from './clipboard';
import { cellKey, decide, guardOf } from './permissions';
import { UndoStack, type CellEdit } from './undo';
import { buildRequest, useCellPatch, type PendingEdit } from './useCellPatch';
import { t } from '@/shared/i18n';

/** Властивості grid. */
export interface DocumentGridProps {
  /** Документ. */
  documentId: number;
  /** Екземпляр таблиці. */
  tableInstanceId: number;
  /** Ключ періоду. */
  periodKey: number;
  /** Чи доступне редагування на рівні всієї таблиці. */
  readOnly: boolean;
}

/** Рядок у моделі grid: значення за кодами колонок плюс службовий ключ. */
type GridRow = Record<string, unknown> & { __rowKey: string };

/**
 * Grid-редактор документа.
 *
 * Обов'язкові можливості (`B21` §12, критерії FQ-1):
 *  1. навігація клавіатурою як в Excel: Tab / Enter / стрілки / Home-End / Ctrl+стрілки;
 *  2. виділення діапазону мишею, Shift+стрілки, Ctrl+A;
 *  3. **вставка з Excel**: Ctrl+V багатоклітинного буфера з правильним розбором
 *     роздільників і десяткової коми;
 *  4. копіювання у форматі, який Excel приймає;
 *  5. fill-handle (протягування);
 *  6. **Undo/Redo ≥50 кроків** у межах таблиці;
 *  7. віртуалізація: 5000 рядків без просідання;
 *  8. права по комірках із візуальним відрізненням.
 *
 * ⚠ Пункти 1, 2, 5 і 7 забезпечує RevoGrid; пункти 3, 4, 6 і 8 — наш код, і
 * саме вони винесені в чисті модулі `clipboard.ts`, `undo.ts`,
 * `permissions.ts`. Це не стиль: undo потребує **власної моделі команд**, і
 * саме на ній ламається більшість grid-бібліотек.
 *
 * ⛔ Поведінка вставки в read-only комірки визначена **до** реалізації: якщо у
 * вставленому діапазоні є заборонені комірки — **відхиляється весь батч**, і
 * користувач бачить перелік заборонених. Часткове застосування заборонене на
 * рівні API (B04 §2.3), і UI не має його імітувати.
 */
export function DocumentGrid(props: DocumentGridProps): JSX.Element {
  const { documentId, tableInstanceId, periodKey, readOnly } = props;

  const slice = useQuery({
    queryKey: ['table-slice', tableInstanceId, periodKey],
    queryFn: () =>
      apiFetch<TableSliceDto>(
        `/api/v1/tables/${tableInstanceId}/slice?periodKey=${periodKey}`,
      ),
  });

  const { patch, isPending, conflicts } = useCellPatch(documentId);
  const history = useRef(new UndoStack(`${tableInstanceId}:${periodKey}`));
  const [rejected, setRejected] = useState<PasteRejection[]>([]);
  const [pending, setPending] = useState<Map<string, PendingEdit>>(new Map());

  // ⛔ Історія скидається при переході на іншу таблицю: крок, застосований до
  // чужого зрізу, писав би значення в комірки з тими самими кодами, але
  // іншого документа.
  useEffect(() => {
    history.current.rescope(`${tableInstanceId}:${periodKey}`);
    setPending(new Map());
  }, [tableInstanceId, periodKey]);

  const data = slice.data;

  const columns = useMemo(() => (data === undefined ? [] : gridColumns(data, readOnly)), [data, readOnly]);
  const rows = useMemo(() => (data === undefined ? [] : gridRows(data)), [data]);

  const save = useCallback(
    async (edits: PendingEdit[]) => {
      if (edits.length === 0) return;

      await patch(buildRequest(tableInstanceId, periodKey, edits));
      setPending(new Map());
    },
    [patch, periodKey, tableInstanceId],
  );

  /** Ctrl+V: розкладає буфер по сітці і відхиляє батч цілком, якщо є заборонені. */
  const onPaste = useCallback(
    (event: React.ClipboardEvent<HTMLDivElement>) => {
      if (data === undefined || readOnly) return;

      const text = event.clipboardData.getData('text/plain');
      if (text.length === 0) return;

      event.preventDefault();

      const plan = planPaste(
        parseClipboard(text),
        data.rows.map((row) => row.rowKey),
        data.columns.map((column) => column.code),
        { rowIndex: 0, columnIndex: 0 },
        guardOf(data),
      );

      if (plan.rejected.length > 0) {
        setRejected(plan.rejected);
        return;
      }

      const versions = new Map(data.rows.map((row) => [row.rowKey, row.rowVersion]));
      const types = new Map(data.columns.map((column) => [column.code, column.dataType]));

      const edits: PendingEdit[] = plan.targets.map((target) => ({
        rowKey: target.rowKey,
        columnCode: target.columnCode,
        value: coerce(target.value, types.get(target.columnCode)),
        isEmpty: false,
        baseVersion: versions.get(target.rowKey) ?? null,
      }));

      // ⚠ Уся вставка — ОДИН крок історії: інакше одне Ctrl+V з'їдало б усю
      // глибину, а Ctrl+Z відкочував би її по комірці.
      history.current.push({
        label: t('grid.paste', { count: edits.length }),
        edits: edits.map<CellEdit>((edit) => ({
          rowKey: edit.rowKey,
          columnCode: edit.columnCode,
          before: valueOf(data, edit.rowKey, edit.columnCode),
          after: edit.value,
        })),
      });

      void save(edits);
    },
    [data, readOnly, save],
  );

  /** Ctrl+C: віддає виділене у форматі, який приймає Excel. */
  const onCopy = useCallback(
    (event: React.ClipboardEvent<HTMLDivElement>) => {
      if (data === undefined) return;

      event.preventDefault();
      event.clipboardData.setData(
        'text/plain',
        toClipboard(
          data.rows.map((row) => data.columns.map((column) => String(row.cells[column.code] ?? ''))),
        ),
      );
    },
    [data],
  );

  const onKeyDown = useCallback(
    (event: React.KeyboardEvent<HTMLDivElement>) => {
      const modifier = event.ctrlKey || event.metaKey;
      if (!modifier) return;

      if (event.key === 's') {
        event.preventDefault();
        void save([...pending.values()]);
        return;
      }

      if (event.key === 'z' && !event.shiftKey) {
        event.preventDefault();
        applyHistory(history.current.undo());
        return;
      }

      if (event.key === 'y' || (event.key === 'z' && event.shiftKey)) {
        event.preventDefault();
        applyHistory(history.current.redo());
      }
    },
    [pending, save],
  );

  const applyHistory = useCallback(
    (edits: CellEdit[] | null) => {
      if (edits === null || data === undefined) return;

      const versions = new Map(data.rows.map((row) => [row.rowKey, row.rowVersion]));

      void save(
        edits.map((edit) => ({
          rowKey: edit.rowKey,
          columnCode: edit.columnCode,
          value: edit.after,
          isEmpty: false,
          baseVersion: versions.get(edit.rowKey) ?? null,
        })),
      );
    },
    [data, save],
  );

  if (slice.isPending) return <Text>{t('grid.loading')}</Text>;

  if (slice.isError || data === undefined) {
    return <Alert color="red">{t('grid.loadFailed')}</Alert>;
  }

  return (
    <Stack gap="xs" onPaste={onPaste} onCopy={onCopy} onKeyDown={onKeyDown}>
      <Group gap="xs">
        <Button size="xs" variant="default" disabled={!history.current.canUndo} onClick={() => applyHistory(history.current.undo())}>
          {t('grid.undo')}
        </Button>
        <Button size="xs" variant="default" disabled={!history.current.canRedo} onClick={() => applyHistory(history.current.redo())}>
          {t('grid.redo')}
        </Button>
        <Button size="xs" loading={isPending} onClick={() => void save([...pending.values()])}>
          {t('grid.save')}
        </Button>
      </Group>

      {conflicts.length > 0 && (
        <Alert color="orange" title={t('grid.conflictTitle')}>
          {/* ⛔ «Перезаписати мовчки» не є опцією: користувач бачить, чия
              правка і яка саме, і вирішує сам. */}
          <Text size="sm">{t('grid.conflictHint', { count: conflicts.length })}</Text>
        </Alert>
      )}

      <RevoGrid
        theme="compact"
        range
        resize
        columns={columns}
        source={rows}
        readonly={readOnly}
        style={{ height: '70vh' }}
      />

      <Modal opened={rejected.length > 0} onClose={() => setRejected([])} title={t('grid.rejectedTitle')}>
        <Text size="sm" mb="sm">
          {t('grid.rejectedHint')}
        </Text>
        <List size="sm">
          {rejected.map((rejection) => (
            <List.Item key={cellKey(rejection.rowKey, rejection.columnCode)}>
              {rejection.rowKey} · {rejection.columnCode} — {rejection.reason}
            </List.Item>
          ))}
        </List>
      </Modal>
    </Stack>
  );
}

/** Колонки grid із опису зрізу. */
function gridColumns(slice: TableSliceDto, readOnly: boolean): ColumnRegular[] {
  return slice.columns.map((column) => ({
    prop: column.code,
    name: column.header,
    size: 140,

    // ⚠ Право читається з рішення, а не з типу колонки: сіра комірка і
    // «сюди не вставиться» мають відповідати одним правилом.
    readonly: ({ model }) => readOnly || !decide(slice, rowKeyOf(model), column).editable,

    cellProperties: ({ model }) => {
      const decision = decide(slice, rowKeyOf(model), column);

      return decision.editable ? {} : { class: 'ecr-cell-readonly', title: decision.hint };
    },
  }));
}

/** Рядки grid; порожні комірки беруться з `defaultValue` колонки (ФВ-3.8). */
function gridRows(slice: TableSliceDto): GridRow[] {
  return slice.rows.map((row) => {
    const model: GridRow = { __rowKey: row.rowKey };

    for (const column of slice.columns) {
      model[column.code] = row.cells[column.code] ?? column.defaultValue ?? '';
    }

    return model;
  });
}

/**
 * Ключ рядка з моделі, яку віддає grid.
 *
 * ⚠ RevoGrid типізує рядок як довільний словник: він не знає нашого
 * службового поля. Приведення локалізоване в одному місці — інакше `as` жив
 * би в кожному колбеку колонки й перестав би читатися як межа типів.
 */
function rowKeyOf(model: unknown): string {
  return (model as GridRow | undefined)?.__rowKey ?? '';
}

function valueOf(slice: TableSliceDto, rowKey: string, columnCode: string): unknown {
  return slice.rows.find((row) => row.rowKey === rowKey)?.cells[columnCode] ?? null;
}

/**
 * Приводить текст із буфера до типу колонки.
 *
 * ⚠ Число, прочитане як текст, впало б на серверній валідації вже після
 * відправки — тобто користувач побачив би помилку там, де її не робив.
 */
function coerce(raw: string, dataType: string | undefined): unknown {
  if (dataType === 'Decimal' || dataType === 'Int') {
    const value = parseNumber(raw);

    // Нерозпізнане число лишається текстом: сервер відповість
    // ECR-CELL-0422 із назвою колонки, і це чесніше за мовчазний нуль.
    return value ?? raw;
  }

  if (dataType === 'Bool') return raw.trim().toLowerCase() === 'true';

  return raw;
}

/** Колонки для решти екранів; експортується заради повторного використання. */
export type { ColumnDto };

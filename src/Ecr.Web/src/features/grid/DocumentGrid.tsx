import { useCallback, useEffect, useMemo, useRef, useState, type JSX } from 'react';
import { Alert, Badge, Button, Group, List, Modal, Stack, Text } from '@mantine/core';
import { RevoGrid } from '@revolist/react-datagrid';
import type { ColumnRegular } from '@revolist/revogrid';
import { useMutation, useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { ColumnDto, CreateRowRequest, TableSliceDto } from '@/api/types';
import { parseClipboard, planPaste, toClipboard, type PasteRejection } from './clipboard';
import { captureEdit, coerce, valueOf } from './edits';
import { cellStateClass, cellStateOf, type LocalCellFlags } from './cellState';
import { isMissingColumns, isSliceEmpty } from './emptiness';
import { DefaultColumnWidth, readWidths, saveWidths, widthsFromEvent } from './columnWidths';
import { roundToScale, type RoundedCell } from './rounding';
import { cellKey, decide, guardOf } from './permissions';
import { UndoStack, type CellEdit } from './undo';
import {
  buildRequest,
  cellEditKey,
  sendPatchBeacon,
  useCellPatch,
  type PendingEdit,
} from './useCellPatch';
import { createDebouncer, registerUnloadFlush } from './autosave';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { showApiError } from '@/shared/ui/notify';
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

  /**
   * Чи додає рядки користувач.
   *
   * ⛔ Відповідь ДОМЕНУ, а не режим таблиці. Складати `Dynamic || Mixed` на
   * клієнті означало б завести друге визначення того самого правила — і воно
   * вже одного разу розійшлося саме з собою всередині сервера, коштувавши
   * режиму `Mixed` цілком (`A7-41`).
   */
  allowsDynamicRows: boolean;

  /** Стеля кількості рядків; `null` — без стелі. */
  maxDynamicRows: number | null;
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
  const { documentId, tableInstanceId, periodKey, readOnly, allowsDynamicRows, maxDynamicRows } =
    props;

  const slice = useQuery({
    // ⚠ Період лишається в КЛЮЧІ КЕШУ, але не в адресі. Екземпляр таблиці
    // існує на кожен період окремо (R-A6), тобто `tableInstanceId` уже
    // визначає період однозначно — і сервер бере його звідти.
    queryKey: ['table-slice', tableInstanceId, periodKey],

    // ⛔ `?periodKey=` тут був, і сервер його НЕ ЧИТАВ: у дії немає такого
    // параметра взагалі. Шкоди він не завдавав лише випадково — бо
    // збігався з періодом екземпляра. Але виглядав як обіцянка: читач
    // припускав, що період можна задати, а інший період мовчки дав би ті
    // самі дані (`A7-48`). Ловить це сторож параметрів запиту.
    queryFn: () =>
      apiFetch<TableSliceDto>(`/api/v1/documents/${documentId}/tables/${tableInstanceId}`),
  });

  const { patch, isPending, conflicts, status: saveStatus } = useCellPatch(documentId);

  /**
   * Додавання рядка динамічної таблиці (`ФВ-3.2`).
   *
   * ⛔ До аудиту цієї дії в інтерфейсі не було зовсім: ендпоінт існував і
   * працював, а динамічна таблиця лишалася порожньою назавжди — рядок у неї
   * не міг додати ніхто (`A7-39`).
   *
   * ⚠ Ключ не задається: сервер видає GUID у форматі `N`. Просити ключ у
   * користувача означало б віддати йому ідентичність рядка, на яку
   * посилаються формули й аудит.
   */
  const addRow = useMutation({
    mutationFn: () =>
      apiFetch(`/api/v1/documents/${documentId}/rows`, {
        method: 'POST',
        body: JSON.stringify({ tableInstanceId, rowKey: null } satisfies CreateRowRequest),
      }),
    onSuccess: () => void slice.refetch(),

    // ⚠ Стеля рядків і дублікат ключа приходять як `ECR-ROW-0409` з числом у
    // тексті: «досягнуто межу динамічних рядків таблиці: 200». Це те, що
    // людина може зрозуміти й погодити, а «не вдалося» — ні.
    onError: showApiError,
  });
  const history = useRef(new UndoStack(`${tableInstanceId}:${periodKey}`));
  const [rejected, setRejected] = useState<PasteRejection[]>([]);
  const [pending, setPending] = useState<Map<string, PendingEdit>>(new Map());

  // ⚠ `pending` читається з таймера дебаунсу й обробника `beforeunload` —
  // обидва живуть поза React-рендером, і замикання на `pending` там бачило б
  // застиглий знімок з моменту створення. `ref` завжди дає ОСТАННЮ мапу.
  const pendingRef = useRef(pending);
  useEffect(() => {
    pendingRef.current = pending;
  }, [pending]);

  const save = useCallback(
    async (edits: PendingEdit[]) => {
      if (edits.length === 0) return;

      await patch(buildRequest(tableInstanceId, periodKey, edits));
      setPending(new Map());
    },
    [patch, periodKey, tableInstanceId],
  );

  // ⚠ Дебаунс тримає ОДИН стабільний колбек (`autosave.ts`, `#38`): він читає
  // найсвіжіші `pending`/`save` через `ref` (`pendingRef` вище), а не через
  // замикання, — інакше кожен рендер створював би новий дебаунсер і
  // скасовував заплановане збереження попереднього, тобто автозбереження
  // ніколи не спрацьовувало б.
  const saveRef = useRef(save);
  useEffect(() => {
    saveRef.current = save;
  }, [save]);

  const autosaveDebouncer = useRef(
    createDebouncer(() => {
      void saveRef.current([...pendingRef.current.values()]);
    }),
  );

  // ⚠ Останній шанс зберегти перед закриттям вкладки (`B-35`, `#38`):
  // `patch()` не встигне — `beforeunload` не чекає на `fetch` — тому тут іде
  // окремий, «доручи й забудь» запит із `keepalive`.
  useEffect(
    () =>
      registerUnloadFlush(
        () => pendingRef.current.size > 0,
        () =>
          sendPatchBeacon(
            documentId,
            buildRequest(tableInstanceId, periodKey, [...pendingRef.current.values()]),
          ),
      ),
    [documentId, tableInstanceId, periodKey],
  );

  // ⛔ Округлені комірки (D-116, ФВ-9.16c) — перелік із «було → стало», а не
  // прапорець на весь зріз. Позначка ставиться лише на ЗМІНЕНІ комірки: інакше
  // лічильник «округлено N значень» показував би всі числа з дробовою частиною
  // і на нього перестали б дивитися. Оригінал зберігається тому, що
  // повідомлення без переліку — це «щось змінилося, розбирайся сам».
  const [rounded, setRounded] = useState<readonly RoundedCell[]>([]);
  const [showRounded, setShowRounded] = useState(false);

  // ⚠ Лічильник змін історії. Стек живе в `ref` — інакше кожна правка
  // перестворювала б його і губила глибину; але тоді React не знає, що
  // «можна скасувати» змінилося, і кнопки лишалися б назавжди сірими.
  // ⚠ Ширини читаються ОДИН раз на таблицю і далі живуть у стані: читати
  // `localStorage` на кожному рендері таблиці 500×60 означало б розбирати JSON
  // при кожному натисканні клавіші.
  const [widths, setWidths] = useState<Record<string, number>>(() =>
    readWidths(tableInstanceId),
  );

  const [historyRevision, setHistoryRevision] = useState(0);
  const touchHistory = useCallback(() => setHistoryRevision((value) => value + 1), []);

  // ⛔ Історія скидається при переході на іншу таблицю: крок, застосований до
  // чужого зрізу, писав би значення в комірки з тими самими кодами, але
  // іншого документа.
  useEffect(() => {
    history.current.rescope(`${tableInstanceId}:${periodKey}`);
    setPending(new Map());
    setWidths(readWidths(tableInstanceId));
    touchHistory();

    // ⚠ Дебаунс іншої таблиці не має права зберегти правку в цю: без
    // скасування таймер, запланований до переходу, спрацював би вже після
    // нього — з `tableInstanceId`/`periodKey`, зафіксованими в замиканні
    // `autosaveDebouncer`, тобто в чужий зріз.
    autosaveDebouncer.current.cancel();
  }, [tableInstanceId, periodKey, touchHistory]);

  /**
   * Зміна ширини колонки.
   *
   * ⚠ Зберігається одразу, а не «при виході»: користувач закриє вкладку, і
   * подія виходу не спрацює. Обсяг запису — кілька десятків байтів.
   */
  const onColumnResize = useCallback(
    (event: { detail: unknown }) => {
      const changed = widthsFromEvent(event.detail);
      if (Object.keys(changed).length === 0) return;

      saveWidths(tableInstanceId, changed);
      setWidths((current) => ({ ...current, ...changed }));
    },
    [tableInstanceId],
  );

  const data = slice.data;

  // ⚠ Позначки клієнта — окремо від зрізу: сервер не знає ні про незбережені
  // правки, ні про те, що значення округлилося при вставці саме тут.
  const flags = useMemo<LocalCellFlags>(
    () => ({
      dirty: new Set(pending.keys()),
      rounded: new Set(rounded.map((cell) => cellKey(cell.rowKey, cell.columnCode))),
    }),
    [pending, rounded],
  );

  const columns = useMemo(
    () => (data === undefined ? [] : gridColumns(data, readOnly, flags, widths)),
    [data, readOnly, flags, widths],
  );
  const rows = useMemo(() => (data === undefined ? [] : gridRows(data)), [data]);

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
      const byCode = new Map(data.columns.map((column) => [column.code, column]));

      // ⛔ Округлення робиться ТУТ, до надсилання (ФВ-9.16c, D-116). Сервер
      // нічого не округлює: зайвий знак у ручному введенні — помилка автора, а
      // в аркуші Excel — норма джерела. Позначка ставиться лише на комірки, у
      // яких значення справді змінилося.
      const roundedNow: RoundedCell[] = [];

      const edits: PendingEdit[] = plan.targets.map((target) => {
        const column = byCode.get(target.columnCode);
        const value = coerce(target.value, column?.dataType);

        if (typeof value === 'number' && column !== undefined) {
          const fixed = roundToScale(value, column);

          if (fixed !== null) {
            roundedNow.push({
              rowKey: target.rowKey,
              columnCode: target.columnCode,
              original: target.value,
              applied: fixed,
            });

            return {
              rowKey: target.rowKey,
              columnCode: target.columnCode,
              value: fixed,
              isEmpty: false,
              baseVersion: versions.get(target.rowKey) ?? null,
            };
          }
        }

        return {
          rowKey: target.rowKey,
          columnCode: target.columnCode,
          value,
          isEmpty: false,
          baseVersion: versions.get(target.rowKey) ?? null,
        };
      });

      // ⚠ Позначки попередньої вставки знімаються: інакше через десять вставок
      // половина таблиці була б помічена, і лічильник перестав би щось значити.
      setRounded(roundedNow);

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

      touchHistory();
      void save(edits);
    },
    [data, readOnly, save, touchHistory],
  );

  /**
   * Правка з клавіатури.
   *
   * ⛔ Без цього обробника grid показував би введене значення і **не зберігав
   * його**: RevoGrid тримає правку у власній моделі й не знає ні про наш
   * batch-PATCH, ні про історію. Дефект без жодної ознаки збою — таблиця
   * виглядає заповненою, доки її не перевідкриють.
   */
  const onAfterEdit = useCallback(
    (event: { detail: unknown }) => {
      if (data === undefined || readOnly) return;

      const detail = event.detail as
        | { prop?: string | number; model?: unknown; val?: unknown }
        | undefined;

      const captured = captureEdit(data, {
        columnCode: detail?.prop === undefined ? '' : String(detail.prop),
        rowKey: rowKeyOf(detail?.model),
        raw: String(detail?.val ?? ''),
      });

      if (captured === null) return;

      history.current.push({
        label: t('grid.edit', { column: captured.columnHeader }),
        edits: [captured.step],
      });

      touchHistory();

      setPending((current) => {
        const next = new Map(current);
        next.set(cellEditKey(captured.pending), captured.pending);

        return next;
      });

      // ⚠ Кожна правка ПЕРЕЗАПУСКАЄ дебаунс (`B-35`, `#38`): збереження йде
      // через 500 мс тиші ПІСЛЯ ОСТАННЬОЇ правки, а не після першої — інакше
      // швидкий ряд натисків Tab відсилав би окремий запит на кожну клітину.
      autosaveDebouncer.current.trigger();
    },
    [data, readOnly, touchHistory],
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

      touchHistory();

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
    [data, save, touchHistory],
  );

  // ⚠ Правило порожнечі — у чистому модулі `emptiness.ts`, а не тут: воно
  // різне для фіксованої і динамічної таблиці (`S-13`), і саме тому має бути
  // перевіреним окремо від сітки, яку в jsdom не рендерять.
  const sliceEmpty = useCallback(
    (loaded: TableSliceDto) => isSliceEmpty(loaded, allowsDynamicRows),
    [allowsDynamicRows],
  );

  /** Чи справа саме в колонках — від цього залежить, що написано в порожньому стані. */
  const noColumns = isMissingColumns(data);

  return (
    /*
     * ⛔ Чотири стани і тут (`ФВ-14.21`). Раніше зріз мав два: «вантажиться» і
     * «не вдалося», а таблиця без жодного рядка малювалася як звичайна порожня
     * сітка — тобто «даних немає» замість «шаблон не має рядків для цього
     * періоду». Це найдорожчий екран системи, і саме на ньому різниця
     * коштує найбільше.
     */
    <AsyncBoundary<TableSliceDto>
      isPending={slice.isPending}
      error={slice.error}
      data={data}
      isEmpty={sliceEmpty}
      emptyTitle={noColumns ? t('grid.emptyTable') : t('grid.emptyFixedTable')}
      emptyHint={noColumns ? t('grid.emptyTableHint') : t('grid.emptyFixedTableHint')}
      skeleton="table"
      onRetry={() => void slice.refetch()}
    >
      {() => (
    <Stack gap="xs" onPaste={onPaste} onCopy={onCopy} onKeyDown={onKeyDown}>
      <Group gap="xs" key={historyRevision}>
        <Button size="xs" variant="default" disabled={!history.current.canUndo} onClick={() => applyHistory(history.current.undo())}>
          {t('grid.undo')}
        </Button>
        <Button size="xs" variant="default" disabled={!history.current.canRedo} onClick={() => applyHistory(history.current.redo())}>
          {t('grid.redo')}
        </Button>
        <Button
          size="xs"
          loading={isPending}
          disabled={pending.size === 0}
          onClick={() => void save([...pending.values()])}
        >
          {t('grid.save', { count: pending.size })}
        </Button>

        {/*
         * ⚠ Видимий індикатор (`B-35`, `#38`): оператор має бачити, чи
         * дійшла правка до сервера, а не здогадуватися з мовчання. `idle` не
         * показується: порожнє місце в панелі інструментів не привертає
         * уваги там, де нічого не відбувається.
         *
         * ⛔ «Збережено» і «зберігається» — НЕЙТРАЛЬНИЙ текст, не новий
         * колірний токен: обидва статусні кольори теми (`statusError`,
         * `statusWarning`) пройшли перебір контрасту під `primaryShade`
         * цього застосунку (`theme.ts`), а невіряний третій колір саме тут
         * дав би контраст ~2.2–2.8:1 — те, що вже раз ламало а11y-гейт
         * (`W4.2`) для нечіпаних кольорів Mantine.
         */}
        {(saveStatus === 'saving' || saveStatus === 'saved') && (
          <Text size="xs" c="dimmed" data-save-status={saveStatus}>
            {t(saveStatus === 'saving' ? 'grid.saving' : 'grid.saved')}
          </Text>
        )}
        {saveStatus === 'error' && (
          <Badge color="statusError" variant="light" data-save-status="error">
            {t('grid.saveError')}
          </Badge>
        )}

        {/* ⛔ Кнопка є лише там, де рядки додає користувач, і зникає при
            досягненні стелі. Показана в `Fixed` таблиці, вона обіцяла б те,
            що сервер відхилить: склад рядків там заданий шаблоном, і «зайвий»
            рядок зламав би і формули з діапазонами, і звірку з еталоном. */}
        {allowsDynamicRows && !readOnly && (
          <Button
            size="xs"
            variant="default"
            loading={addRow.isPending}
            disabled={maxDynamicRows !== null && (data?.rows.length ?? 0) >= maxDynamicRows}
            onClick={() => addRow.mutate()}
          >
            {t('grid.addRow')}
          </Button>
        )}
      </Group>

      {rounded.length > 0 && (
        // ⛔ Повідомлення, а не діалог підтвердження (ФВ-9.16c). Діалог на
        // кожну вставку з Excel закривали б не читаючи — і правило «мовчки
        // округлювати не можна ніде» перестало б працювати саме там, де воно
        // потрібне найбільше.
        <Alert color="violet" title={t('grid.roundedTitle', { count: rounded.length })}>
          <Group gap="sm">
            <Text size="sm">{t('grid.roundedHint')}</Text>
            <Button size="xs" variant="subtle" onClick={() => setShowRounded(true)}>
              {t('grid.roundedShow')}
            </Button>
          </Group>
        </Alert>
      )}

      {conflicts.length > 0 && (
        <Alert color="statusWarning" title={t('grid.conflictTitle')}>
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
        onAfteredit={onAfterEdit}
        onAftercolumnresize={onColumnResize}
        style={{ height: '70vh' }}
      />

      <Modal
        opened={showRounded}
        onClose={() => setShowRounded(false)}
        title={t('grid.roundedTitle', { count: rounded.length })}
      >
        <List size="sm">
          {rounded.map((cell) => (
            <List.Item key={cellKey(cell.rowKey, cell.columnCode)}>
              {cell.rowKey} · {cell.columnCode} — {cell.original} → {String(cell.applied)}
            </List.Item>
          ))}
        </List>
      </Modal>

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
      )}
    </AsyncBoundary>
  );
}

/**
 * Колонки grid із опису зрізу.
 *
 * ⚠ Стан комірки рахує спільна функція `cellStateOf`, а не цей колбек: п'ять
 * станів, розкладені по місцю використання, розійшлися б на другому ж екрані,
 * а перевірити їх можна було б лише через DOM веб-компонента.
 */
function gridColumns(
  slice: TableSliceDto,
  readOnly: boolean,
  flags: LocalCellFlags,
  widths: Record<string, number>,
): ColumnRegular[] {
  return slice.columns.map((column) => ({
    prop: column.code,

    // ⛔ Одиниця — В ЗАГОЛОВКУ, а не в підказці. Оператор дивиться на
    // числа, а не на підказки, і «12» без одиниці — це або 12 кілограмів,
    // або 12 тонн. Різниця в тисячу разів, і помічає її регулятор.
    //
    // ⚠ Поле приходило порожнім завжди: обидва обробники сервера віддавали
    // `UnitSymbol: null` (`A7-47`). Тепер воно розв'язується з довідника.
    name: column.unitSymbol === null ? column.header : `${column.header}, ${column.unitSymbol}`,

    // Збережена ширина цієї колонки для цього робочого місця (ФВ-14.29).
    size: widths[column.code] ?? DefaultColumnWidth,

    // ⚠ Право читається з рішення, а не з типу колонки: сіра комірка і
    // «сюди не вставиться» мають відповідати одним правилом.
    readonly: ({ model }) => readOnly || !decide(slice, rowKeyOf(model), column).editable,

    cellProperties: ({ model }) => {
      const rowKey = rowKeyOf(model);
      const state = cellStateOf(slice, rowKey, column, flags);

      if (state === null) return {};

      const decision = decide(slice, rowKey, column);

      return {
        class: cellStateClass(state),

        // ⚠ Атрибут окремо від класу: тест читає саме його і тому доводить
        // розрізнення станів, не залежачи від жодного кольору (`ФВ-14.18`).
        'data-cell-state': state,

        // ⚠ Стан доступний і ТЕКСТОМ: форма й колір нічого не кажуть тому, хто
        // працює з читалкою, а причина заборони вже є на сервері.
        ...(decision.hint.length === 0 ? {} : { title: decision.hint }),
      };
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

/** Колонки для решти екранів; експортується заради повторного використання. */
export type { ColumnDto };

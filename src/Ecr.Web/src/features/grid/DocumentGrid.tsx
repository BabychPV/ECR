import { useCallback, useEffect, useMemo, useRef, useState, type JSX } from 'react';
import { Alert, Badge, Button, Group, List, Modal, Stack, Text } from '@mantine/core';
import { RevoGrid } from '@revolist/react-datagrid';
import type { ColumnRegular } from '@revolist/revogrid';
import { useMutation, useQueries, useQuery } from '@tanstack/react-query';
import { apiFetch, EcrApiError, type RequiredInputCell } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { ColumnDto, CreateRowRequest, RegistryDefDto, RegistryEntryDto, TableSliceDto } from '@/api/types';
import { cellAppearanceOf } from './cellAppearance';
import { parseClipboard, planPaste, toClipboard, type PasteRejection } from './clipboard';
import { captureEdit, coerce, valueOf } from './edits';
import { cellStateClass, cellStateOf, type LocalCellFlags } from './cellState';
import { isMissingColumns, isSliceEmpty } from './emptiness';
import { DefaultColumnWidth, readWidths, saveWidths, widthsFromEvent } from './columnWidths';
import { createLookupCellEditor, lookupCellDisplay } from './LookupCellEditor';
import { roundToScale, type RoundedCell } from './rounding';
import { cellKey, confirmationOf, decide, guardOf, rowKeyOfCellKey } from './permissions';
import { UndoStack, type CellEdit } from './undo';
import {
  buildRequest,
  cellEditKey,
  sendPatchBeacon,
  useCellPatch,
  type PendingEdit,
} from './useCellPatch';
import { createDebouncer, registerUnloadFlush } from './autosave';
import { installEnterKeyCompat } from './keyboardCompat';
import { cellsOfSaveError } from './saveErrors';
import {
  TableCornerAnchor,
  clampSelection,
  trackSelection,
  type GridSelection,
} from './selection';
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

  /**
   * Поточне виділення сітки (аудит §10.1).
   *
   * ⛔ `ref`, а не стан: значення читають лише обробники `onPaste`/`onCopy` у
   * момент натискання, і перемальовувати всю сітку на кожен рух виділення
   * мишею означало б перерахувати 500×60 колонок на кожен піксель
   * протягування.
   */
  const selection = useRef<GridSelection | null>(null);
  const [pending, setPending] = useState<Map<string, PendingEdit>>(new Map());

  // ⚠ Значення, підтверджені оператором (`ФВ-2.16`, `#43`), яких сервер ще
  // не бачив: доки не прийде свіжий зріз, grid показує ЇХ, а не збережене
  // значення з `data`, — інакше підтверджений ввід зникав би з екрана до
  // першого успішного збереження.
  const [overrides, setOverrides] = useState<Map<string, unknown>>(new Map());

  // ⚠ Запит на підтвердження — окремий стан, а не частина `pending`: до кліку
  // «Продовжити» значення НЕ застосоване взагалі (`onBeforeEdit` блокує його
  // синхронно), і показувати його як незбережену правку означало б брехати
  // про те, що вже сталося.
  const [confirmRequest, setConfirmRequest] = useState<{
    rowKey: string;
    columnCode: string;
    value: unknown;
    hint: string;
  } | null>(null);

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

      // ⚠ Рядки ЦЬОГО патчу — саме їх обов'язкові-вхідні позначки заміняються
      // нижче. Позначки інших рядків (з попереднього, ще не повтореного
      // збереження) лишаються як є: цей виклик про них нічого не знає.
      const touchedRowKeys = new Set(edits.map((edit) => edit.rowKey));

      try {
        const response = await patch(buildRequest(tableInstanceId, periodKey, edits));

        // ⛔ Аудит 2026-09-16 §10.2 (High, тиха втрата даних): тут стояло
        // `setPending(new Map())` — безумовне очищення ВСІХ незбережених
        // правок, а не лише рядків ЦЬОГО патчу. `save()` кличуть чотири
        // незалежні, несеріалізовані шляхи (автозбереження, Ctrl+S, вставка,
        // undo/redo), і ніщо не забороняє двом запитам бути в дорозі
        // одночасно: повільний патч рядка A, завершившись, викидав правку
        // рядка C, яку ще НІХТО не надсилав. Той самий resolve інвалідує зріз,
        // тож значення зникало і з екрана — без помилки, без позначки
        // «незбережено», без відновлення.
        //
        // ⚠ Фільтр — той самий `touchedRowKeys`, за яким уже фільтруються
        // `requiredInputBlocked`/`saveErrorCells` нижче: успіх патчу
        // стосується РІВНО його рядків і нічиїх більше.
        setPending((prev) => discardRows(prev, touchedRowKeys, (_key, edit) => edit.rowKey));

        // ⚠ Той самий стан, що й `pending`: наступний зріз уже несе справжнє
        // значення, і локальна підстава більше не потрібна нікому — але так
        // само лише для рядків цього патчу. Ключ тут — `rowKey:columnCode`
        // (`cellKey`), тож рядок дістається з ключа, а не зі значення.
        setOverrides((prev) => discardRows(prev, touchedRowKeys, (key) => rowKeyOfCellKey(key)));

        // Успіх означає, що серед рядків цього патчу немає жодного Block:
        // інакше сервер відхилив би весь батч (ECR-CALC-0437), а не повернув
        // 200. Warn — навпаки, приходить САМЕ в успішній відповіді.
        setRequiredInputBlocked((prev) => prev.filter((c) => !touchedRowKeys.has(c.rowKey)));
        setRequiredInputWarnings((prev) => [
          ...prev.filter((c) => !touchedRowKeys.has(c.rowKey)),
          ...response.validation
            .filter(
              (m): m is typeof m & { rowKey: string; columnCode: string } =>
                m.ruleCode === 'ECR-CALC-0437' && m.rowKey !== null && m.columnCode !== null,
            )
            .map((m) => ({
              rowKey: m.rowKey,
              columnCode: m.columnCode,
              ruleCode: m.ruleCode,
              message: m.message,
            })),
        ]);

        // ⚠ Успіх ЦЬОГО патчу знімає банер і маркери лише з рядків, яких він
        // стосувався: помилка іншого, ще не повтореного збереження, не має
        // права мовчки зникнути через УСПІХ чужого патчу.
        setSaveError(null);
        setSaveErrorCells((prev) => prev.filter((c) => !touchedRowKeys.has(c.rowKey)));
      } catch (error) {
        if (error instanceof EcrApiError && error.isRequiredInputMissing) {
          setRequiredInputBlocked((prev) => [
            ...prev.filter((c) => !touchedRowKeys.has(c.rowKey)),
            ...error.requiredInputCells,
          ]);

          // ⚠ `ECR-CALC-0437` уже має власний, повніший `Alert` вище
          // (`requiredInputBlocked`) — другий банер із тим самим по суті
          // повідомленням розсіював би увагу, а не додавав інформацію.
          setSaveError(null);
          setSaveErrorCells((prev) => prev.filter((c) => !touchedRowKeys.has(c.rowKey)));
        } else if (error instanceof EcrApiError) {
          // ⛔ Q-30x (High): ось сам фікс — реальний, локалізований текст
          // сервера («Колонка «C1» очікує число.» і подібні) показується як
          // є, а не губиться в необробленому знеструмленні проміса. Саме
          // цей рядок і мала на увазі заглушка «NOT SAVED — SEE THE ERROR
          // ABOVE», яка досі не мала на що вказувати.
          setSaveError(error.message);
          setSaveErrorCells((prev) => [
            ...prev.filter((c) => !touchedRowKeys.has(c.rowKey)),
            ...cellsOfSaveError(error, edits),
          ]);
        } else {
          setSaveError(String(error));
        }

        // ⚠ Рестрибок НЕ повторюється: жоден викликач (`onPaste`, кнопка
        // «Зберегти», `applyHistory`, автозбереження) не чекає цей проміс і
        // не обробляє відмову сам — усі викликають `void save(...)`. Стан
        // вище вже показує причину користувачеві; повторний `throw` тут
        // давав ЛИШЕ необроблене знеструмлення проміса в консолі (саме
        // симптом, який документує Stage 1), без жодного адресата.
      }
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

  // ⛔ Дебаунс скасовується при РОЗМОНТУВАННІ, а не лише при переході на інший
  // зріз (ефект нижче за текстом, `tableInstanceId`/`periodKey`).
  //
  // ⛔ Попередня версія цього не робила, і це не «витік таймера заради
  // чистоти»: `setTimeout` на 500 мс переживав компонент і через півсекунди
  // після його зникнення виконував `saveRef.current(...)` — тобто НАДСИЛАВ
  // `PATCH` від імені гріда, якого вже немає. Наслідок для оператора: аркуші
  // документа рендеряться списком грідів із `key={tableInstanceId}`
  // (`DocumentPage.tsx`), тож перемикання аркуша розмонтовує гріди
  // попереднього. Правка, введена менш ніж за 500 мс до перемикання, ішла на
  // сервер запитом, чий результат НІКОМУ показати: `setSaveError`,
  // `setConflicts`, діалог порівняння версій і банер «не збережено» належать
  // розмонтованому дереву. Конфлікт `409` чи відмова валідації просто зникали.
  //
  // ⚠ Рішення симетричне вже ухваленому нижче: при зміні зрізу `pending`
  // скидається і дебаунс скасовується — «таймер зрізу, який більше не
  // відкритий, не має права зберігати». Розмонтування — та сама подія, лише
  // остаточна. Закриття вкладки покриває окремий механізм (`beforeunload` +
  // `keepalive` нижче), і саме він, а не випадково вцілілий таймер, є
  // передбаченим шляхом «ми йдемо, відповіді не дочекаємось».
  //
  // ⚠ Той самий взірець уже стоїть у `useCellPatch.ts` для таймера індикатора
  // «збережено» — там його скасування на розмонтуванні описане тим самим
  // аргументом. Незакритим лишався рівно цей один таймер.
  useEffect(() => {
    const debouncer = autosaveDebouncer.current;

    return () => debouncer.cancel();
  }, []);

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

  // ⛔ Директива «обов'язкові вхідні колонки методології»: два різних
  // повідомлення сервера про той самий факт («у рядка вже визначена
  // методологія, і в неї є незаповнений вхід») — Block ВІДХИЛЯЄ батч
  // (`ECR-CALC-0437` з `EcrApiError`), Warn ПРОХОДИТЬ і повертається в
  // `PatchCellsResponse.validation`. Обидва зберігаються по РЯДКАХ, а не
  // цілим зрізом: інша таблиця чи інший рядок не повинні гаснути тут.
  const [requiredInputBlocked, setRequiredInputBlocked] = useState<
    readonly RequiredInputCell[]
  >([]);
  const [requiredInputWarnings, setRequiredInputWarnings] = useState<
    readonly RequiredInputCell[]
  >([]);

  // ⛔ Q-30x (High): раніше справжня причина відмови збереження (наприклад,
  // `Колонка «C1» очікує число.`, ECR-CELL-0422) доїжджала до клієнта
  // коректно, але ніде не показувалась — тулбар малював тільки заглушку
  // «NOT SAVED — SEE THE ERROR ABOVE», а сам текст губився в необробленому
  // знеструмленні проміса, яке бачить лише консоль розробника. `saveError` —
  // ТЕКСТ сервера як є (ФВ-14.24), `saveErrorCells` — комірки, яких він
  // стосується (`cellsOfSaveError`, `saveErrors.ts`), для маркера поверх
  // клітинки за тим самим взірцем, що й обов'язкові вхідні колонки (Q-306).
  //
  // ⚠ НЕ для `ECR-CALC-0437`: та відмова вже має власний, повніший `Alert`
  // (`requiredInputBlocked` нижче) — дублювати той самий текст у двох
  // банерах означало б розсіювати увагу там, де причина вже названа.
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saveErrorCells, setSaveErrorCells] = useState<readonly RequiredInputCell[]>([]);

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
    setOverrides(new Map());
    setConfirmRequest(null);

    // ⚠ Виділення теж належить ЦЬОМУ зрізу: індекси рядка 50 в іншій таблиці
    // вказують на інші дані, і вставка пішла б від чужого якоря.
    selection.current = null;
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

  // ⚠ Директива «обов'язкові вхідні колонки методології»: за ключем комірки,
  // а не станом `cellStateOf` — цей маркер ДОДАЄТЬСЯ поверх будь-якого стану
  // (dirty, readOnly, ...), а не змагається з ним за пріоритет.
  const requiredInputByCell = useMemo(() => {
    const blocked = new Map<string, string>();
    for (const cell of requiredInputBlocked) blocked.set(cellKey(cell.rowKey, cell.columnCode), cell.message);

    const warning = new Map<string, string>();
    for (const cell of requiredInputWarnings) warning.set(cellKey(cell.rowKey, cell.columnCode), cell.message);

    return { blocked, warning };
  }, [requiredInputBlocked, requiredInputWarnings]);

  // ⛔ Q-30x (High): той самий взірець, що й обов'язкові вхідні колонки вище
  // — маркер ЗА КЛЮЧЕМ комірки, що ДОДАЄТЬСЯ поверх будь-якого стану
  // `cellStateOf`, а не змагається з ним. Заповнюється РЕАЛЬНИМИ даними з
  // відповіді сервера (`cellsOfSaveError`, `saveErrors.ts`), не здогадом.
  const saveErrorByCell = useMemo(() => {
    const byCell = new Map<string, string>();
    for (const cell of saveErrorCells) byCell.set(cellKey(cell.rowKey, cell.columnCode), cell.message);

    return byCell;
  }, [saveErrorCells]);

  // ⛔ Директива registry-lookup, PR A4: `ColumnDto.lookupRegistryDefId` — це
  // ID довідника, а ендпоінт записів адресується КОДОМ
  // (`GET /api/v1/registries/{code}/entries`) — те саме розходження, що вже
  // розв'язала `ColumnEditor.tsx` (A3). Резолв іде у два кроки: перелік
  // довідників (ID → код) один раз для будь-якої кількості `Lookup`-колонок,
  // потім по одному запиту записів НА ДОВІДНИК (не на колонку — кілька
  // колонок можуть ділити той самий довідник, `gridColumns` вище).
  const lookupRegistryDefIds = useMemo(
    () => [
      ...new Set(
        (data?.columns ?? [])
          .filter((column) => column.dataType === 'Lookup' && column.lookupRegistryDefId !== null)
          .map((column) => column.lookupRegistryDefId as number),
      ),
    ],
    [data],
  );

  const registriesList = useQuery({
    queryKey: queryKeys.registries.list(),
    queryFn: () => apiFetch<RegistryDefDto[]>('/api/v1/registries'),
    enabled: lookupRegistryDefIds.length > 0,

    // ⚠ Перелік довідників міняється рідко (адміністративна дія, не робота
    // оператора) — довший `staleTime`, ніж дефолтний `App.tsx` (30 с), тут
    // не потрібен: дефолт і так рідко перезапитується в межах одного сеансу
    // роботи з документом.
  });

  const lookupRegistryCodes = useMemo(() => {
    const byId = new Map((registriesList.data ?? []).map((registry) => [registry.id, registry.code]));
    return lookupRegistryDefIds
      .map((id) => ({ id, code: byId.get(id) }))
      .filter((entry): entry is { id: number; code: string } => entry.code !== undefined);
  }, [lookupRegistryDefIds, registriesList.data]);

  const lookupEntriesQueries = useQueries({
    queries: lookupRegistryCodes.map(({ code }) => ({
      queryKey: queryKeys.registries.entries(code),
      queryFn: () => apiFetch<RegistryEntryDto[]>(`/api/v1/registries/${encodeURIComponent(code)}/entries`),
    })),
  });

  const lookupEntriesByRegistryId = useMemo(() => {
    const map = new Map<number, readonly RegistryEntryDto[]>();
    lookupRegistryCodes.forEach(({ id }, index) => {
      const entries = lookupEntriesQueries[index]?.data;
      if (entries !== undefined) map.set(id, entries);
    });

    return map;
  }, [lookupRegistryCodes, lookupEntriesQueries]);

  const columns = useMemo(
    () =>
      data === undefined
        ? []
        : gridColumns(
            data,
            readOnly,
            flags,
            widths,
            requiredInputByCell,
            saveErrorByCell,
            lookupEntriesByRegistryId,
          ),
    [data, readOnly, flags, widths, requiredInputByCell, saveErrorByCell, lookupEntriesByRegistryId],
  );

  // ⚠ `overrides` перекриває значення зі зрізу лише для комірок, підтверджених
  // ЩОЙНО (`ФВ-2.16`, `#43`): сервер про них ще не знає, і без цього шару
  // підтверджений ввід зникав би з екрана до першого успішного збереження.
  const rows = useMemo(
    () => (data === undefined ? [] : gridRows(data, overrides)),
    [data, overrides],
  );

  /** Ctrl+V: розкладає буфер по сітці і відхиляє батч цілком, якщо є заборонені. */
  const onPaste = useCallback(
    (event: React.ClipboardEvent<HTMLDivElement>) => {
      if (data === undefined || readOnly) return;

      const text = event.clipboardData.getData('text/plain');
      if (text.length === 0) return;

      event.preventDefault();

      // ⛔ Аудит 2026-09-16 §10.1: тут стояв жорсткий якір
      // `{ rowIndex: 0, columnIndex: 0 }`. Оператор клацав рядок 50, колонку
      // «C3», вставляв блок з Excel — і значення лягали з рядка 1, колонки 1,
      // тихо перезаписуючи чужі, уже коректні дані; вставка при цьому
      // виглядала «успішною». Тепер якір — той, що його справді обрала людина
      // (`selection.ts`), а кут таблиці лишається лише як стан «нічого не
      // обрано»: Ctrl+V одразу після завантаження не має падати в нікуди.
      const anchor = selection.current?.anchor ?? TableCornerAnchor;

      const plan = planPaste(
        parseClipboard(text),
        data.rows.map((row) => row.rowKey),
        data.columns.map((column) => column.code),
        anchor,
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
   * Захоплює правку в `pending`/історію і планує автозбереження.
   *
   * ⛔ Спільна для двох джерел правки: клавіатура (`onAfterEdit`, нижче) і
   * підтверджена комірка `AllowWithConfirmation` (`onConfirmEdit`, `#43`).
   * Друга копія цієї логіки розійшлася б із першою на першій же зміні
   * правила історії чи дебаунсу.
   */
  const applyEditedValue = useCallback(
    (signal: { columnCode: string; rowKey: string; raw: string }) => {
      if (data === undefined) return null;

      const captured = captureEdit(data, signal);
      if (captured === null) return null;

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

      return captured;
    },
    [data, touchHistory],
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

      applyEditedValue({
        columnCode: detail?.prop === undefined ? '' : String(detail.prop),
        rowKey: rowKeyOf(detail?.model),
        raw: String(detail?.val ?? ''),
      });
    },
    [data, readOnly, applyEditedValue],
  );

  /**
   * `beforeedit`: гейт підтвердження для `AllowWithConfirmation` (`ФВ-2.16`,
   * `#43`).
   *
   * ⛔ Значення блокується СИНХРОННО — `event.preventDefault()` до того, як
   * RevoGrid встигне застосувати його до комірки (`onCellEdit` бібліотеки
   * читає `defaultPrevented` одразу після `emit`). Показ модалки й клік
   * оператора — АСИНХРОННІ, а `beforeedit` ні: єдиний спосіб встигнути
   * запитати підтвердження — не дати grid застосувати значення самому, а
   * застосувати його самим, ПІСЛЯ відповіді, через `applyEditedValue`.
   *
   * ⚠ Комірку без вимоги підтвердження обробник не чіпає взагалі: звичайна
   * правка йде далі своїм шляхом, `afteredit` спрацьовує як завжди.
   */
  const onBeforeEdit = useCallback(
    (event: { detail: unknown; preventDefault: () => void }) => {
      if (data === undefined || readOnly) return;

      const detail = event.detail as
        | { prop?: string | number; model?: unknown; val?: unknown }
        | undefined;

      const columnCode = detail?.prop === undefined ? '' : String(detail.prop);
      const column = data.columns.find((candidate) => candidate.code === columnCode);
      if (column === undefined) return;

      const rowKey = rowKeyOf(detail?.model);
      const hint = confirmationOf(data, rowKey, column);
      if (hint === null) return;

      event.preventDefault();
      setConfirmRequest({ rowKey, columnCode, value: detail?.val, hint });
    },
    [data, readOnly],
  );

  /** Оператор підтвердив правку поза вікном доступу (`ФВ-2.16`, `#43`). */
  const onConfirmEdit = useCallback(() => {
    if (confirmRequest === null) return;

    const captured = applyEditedValue({
      rowKey: confirmRequest.rowKey,
      columnCode: confirmRequest.columnCode,
      raw: String(confirmRequest.value ?? ''),
    });

    // ⚠ Підстава ставиться ЛИШЕ якщо правка справді захоплена: відмова
    // `applyEditedValue` (право забрали між показом діалогу й кліком) не
    // повинна намалювати значення, якого сервер не отримає ніколи.
    if (captured !== null) {
      setOverrides((current) => {
        const next = new Map(current);
        next.set(cellKey(confirmRequest.rowKey, confirmRequest.columnCode), captured.pending.value);

        return next;
      });
    }

    setConfirmRequest(null);
  }, [confirmRequest, applyEditedValue]);

  /**
   * Ctrl+C: віддає ВИДІЛЕНЕ у форматі, який приймає Excel.
   *
   * ⛔ Аудит 2026-09-16 §10.1: тут стояло `data.rows.map(...)` — уся таблиця,
   * незалежно від виділення. Ctrl+C після виділення двох комірок кладе в буфер
   * тисячі, і вставлене далі в Excel не має нічого спільного з тим, що людина
   * позначила.
   *
   * ⚠ Виділення читається з `ref`, а не питається в сітки: `getSelectedRange()`
   * кореневого елемента віддає ОБІЦЯНКУ, а після завершення цього обробника
   * `event.clipboardData` більше не приймає запис — чекати тут неможливо
   * фізично (`selection.ts`).
   *
   * ⚠ «Нічого не виділено» лишається «уся таблиця» — свідома деградація:
   * порожній буфер на Ctrl+C виглядав би як несправність, а в Excel Ctrl+C без
   * виділення так само працює по всьому, що є під фокусом.
   */
  const onCopy = useCallback(
    (event: React.ClipboardEvent<HTMLDivElement>) => {
      if (data === undefined) return;

      const range =
        selection.current === null
          ? null
          : clampSelection(selection.current.range, data.rows.length, data.columns.length);

      const rows = range === null ? data.rows : data.rows.slice(range.fromRow, range.toRow + 1);
      const columns =
        range === null ? data.columns : data.columns.slice(range.fromColumn, range.toColumn + 1);

      event.preventDefault();
      event.clipboardData.setData(
        'text/plain',
        toClipboard(rows.map((row) => columns.map((column) => String(row.cells[column.code] ?? '')))),
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

  // ⛔ Q-30x (Critical): нормалізація Enter для клавіатур без сучасного
  // `KeyboardEvent.key` (`keyboardCompat.ts`) — сама причина Finding 1.
  // Слухач на КОНТЕЙНЕРІ з `capture: true` спрацьовує РАНІШЕ за bubble-
  // обробник RevoGrid на `<input>` редактора, тож дописує `key`, ще до
  // того, як бібліотека встигне його прочитати і мовчки нічого не зробити.
  //
  // ⚠ Callback-ref, а не `useRef` + `useEffect([])`: контейнер рендериться
  // лише в дочірній функції `AsyncBoundary`, ПІСЛЯ завантаження зрізу — ефект
  // із порожніми залежностями спрацював би раз, до монтування вузла, і ніколи
  // більше. Callback-ref натомість викликається рівно тоді, коли вузол
  // з'являється чи зникає, незалежно від умовного рендеру.
  //
  // ⛔ Аудит §10.1: тим самим callback-ref підписуємось і на події ВИДІЛЕННЯ
  // (`selection.ts`). Причина та сама, що вище: контейнер існує лише після
  // завантаження зрізу, тож ефект із порожніми залежностями його не побачив би
  // — а `focuscell`/`setrange` треба слухати на вузлі, до якого вони
  // піднімаються з тіньового дерева `revo-grid`.
  const gridListenersCleanup = useRef<(() => void)[]>([]);
  const gridContainerRef = useCallback((node: HTMLDivElement | null) => {
    for (const cleanup of gridListenersCleanup.current) cleanup();
    gridListenersCleanup.current = [];

    if (node === null) return;

    gridListenersCleanup.current = [
      installEnterKeyCompat(node),
      trackSelection(node, (next) => {
        selection.current = next;
      }),
    ];
  }, []);

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
          <Text size="xs" c="dimmed" role="status" aria-live="polite" data-save-status={saveStatus}>
            {t(saveStatus === 'saving' ? 'grid.saving' : 'grid.saved')}
          </Text>
        )}
        {saveStatus === 'error' && (
          <Badge color="statusError" variant="light" role="alert" data-save-status="error">
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

      {saveError !== null && (
        // ⛔ Q-30x (High): САМЕ СЮДИ вказує «NOT SAVED — SEE THE ERROR
        // ABOVE» з бейджа тулбару вище — раніше під цим написом не було
        // нічого, а справжня причина губилася в консолі. Текст — рівно той,
        // що назвав сервер (ФВ-14.24): код розрізняє причини, текст —
        // людині.
        <Alert color="statusError" title={t('grid.saveError')} role="alert">
          <Text size="sm">{saveError}</Text>
        </Alert>
      )}

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

      {requiredInputBlocked.length > 0 && (
        // ⛔ Директива «обов'язкові вхідні колонки методології»: батч
        // ВІДХИЛЕНО (ECR-CALC-0437), комірки лишаються dirty. Перелік називає
        // КОНКРЕТНІ колонки — «дані неповні» саме по собі нічого не пояснює.
        <Alert
          color="statusError"
          title={t('grid.requiredInputBlockedTitle', { count: requiredInputBlocked.length })}
        >
          <List size="sm">
            {requiredInputBlocked.map((cell) => (
              <List.Item key={`${cell.rowKey}:${cell.columnCode}`}>{cell.message}</List.Item>
            ))}
          </List>
        </Alert>
      )}

      {requiredInputWarnings.length > 0 && (
        // ⚠ На відміну від блоку вище — запис ПРОЙШОВ. Це підказка, а не
        // відмова, і лишається видимою, доки колонку не заповнять.
        <Alert
          color="statusWarning"
          title={t('grid.requiredInputWarningTitle', { count: requiredInputWarnings.length })}
        >
          <List size="sm">
            {requiredInputWarnings.map((cell) => (
              <List.Item key={`${cell.rowKey}:${cell.columnCode}`}>{cell.message}</List.Item>
            ))}
          </List>
        </Alert>
      )}

      <div ref={gridContainerRef} style={{ display: 'contents' }}>
        <RevoGrid
          theme="compact"
          range
          resize
          columns={columns}
          source={rows}
          readonly={readOnly}
          onBeforeedit={onBeforeEdit}
          onAfteredit={onAfterEdit}
          onAftercolumnresize={onColumnResize}
          style={{ height: '70vh' }}
        />
      </div>

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

      {/*
       * ⛔ `AllowWithConfirmation` (`ФВ-2.16`, `#43`). До цієї модалки правка
       * поза вікном доступу проходила МОВЧКИ — так само, як звичайна: третя
       * поведінка з трьох, названих вимогою, не відрізнялася від першої.
       * Закриття без кнопки (хрестик, `Esc`, клік поза) трактується як
       * СКАСУВАННЯ: `applyEditedValue` тут не викликаний узагалі, тож
       * скасовувати нічого не треба — значення й так ніколи не потрапляло
       * в комірку (`onBeforeEdit` заблокував його синхронно).
       */}
      <Modal
        opened={confirmRequest !== null}
        onClose={() => setConfirmRequest(null)}
        title={t('grid.confirmTitle')}
      >
        <Text size="sm" mb="md">
          {confirmRequest?.hint}
        </Text>
        <Group justify="flex-end" gap="xs">
          <Button size="xs" variant="default" onClick={() => setConfirmRequest(null)}>
            {t('grid.confirmCancel')}
          </Button>
          <Button size="xs" onClick={onConfirmEdit}>
            {t('grid.confirmProceed')}
          </Button>
        </Group>
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
export function gridColumns(
  slice: TableSliceDto,
  readOnly: boolean,
  flags: LocalCellFlags,
  widths: Record<string, number>,
  requiredInput: { blocked: ReadonlyMap<string, string>; warning: ReadonlyMap<string, string> },
  saveErrorByCell: ReadonlyMap<string, string> = new Map(),

  // ⛔ Директива registry-lookup, PR A4: перелік записів на РЕЄСТР
  // (`ColumnDto.lookupRegistryDefId`), не на колонку — кілька `Lookup`-колонок
  // однієї таблиці можуть посилатись на той самий довідник, і дублювати запит
  // означало б потрапити в ту саму пастку, яку вже виправив пакетний
  // `StatesBatchAsync` (`Q-325`, коментар вище) — по одному запиту на кожну
  // з них замість одного на довідник.
  lookupEntriesByRegistryId: ReadonlyMap<number, readonly RegistryEntryDto[]> = new Map(),
): ColumnRegular[] {
  return slice.columns.map((column) => {
    // ⛔ Обов'язковість — НА СІТЦІ, ДО спроби зберегти, а не лише в момент
    // відхиленого PATCH. Дві незалежні осі зливаються в один сигнал
    // (`ColumnDto.IsRequiredByMethodology` навмисно документує це як «інша
    // вісь, той самий екран» — `02-contracts.md`): звичайна структурна
    // обов'язковість (`IsRequired`) і обов'язковий вхід чинної методології
    // — оператору однаково байдуже ЗВІДКИ вимога, важливо лише «заповни це
    // до подання».
    const isRequired = column.isRequired || column.isRequiredByMethodology;
    const requiredHint = isRequired ? t('grid.columnRequiredHint') : null;

    // ⛔ Директива registry-lookup, PR A4: перелік — за `lookupRegistryDefId`
    // ЦІЄЇ колонки. `null`/відсутній у мапі (запит ще вантажиться, або
    // колонку налаштовано без довідника) — редактор і показ деградують до
    // порожнього переліку, а не падають: комірка лишається текстовим
    // інпутом на секунду вантаження, не помилкою на екрані.
    const lookupEntries =
      column.dataType === 'Lookup' && column.lookupRegistryDefId !== null
        ? (lookupEntriesByRegistryId.get(column.lookupRegistryDefId) ?? [])
        : null;

    return {
      prop: column.code,

      // ⛔ Одиниця — В ЗАГОЛОВКУ, а не в підказці. Оператор дивиться на
      // числа, а не на підказки, і «12» без одиниці — це або 12 кілограмів,
      // або 12 тонн. Різниця в тисячу разів, і помічає її регулятор.
      //
      // ⚠ Поле приходило порожнім завжди: обидва обробники сервера віддавали
      // `UnitSymbol: null` (`A7-47`). Тепер воно розв'язується з довідника.
      //
      // ⚠ Зірочка ДОДАЄТЬСЯ до вже сформованого заголовка (з одиницею чи
      // без), а не замінює його: обидва сигнали мають лишатися видимими
      // одночасно.
      name:
        (column.unitSymbol === null ? column.header : `${column.header}, ${column.unitSymbol}`) +
        (isRequired ? ' *' : ''),

      // Збережена ширина цієї колонки для цього робочого місця (ФВ-14.29).
      size: widths[column.code] ?? DefaultColumnWidth,

      // ⚠ Зірочка в заголовку — це ЗНАК, а не пояснення: читалка екрана й
      // наведення миші мають почути/побачити ПОВНИЙ текст вимоги, а не лише
      // символ (той самий принцип, що й `hint` у `cellProperties` нижче).
      ...(requiredHint === null ? {} : { columnProperties: () => ({ title: requiredHint }) }),

      // ⛔ Директива registry-lookup, PR A4: `Lookup`-колонка редагується
      // dropdown-ом записів довідника (`LookupCellEditor.ts`), не звичайним
      // текстовим інпутом RevoGrid, і показує `entry.Display`, а не сирий
      // `ValueRegistryEntryId`, поки комірка НЕ редагується.
      ...(lookupEntries === null
        ? {}
        : {
            editor: createLookupCellEditor(lookupEntries),
            cellTemplate: (_h, props: { value?: unknown }) =>
              lookupCellDisplay(props.value, lookupEntries),
          }),

      // ⚠ Право читається з рішення, а не з типу колонки: сіра комірка і
      // «сюди не вставиться» мають відповідати одним правилом.
      readonly: ({ model }) => readOnly || !decide(slice, rowKeyOf(model), column).editable,

      cellProperties: ({ model }) => {
        const rowKey = rowKeyOf(model);
        const state = cellStateOf(slice, rowKey, column, flags, readOnly);
        const key = cellKey(rowKey, column.code);

        // ⛔ Директива «обов'язкові вхідні колонки методології»: маркер
        // ДОДАЄТЬСЯ до класу стану, а не замінює його — Block і Warn не
        // беруть участі в пріоритеті `cellStateOf` (`ФВ-14.18` рахує лише
        // п'ять виміряних станів; переробляти ту палітру заради двох нових —
        // окрема задача, не ця).
        const requiredInputMessage = requiredInput.blocked.get(key) ?? requiredInput.warning.get(key);
        const requiredInputClass = requiredInput.blocked.has(key)
          ? 'ecr-cell-required-input-blocked'
          : requiredInput.warning.has(key)
            ? 'ecr-cell-required-input-warning'
            : null;

        // ⛔ Q-30x (High): той самий взірець маркера, що й обов'язкові вхідні
        // колонки вище — ДОДАЄТЬСЯ до класу стану, а не змагається з ним за
        // пріоритет `cellStateOf`. На відміну від Block/Warn, тут завжди
        // РІВНО одна причина на комірку (остання відповідь сервера), тому й
        // маркер один, без варіанту blocked/warning.
        const saveErrorMessage = saveErrorByCell.get(key) ?? null;
        const saveErrorClass = saveErrorMessage === null ? null : 'ecr-cell-save-error';

        // ⛔ Директива registry-lookup / cell-style, PR B2: оформлення
        // автора шаблону — ШАР ПІД будь-яким станом (`cellStateOf` вище),
        // не заміна: рахується ЗАВЖДИ, незалежно від того, чи спрацював
        // хоч один з інших маркерів, — інакше жирна колонка без стилю
        // фарбувалась би, лише щойно комірку зроблено `dirty`.
        const appearance = cellAppearanceOf(column.style);

        if (state === null && requiredInputClass === null && saveErrorClass === null && appearance === undefined) {
          return {};
        }

        const decision = decide(slice, rowKey, column);

        // ⛔ Аудит Етапу 3, лана "Documents core": `decision.hint` порожній
        // САМЕ тоді, коли причина — не бізнес-правило комірки, а стан
        // ПОДАННЯ (`readOnly` пропу, вище) — сервер вважає комірку
        // дозволеною, це грід ЦІЛКОМ замкнено ззовні. Без цього фолбеку
        // заштрихована комірка мала б `cursor: not-allowed`, але жодного
        // ТЕКСТУ — той самий дефект, half-fixed.
        const submittedHint = readOnly && decision.editable ? t('grid.submittedReadOnlyHint') : null;

        // Стан доступний і ТЕКСТОМ, не лише кольором/формою: причина заборони
        // чи незаповненого входу вже є на сервері — читалка має її почути.
        const hint = [decision.hint, submittedHint, requiredInputMessage, saveErrorMessage]
          .filter((part) => !!part)
          .join(' ');

        return {
          // ⚠ Базовий `ecr-cell` завжди присутній, навіть коли `state === null`:
          // від нього залежить `position: relative` і резерв місця під маркер
          // (`cell-states.css`), а маркер обов'язкового входу — свій маркер.
          class: [state === null ? 'ecr-cell' : cellStateClass(state), requiredInputClass, saveErrorClass]
            .filter((part): part is string => part !== null)
            .join(' '),

          // ⚠ Атрибут окремо від класу: тест читає саме його і тому доводить
          // розрізнення станів, не залежачи від жодного кольору (`ФВ-14.18`).
          ...(state === null ? {} : { 'data-cell-state': state }),

          ...(hint.length === 0 ? {} : { title: hint }),

          // ⚠ `backgroundColor`/`verticalAlign` НЕМАЄ серед перенесених полів
          // — див. коментар `cellAppearanceOf` (`cellAppearance.ts`): перший
          // ховав би індикатор стану під кольором автора, другий не робить
          // нічого на звичайному `<div>`.
          ...(appearance === undefined ? {} : { style: appearance }),
        };
      },
    };
  });
}

/**
 * Рядки grid; порожні комірки беруться з `defaultValue` колонки (ФВ-3.8).
 *
 * @param overrides Значення, підтверджені оператором (`ФВ-2.16`, `#43`), яких
 * сервер ще не бачив — перекривають і збережене значення, і `defaultValue`,
 * бо мають ознаку «оператор це щойно ввів», а не «так було до нього».
 */
function gridRows(slice: TableSliceDto, overrides?: ReadonlyMap<string, unknown>): GridRow[] {
  return slice.rows.map((row) => {
    const model: GridRow = { __rowKey: row.rowKey };

    for (const column of slice.columns) {
      const key = cellKey(row.rowKey, column.code);

      model[column.code] = overrides?.has(key)
        ? overrides.get(key)
        : (row.cells[column.code] ?? column.defaultValue ?? '');
    }

    return model;
  });
}

/**
 * Знімає з мапи записи названих рядків — і лише їх (аудит §10.2).
 *
 * ⛔ Не `new Map()`. Успіх ОДНОГО патчу нічого не каже про правки, яких він не
 * стосувався: вони або ще в дорозі іншим запитом, або ще не надіслані взагалі.
 * Викинути їх означає втратити введене оператором без сліду — і саме це тут і
 * відбувалося.
 *
 * ⚠ Нова мапа повертається лише якщо щось справді змінилося: інакше кожен
 * успішний патч віддавав би React новий об'єкт, а з ним — новий `flags` і
 * перерахунок усіх колонок сітки на 500×60 комірок.
 *
 * @param rowOf Як дістати рядок із записи мапи: `pending` тримає його в
 * значенні (`PendingEdit.rowKey`), `overrides` — у ключі (`cellKey`).
 */
function discardRows<V>(
  current: Map<string, V>,
  rowKeys: ReadonlySet<string>,
  rowOf: (key: string, value: V) => string,
): Map<string, V> {
  const next = new Map<string, V>();

  for (const [key, value] of current) {
    if (!rowKeys.has(rowOf(key, value))) next.set(key, value);
  }

  return next.size === current.size ? current : next;
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

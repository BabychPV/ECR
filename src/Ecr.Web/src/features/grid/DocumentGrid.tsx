import { useCallback, useEffect, useMemo, useRef, useState, type JSX } from 'react';
import { Alert, Badge, Button, Group, List, Modal, Stack, Text } from '@mantine/core';
import { RevoGrid } from '@revolist/react-datagrid';
import type { ColumnRegular } from '@revolist/revogrid';
import { useMutation, useQueries, useQuery } from '@tanstack/react-query';
import { apiFetch, EcrApiError, type RequiredInputCell } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { ColumnDto, CreateRowRequest, RegistryDefDto, RegistryEntryDto, TableSliceDto } from '@/api/types';
import { cellAppearanceOf } from './cellAppearance';
import { cellDisplay, cellText, isNumericColumn } from './cellValue';
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
  conflictTimeLabel,
  useCellPatch,
  useRecalculationStatus,
  type PendingEdit,
} from './useCellPatch';
import { registerSliceSaver, scheduleAutosave } from './autosave';
// ⚠ Ключ комірки СХОВИЩА під власним іменем: у цьому файлі вже є `cellKey`
// з `permissions.ts`, і хоч обидва дають `rowKey:columnCode`, ключем мапи
// правок має бути рівно той, яким її будує сам сховищний модуль.
import {
  cellKey as pendingCellKey,
  discardPendingRows,
  putPendingEdit,
  usePendingSlice,
} from './pendingStore';
import { installEnterKeyCompat } from './keyboardCompat';
import { cellsOfSaveError } from './saveErrors';
import {
  TableCornerAnchor,
  clampSelection,
  trackFocusedCell,
  trackSelection,
  type GridSelection,
} from './selection';
import { publishFocus } from './focusStore';
import { GridFormulaBar } from './GridFormulaBar';
import {
  columnTotals,
  isTotalsRow,
  totalsRow,
  type ColumnTotal,
  type GridTotalsRow,
} from './gridTotals';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showApiError } from '@/shared/ui/notify';
import { useRowHeight } from '@/shared/theme/preferences';
import { t } from '@/shared/i18n';

/**
 * Остання календарна дата періоду (`periodKey` — `YYYYMM`, той самий формат,
 * що вже кодує `DocumentPage.tsx`/`shared/ui/PeriodPicker.tsx`) як
 * `"YYYY-MM-DD"` — `asOf` для темпоральних Lookup-довідників комірок: документ
 * за березень має бачити довідник станом на березень, а не на сьогодні
 * (ФВ-8.5).
 *
 * ⛔ Не переюзано з `PeriodPicker.tsx`: розбір `periodKey` там лишається
 * приватним (`parsePeriodKey` не експортовано), а сам файл — поза межами
 * дозволених для цієї задачі. Формула ТА САМА (`YYYYMM`, місяць `1..12`),
 * продубльована тут як кілька рядків, а не переосмислена вдруге.
 *
 * `null` — `periodKey` не в очікуваному форматі (місяць поза `1..12`):
 * викликач тоді не надсилає `asOf`, а не падає на невалідній даті.
 */
function periodEndDateIso(periodKey: number): string | null {
  const year = Math.trunc(periodKey / 100);
  const month = periodKey - year * 100;

  if (month < 1 || month > 12) return null;

  // День `0` наступного місяця — останній день ЦЬОГО: конструктор `Date`
  // сам нормалізує переповнення (грудень → січень наступного року).
  const lastDay = new Date(year, month, 0).getDate();

  return `${String(year).padStart(4, '0')}-${String(month).padStart(2, '0')}-${String(lastDay).padStart(2, '0')}`;
}

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
 * Ім'я властивості моделі, під яким живе ПІДПИС рядка.
 *
 * ⛔ Подвійне підкреслення — та сама домовленість, що й у `__rowKey`: службове
 * поле не має права зіткнутися з кодом колонки таблиці. Код колонки —
 * латиниця, цифри й підкреслення (`EcrCode`), тож формально зіткнення
 * можливе; префікс робить його неможливим на практиці й водночас читається як
 * «це не дані».
 *
 * ⚠ Ця колонка НЕ входить у `slice.columns`, тому вставка з буфера
 * (`planPaste` отримує саме `slice.columns.map(c => c.code)`), збереження
 * (`PATCH` шле коди колонок) і стани комірок її не бачать узагалі. Це не
 * випадковість, а причина, чому підпис зроблено окремою колонкою, а не
 * псевдоколонкою в самому зрізі.
 */
const RowLabelProp = '__rowLabel';

/**
 * Підпис рядка, яким його бачить оператор.
 *
 * ⚠ Запасний варіант — `rowKey`, і це не вигадка: так само робить сторінка
 * версії шаблону (`TemplateVersionPage`, `row.label ?? row.rowKey`). Порожній
 * підпис у формі з фіксованими рядками гірший за технічний ключ: ключ
 * принаймні дає за що зачепитися очима й що назвати в листі підтримці.
 */
function rowLabelOf(row: TableSliceDto['rows'][number]): string {
  return row.label ?? row.rowKey;
}

/**
 * Чи є в зрізі хоч один рядок із власним підписом.
 *
 * ⛔ Колонка підпису з'являється лише тоді, коли підписи справді є. У таблиці
 * з динамічними рядками (`RowMode` дозволяє додавати свої) підписів немає за
 * побудовою — `Label` там `null` на кожному рядку, — і колонка з самими
 * технічними ключами відбирала б ширину в даних, нічого не пояснюючи.
 */
function hasRowLabels(slice: TableSliceDto): boolean {
  return slice.rows.some((row) => row.label !== null && row.label.length > 0);
}

/**
 * Переводить індекс колонки СІТКИ (те, що несуть події RevoGrid і читає
 * `selection.ts` — `GridSelection.anchor.columnIndex`, `SelectionRange.from/
 * toColumn`) в індекс колонки ДАНИХ (`data.columns`, тобто `slice.columns`).
 *
 * ⛔ Коли є підписи рядків, `gridColumns` (нижче) вставляє колонку
 * `RowLabelProp` ПЕРШОЮ — і зсуває решту колонок на одну позицію праворуч.
 * `selection.ts` про це не знає навмисно (модуль розбирає сиру подію, без
 * контексту зрізу — див. коментар над `FocusedCell`): `columnIndex`/`x`/`x1`
 * там — координати у ВІДРЕНДЕРЕНИХ колонках. Без цього перетворення
 * `onPaste`/`onCopy` на будь-якій таблиці з підписами рядків (практично всі
 * форми з фіксованими рядками) працюють зі зсувом на одну колонку праворуч:
 * та сама категорія дефекту, що аудит §10.1 уже закривав для якоря рядка.
 *
 * @returns Індекс у `data.columns`, кламплений до 0. Колонка підпису сама —
 * `readonly`, тож для неї немає відповідного індексу в `data.columns`; кут
 * `gridColumnIndex === 0` (сама колонка підпису, коли підписи є) зводиться до
 * першої колонки ДАНИХ, а не до від'ємного індексу — так вставка в підпис
 * мовчки нічого туди не пише (`planPaste` однаково працює лише з кодами
 * `data.columns`), а копіювання з діапазону, що зачіпає підпис, не тягне за
 * собою зайву колонку даних за межею вибраного.
 */
function dataColumnIndexOf(gridColumnIndex: number, data: TableSliceDto): number {
  return hasRowLabels(data) ? Math.max(0, gridColumnIndex - 1) : gridColumnIndex;
}

/**
 * Ширина колонки підпису за замовчуванням.
 *
 * ⚠ Ширша за колонку даних (`DefaultColumnWidth`), бо несе не число, а назву
 * показника — «Валові викиди діоксиду вуглецю» не вміщується в ширину, якої
 * вистачає на `1 234,56`. Оператор може змінити її, і зміна зберігається тим
 * самим механізмом, що й для решти колонок.
 */
const RowLabelColumnWidth = 260;

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

  const {
    patch,
    isPending,
    conflicts,
    moreConflicts,
    status: saveStatus,
    recalculationJobId,
  } = useCellPatch(documentId);

  // ⚠ `BE-05`: стеження за перерахунком — ЛИШЕ читання стану задачі. Зріз
  // цей хук не чіпає взагалі (ні `invalidateQueries`, ні `refetch`): саме
  // перезапит зрізу на кожне збереження прибрали `CL-01…03`, і повертати його
  // під виглядом «оновити обчислені колонки» не можна.
  const recalc = useRecalculationStatus(recalculationJobId);

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

  /**
   * Незбережені правки ЦЬОГО зрізу — вигляд над сховищем документа
   * (`pendingStore.ts`, `D14-12`).
   *
   * ⛔ Тут стояв `useState`, і саме він коштував даних (`W-02`): стан помирав
   * разом із сіткою, а сітки розмонтовує і перемикання аркуша, і прокрутка
   * (`SheetTables.tsx`). Правка молодша за 500 мс зникала МОВЧКИ. Тепер сітка
   * лише ПОКАЗУЄ те, що належить документу: `putPendingEdit` пише,
   * `discardPendingRows` підтверджує, а хто і коли надішле — рівень вище
   * (`autosave.ts`).
   *
   * ⚠ Перемальовуються не всі сітки на кожне натискання: `usePendingSlice`
   * віддає знімок ЗРІЗУ, а `useSyncExternalStore` порівнює його за посиланням
   * — сітки, чий зріз не змінився, бачать те саме посилання й не рендеряться.
   */
  const pending = usePendingSlice(tableInstanceId, periodKey);

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

  const save = useCallback(
    async (edits: PendingEdit[]) => {
      if (edits.length === 0) return;

      // ⚠ Рядки ЦЬОГО патчу — саме їх обов'язкові-вхідні позначки заміняються
      // нижче. Позначки інших рядків (з попереднього, ще не повтореного
      // збереження) лишаються як є: цей виклик про них нічого не знає.
      const touchedRowKeys = new Set(edits.map((edit) => edit.rowKey));

      // ⚠ Знімок ТОГО, ЩО ПІШЛО на сервер, — за ним `discardPendingRows`
      // відрізнить підтверджену правку від тієї, яку оператор зробив у ту саму
      // комірку, доки patch летів.
      const sent = new Map(edits.map((edit) => [pendingCellKey(edit), edit]));

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
        //
        // ⚠ `D14-12`: підтвердження йде у СХОВИЩЕ документа, а не в стан
        // сітки. `sent` тут обов'язковий — без нього зникла б і правка, яку
        // оператор зробив у ту саму комірку, доки цей патч був у дорозі.
        discardPendingRows(tableInstanceId, periodKey, touchedRowKeys, sent);

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

  // ⚠ `save` читають ззовні React-рендера (автозбереження документа), тож
  // потрібне ОСТАННЄ його втілення, а не те, що було на момент підписки.
  const saveRef = useRef(save);
  useEffect(() => {
    saveRef.current = save;
  }, [save]);

  /*
   * ⛔ `D14-12`, крок 2. Тут стояло скасування дебаунсу при розмонтуванні —
   * і це був свідомий обмін «краще втратити правку, ніж надіслати `PATCH` від
   * мертвого компонента» (`W-02`). Обміну більше немає: правка живе в сховищі
   * документа, а план збереження — в одному дебаунсері на документ
   * (`autosave.ts`). Сітка лише оголошує себе зберігачем СВОГО зрізу на час,
   * доки вона на екрані: поки вона тут — результат патчу видно їй (банер,
   * конфлікти, маркери комірок); щойно її немає — зріз бере документ.
   *
   * ⚠ Реєструється стабільна обгортка над `saveRef`, а не сам `save`:
   * інакше кожна зміна `save` (а він залежить від `patch`) перереєстровувала б
   * зберігача, і між зняттям та встановленням існувало б вікно, у якому зріз
   * виглядав би безхазяйним.
   */
  useEffect(
    () =>
      registerSliceSaver(tableInstanceId, periodKey, (edits) => {
        void saveRef.current([...edits]);
      }),
    [tableInstanceId, periodKey],
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

    // ⚠ `pending` тут більше НЕ скидається, і це не недогляд: правки лежать у
    // сховищі за ключем `tableInstanceId:periodKey`, тож зміна зрізу просто
    // переводить погляд на інший ключ. Скидати означало б стерти чуже —
    // правки зрізу, з якого оператор щойно пішов і куди може повернутися.
    setOverrides(new Map());
    setConfirmRequest(null);

    // ⚠ Виділення теж належить ЦЬОМУ зрізу: індекси рядка 50 в іншій таблиці
    // вказують на інші дані, і вставка пішла б від чужого якоря.
    selection.current = null;

    // ⚠ І фокус — з тієї самої причини (`UI-08`): рядок формули показував би
    // вираз колонки з тим самим номером, але з іншої таблиці.
    publishFocus(tableInstanceId, periodKey, null);

    setWidths(readWidths(tableInstanceId));
    touchHistory();

    // ⚠ І дебаунс тут більше не скасовується. Раніше це було обов'язкове
    // прибирання за собою: таймер ніс `tableInstanceId`/`periodKey` у
    // замиканні й після переходу зберіг би правку в ЧУЖИЙ зріз. Тепер
    // запланований пакет будується з самого сховища, де кожна правка вже
    // лежить під ключем свого зрізу, — переплутати їх нічим.
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
    const byId = new Map(
      (registriesList.data ?? []).map((registry) => [registry.id, registry] as const),
    );
    return lookupRegistryDefIds
      .map((id) => {
        const registry = byId.get(id);
        return { id, code: registry?.code, isTemporal: registry?.isTemporal ?? false };
      })
      .filter(
        (entry): entry is { id: number; code: string; isTemporal: boolean } =>
          entry.code !== undefined,
      );
  }, [lookupRegistryDefIds, registriesList.data]);

  /*
   * ⛔ Дефект живого прогону: `GET …/entries` вимагав `asOf` БЕЗУМОВНО, і
   * жоден Lookup-піцкер комірки сітки його не надсилав — сервер (фікс у
   * `GetRegistryEntriesHandler.cs`, той самий PR) відмовляв `422` для
   * КОЖНОГО довідника, включно з нетемпоральним. Тепер `asOf` іде лише для
   * ТЕМПОРАЛЬНОГО довідника — і це дата КІНЦЯ ПЕРІОДУ документа
   * (`periodEndDateIso`), а не «сьогодні»: комірка березневого документа має
   * пропонувати записи, чинні в березні (ФВ-8.5), навіть якщо сьогодні
   * жовтень.
   */
  const lookupAsOf = periodEndDateIso(periodKey);

  const lookupEntriesQueries = useQueries({
    queries: lookupRegistryCodes.map(({ code, isTemporal }) => {
      const asOf = isTemporal ? lookupAsOf : null;

      // ⚠ Базовий шлях — ОКРЕМИЙ шаблонний рядок, без `?asOf=` усередині:
      // `EndpointCoverageTests.Кожна_адреса_яку_викликає_клієнт_існує_на_сервері`
      // бере ВЕСЬ вміст МІЖ парою лапок як адресу — рядок запиту в тому
      // самому літералі виглядав би для неї окремим неіснуючим маршрутом.
      const baseUrl = `/api/v1/registries/${encodeURIComponent(code)}/entries`;

      return {
        queryKey: [...queryKeys.registries.entries(code), asOf],
        queryFn: () =>
          apiFetch<RegistryEntryDto[]>(
            asOf === null ? baseUrl : `${baseUrl}?asOf=${asOf}`,
          ),
      };
    }),
  });

  const lookupEntriesByRegistryId = useMemo(() => {
    const map = new Map<number, readonly RegistryEntryDto[]>();
    lookupRegistryCodes.forEach(({ id }, index) => {
      const entries = lookupEntriesQueries[index]?.data;
      if (entries !== undefined) map.set(id, entries);
    });

    return map;
  }, [lookupRegistryCodes, lookupEntriesQueries]);

  /*
   * ⛔ Відмова довідника — НЕ те саме, що «довідник ще їде» і не те саме, що
   * «колонку налаштовано без довідника». Коментар нижче (`gridColumns`) каже
   * правильну річ: редактор деградує до порожнього переліку, а не падає — і це
   * лишається. Але третій випадок — сервер ВІДМОВИВ — потрапляв у ту саму
   * гілку мовчки, і саме він коштує найдорожче.
   *
   * ⚠ Чому дорожче за решту цього класу: це єдине місце, яке щодня бачить
   * ОПЕРАТОР, а не адміністратор. Порожній список у комірці `Lookup` він читає
   * як «довідник не наповнили» — і або зупиняє заповнення й пише
   * конфігураторові неправдиву скаргу, або (комірка ж деградувала до тексту)
   * вписує значення руками, і в документ їде рядок, якого в довіднику немає.
   *
   * ⚠ Банер, а не тост: тост зникає за кілька секунд, а заповнення аркуша йде
   * годинами. Комірки при цьому лишаються робочими — відмова довідника не
   * привід забрати в оператора решту таблиці.
   */
  const lookupError =
    registriesList.error ?? lookupEntriesQueries.find((query) => query.error !== null)?.error ?? null;

  const refetchLookups = (): void => {
    void registriesList.refetch();
    lookupEntriesQueries.forEach((query) => void query.refetch());
  };

  /**
   * Підсумки колонок — `UI-08`.
   *
   * ⛔ Залежить і від `pending`: сума має відповідати тому, що ВИДНО, а
   * незбережена правка видна одразу. `pending` і так змінюється на кожну
   * правку (від нього залежить `flags` вище), тож жодного зайвого
   * перемальовування це не додає — саме тому підсумок і не живе в окремому
   * стані з власним оновленням.
   *
   * ⚠ Від `data` він теж залежить, а `data` після `CL-01…03` не
   * перезапитується на збереження — тобто лічильник рендерів на `PATCH` цей
   * рядок не рухає.
   */
  const totals = useMemo(
    () => (data === undefined ? new Map<string, ColumnTotal>() : columnTotals(data, { pending, overrides })),
    [data, pending, overrides],
  );

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
            totals,
          ),
    [
      data,
      readOnly,
      flags,
      widths,
      requiredInputByCell,
      saveErrorByCell,
      lookupEntriesByRegistryId,
      totals,
    ],
  );

  // ⚠ `overrides` перекриває значення зі зрізу лише для комірок, підтверджених
  // ЩОЙНО (`ФВ-2.16`, `#43`): сервер про них ще не знає, і без цього шару
  // підтверджений ввід зникав би з екрана до першого успішного збереження.
  const rows = useMemo(
    () => (data === undefined ? [] : gridRows(data, overrides)),
    [data, overrides],
  );

  /**
   * Закріплений рядок підсумків — `UI-08`.
   *
   * ⛔ `pinnedBottomSource` бібліотеки, а не власний `<div>` під сіткою.
   * Сітка горизонтально прокручується (до 60 колонок), і намальований поруч
   * рядок лишався б на місці, доки дані їдуть убік, — тобто підписував би
   * суми не тим колонкам. RevoGrid 4.11 закріплення знизу підтримує
   * (`pinnedBottomSource` у `@revolist/revogrid/dist/types/components.d.ts`),
   * і рядок прокручується разом із колонками.
   *
   * ⚠ Порожній масив, доки зрізу немає: `undefined` бібліотека читає як
   * «властивість не задано» і в частині шляхів падає на `.length`.
   */
  const pinnedTotals = useMemo<GridTotalsRow[]>(
    () =>
      data === undefined || totals.size === 0
        ? []
        : [
            totalsRow(
              data,
              totals,
              t('grid.totalsRowLabel'),
              hasRowLabels(data) ? RowLabelProp : null,
            ),
          ],
    [data, totals],
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

      // ⛔ `anchor.columnIndex` — індекс у сітці (див. `dataColumnIndexOf`):
      // на таблиці з підписами рядків без цієї поправки вставка лягала на
      // одну колонку правіше від тієї, куди справді клацнув оператор.
      const dataAnchor = {
        rowIndex: anchor.rowIndex,
        columnIndex: dataColumnIndexOf(anchor.columnIndex, data),
      };

      const plan = planPaste(
        parseClipboard(text),
        data.rows.map((row) => row.rowKey),
        data.columns.map((column) => column.code),
        dataAnchor,
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

        if (column !== undefined) {
          // ⛔ Сюди йде СИРИЙ текст буфера, а не `coerce`-нуте число:
          // `Number(text)` уже втратив би знаки, яких у `decimal(25,16)`
          // рівно шістнадцять (`rounding.ts`).
          const fixed = roundToScale(target.value, column);

          if (fixed !== null) {
            // ✎ 2026-09-21: борг закрито. Тут стояло `const applied =
            // Number(fixed)` з поясненням «сервер десяткове рядком ще не
            // приймає» — тобто весь рядковий шлях округлення закінчувався
            // поверненням у `double` за один крок до мережі, і шістнадцятий
            // знак зникав саме на головному шляху введення. Після `e470777a`
            // сервер приймає `decimal` рядком, а `PatchCell.value` — `unknown`,
            // тож рядок їде як є: те, що показано оператору в переліку
            // «округлено», і те, що лежить у тілі запиту, — один і той самий
            // текст.
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

      // ⚠ `D14-12`: правка потрапляє у сховище ДОКУМЕНТА одразу, ще до
      // будь-якого надсилання. Саме тому вона переживає і розмонтування
      // сітки, і перемикання аркуша — компонент більше не єдине місце, де
      // вона існує.
      putPendingEdit(tableInstanceId, periodKey, captured.pending);

      // ⚠ Кожна правка ПЕРЕЗАПУСКАЄ дебаунс (`B-35`, `#38`): збереження йде
      // через 500 мс тиші ПІСЛЯ ОСТАННЬОЇ правки, а не після першої — інакше
      // швидкий ряд натисків Tab відсилав би окремий запит на кожну клітину.
      // Дебаунсер один на документ (`autosave.ts`), і він переживе цю сітку.
      scheduleAutosave();

      return captured;
    },
    [data, touchHistory, tableInstanceId, periodKey],
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

      // ⛔ `selection.current.range.from/toColumn` — індекси в сітці (див.
      // `dataColumnIndexOf`): без поправки Ctrl+C на таблиці з підписами
      // рядків копіював вікно, зсунуте на одну колонку, і за межею вибраного
      // діапазону міг прихопити зайву колонку.
      const range =
        selection.current === null
          ? null
          : clampSelection(
              {
                ...selection.current.range,
                fromColumn: dataColumnIndexOf(selection.current.range.fromColumn, data),
                toColumn: dataColumnIndexOf(selection.current.range.toColumn, data),
              },
              data.rows.length,
              data.columns.length,
            );

      const rows = range === null ? data.rows : data.rows.slice(range.fromRow, range.toRow + 1);
      const columns =
        range === null ? data.columns : data.columns.slice(range.fromColumn, range.toColumn + 1);

      event.preventDefault();

      // ⛔ `cellText`, а не `String(...)`: після `e470777a` десяткове приходить
      // рядком у масштабі колонки, і `String()` клав би в буфер
      // `5.0000000000` замість `5` — у КОЖНУ комірку аркуша, який оператор
      // потім вставляє в Excel. Число те саме, аркуш — нечитабельний.
      event.clipboardData.setData(
        'text/plain',
        toClipboard(rows.map((row) => columns.map((column) => cellText(row.cells[column.code])))),
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
  /**
   * Висота рядка сітки — `UI-03`.
   *
   * ⛔ Числом, а не CSS-ом, і це не вибір: RevoGrid віртуалізує подання —
   * рахує, скільки рядків помістилося і на скільки пікселів зсунути полотно.
   * Висота, задана стилем повз `rowSize`, посунула б намальоване відносно
   * того, що бібліотека вважає видимим: комірка під курсором виявилася б не
   * тією, у яку йде введення.
   *
   * ⚠ Значення — з тієї самої змінної `--ecr-row-height`, що її читають усі
   * таблиці (`tokens.css`), а не з окремого числа: інакше сітка документа
   * жила б у власній щільності, розходячись із рештою екрана на кожну зміну
   * макета.
   *
   * ⚠ `theme` лишається `compact` НЕЗАЛЕЖНО від щільності, і це свідомо.
   * «Тема» RevoGrid — це шкура (шрифт `Nunito`, шапка капслоком, зашиті
   * `#000`/`#f8f9fa` у `theme=default`), а не щільність; міняти її разом із
   * висотою рядка означало б міняти шрифт і кольори шапки — і вийти
   * з-під сторожа контрасту, зведеного саме для `[theme=compact]`
   * (`gridCellContrast.test.ts`).
   */
  const rowSize = useRowHeight();

  const gridListenersCleanup = useRef<(() => void)[]>([]);
  const gridContainerRef = useCallback(
    (node: HTMLDivElement | null) => {
      for (const cleanup of gridListenersCleanup.current) cleanup();
      gridListenersCleanup.current = [];

      if (node === null) return;

      gridListenersCleanup.current = [
        installEnterKeyCompat(node),
        trackSelection(node, (next) => {
          selection.current = next;
        }),

        // ⛔ `UI-08`: фокус публікується у СХОВИЩЕ, а не в стан компонента.
        // Стан тут коштував би перебудови всього опису колонок (`gridColumns`,
        // до 60 замикань) на кожну стрілку клавіатури; сховище будить рівно
        // `GridFormulaBar` (`focusStore.ts`).
        trackFocusedCell(node, (cell) => {
          publishFocus(tableInstanceId, periodKey, cell);
        }),
      ];
    },
    [tableInstanceId, periodKey],
  );

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
      {/*
       * ⛔ Перше, що видно: довідник не завантажився. Раніше тут не було
       * НІЧОГО — випадний список у комірці просто ставав порожнім, і оператор
       * читав це як «довідник не наповнили».
       */}
      {lookupError !== null && <ErrorAlert error={lookupError} onRetry={refetchLookups} />}

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

        {/*
         * ⛔ `BE-05`: статус-рядок перерахунку. До ідентифікатора задачі у
         * відповіді на `PATCH` клієнт міг лише вгадувати таймером, коли
         * обчислені колонки оновляться, — тобто не показував нічого, і
         * оператор не знав, чи перерахунок іще йде, чи вже впав.
         *
         * ⚠ Окремий індикатор, а не розширення `saveStatus`: «збережено» і
         * «перераховано» — різні факти й різні моменти. Записано вже тоді,
         * коли перерахунок тільки поставлено в чергу; злити їх в один напис
         * означало б або зарано сказати «готово», або тримати «зберігається»
         * на правці, яка давно в базі.
         *
         * ⚠ `unknown` (стан прочитати не вдалося) навмисно мовчить — той
         * самий вибір, що в `ExportButton`: причина в праві на читання задачі,
         * а не в перерахунку, і показ помилки звинуватив би його в тому, чого
         * він не робив.
         */}
        {recalc.outcome === 'running' && (
          <Text size="xs" c="dimmed" role="status" aria-live="polite" data-recalc-status="running">
            {t('grid.recalculating')}
          </Text>
        )}
        {recalc.outcome === 'succeeded' && recalc.finishedAt !== null && (
          <Text size="xs" c="dimmed" role="status" aria-live="polite" data-recalc-status="succeeded">
            {t('grid.recalculated', { time: recalc.finishedAt })}
          </Text>
        )}
        {recalc.outcome === 'failed' && (
          <Badge color="statusWarning" variant="light" role="alert" data-recalc-status="failed">
            {t('grid.recalcFailed')}
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

          {/* ⛔ `BE-06`: перелік, а не саме лише число. Лічильник «змінено
              комірок: 3» не веде до жодної дії — людина не дізнається ні що
              саме розійшлося, ні чия це правка, ні коли вона сталася, а
              вирішувати «беру їхнє / лишаю своє» доводиться саме за цим.
              Сервер до цієї роботи й не мав чого сказати: поля заповнювалися
              заглушками. */}
          <List size="sm">
            {conflicts.map((conflict) => (
              <List.Item key={`${conflict.rowKey}:${conflict.columnCode}`}>
                {t('grid.conflictItem', {
                  row: conflict.rowKey,
                  column: conflict.columnCode,
                  // ⚠ `cellText`, не `String(...)`: чуже значення приходить тим
                  // самим десятковим рядком, і «їхнє значення 12.4000000000»
                  // у реченні, за яким людина вирішує «беру їхнє / лишаю
                  // своє», читалося б як інше число.
                  value:
                    conflict.theirValue === null || conflict.theirValue === undefined
                      ? t('grid.conflictNoValue')
                      : cellText(conflict.theirValue),

                  // ⚠ `null` означає «невідомо», і воно так і написано словом.
                  // Порожнє місце на цьому рядку читалося б як «ніхто».
                  user: conflict.theirUser ?? t('grid.conflictUnknownUser'),
                  time: conflictTimeLabel(conflict.theirChangedAt) ?? t('grid.conflictUnknownTime'),
                })}
              </List.Item>
            ))}
          </List>

          {moreConflicts > 0 && (
            // ⛔ Стеля переліку — 100 комірок; решта не має зникати мовчки.
            // Людина, яка бачить сто рядків із трьохсот, вважає, що бачить усі.
            <Text size="sm">{t('grid.conflictMore', { count: moreConflicts })}</Text>
          )}
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

      {/* ⛔ `UI-08`: рядок формули НАД сіткою — там, де він стоїть в Excel і
          де око шукає «що в комірці, на якій я стою». Під сіткою його
          закривав би закріплений рядок підсумків. */}
      {data !== undefined && (
        <GridFormulaBar
          tableInstanceId={tableInstanceId}
          periodKey={periodKey}
          slice={data}
          columns={columns}
          rows={rows}
          labelProp={RowLabelProp}
          pending={pending}
        />
      )}

      <div ref={gridContainerRef} style={{ display: 'contents' }}>
        <RevoGrid
          theme="compact"
          rowSize={rowSize}
          range
          resize
          columns={columns}
          source={rows}
          pinnedBottomSource={pinnedTotals}
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
              {cell.rowKey} · {cell.columnCode} — {cell.original} → {cell.applied}
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

  // ⛔ `UI-08`: підсумки потрібні САМІЙ колонці, а не лише рядку моделі —
  // кількість заповнених комірок їде підказкою на клітинці підсумку. Сума без
  // «скільки значень її склало» читається як підсумок по ВСІХ рядках колонки,
  // хоч би скільки з них були порожні.
  totals: ReadonlyMap<string, ColumnTotal> = new Map(),
): ColumnRegular[] {
  // ⚠ Тип оголошений ЯВНО, а не виведений із `map`. Без нього лямбди
  // всередині (`readonly`, `cellProperties`, `cellTemplate`) втрачають
  // контекстний тип, який доти давав їм сам тип повернення функції, — і
  // `model` стає `any`. Те саме значення, але мовчки без перевірок.
  const dataColumns: ColumnRegular[] = slice.columns.map((column) => {
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

      /*
       * ⛔ Показ десяткового (`e470777a`). Доти числова комірка малювалася
       * тим, що RevoGrid зробить із значення моделі сама, — і це працювало
       * рівно доти, доки `decimal` був JSON-числом: `JSON.parse` мовчки
       * прибирав хвостові нулі, тож `5.0000000000` доїжджало як `5`. Тепер
       * значення приходить РЯДКОМ і малюється як є: оператор бачить
       * `5.0000000000` у кожній комірці, де ввів `5`.
       *
       * ⚠ Формат — `shared/format` і жодного власного правила: `en` →
       * `1,234.5`, `ru`/`kk` → `1 234,5`. І жодного `Number(...)` на шляху:
       * шістнадцятий знак має дійти до екрана, а `double` його не тримає.
       *
       * ⚠ Лише ПОКАЗ. Редактор RevoGrid бере значення з моделі рядка, а не з
       * шаблону, тож у полі введення лишається сам запис — і Ctrl+C віддає
       * його ж (`cellText`, вище), бо групування розрядів Excel прочитав би
       * як текст.
       *
       * ⚠ Колонка `Lookup` сюди не потрапляє: її шаблон уже заданий вище, і
       * порядок полів це гарантує — `dataType` у них різний.
       */
      ...(lookupEntries !== null || !isNumericColumn(column)
        ? {}
        : {
            cellTemplate: (_h, props: { value?: unknown }) => cellDisplay(props.value, column),
          }),

      // ⚠ Право читається з рішення, а не з типу колонки: сіра комірка і
      // «сюди не вставиться» мають відповідати одним правилом.
      //
      // ⛔ `UI-08`: рядок підсумків — ЗАВЖДИ лише для читання, і перевіряється
      // він ПЕРШИМ. Це не «про всяк випадок»: його `__rowKey` у зрізі
      // відсутній, а `decide()` на невідомому рядку звичайної колонки чесно
      // віддає «можна» (запис у `cellPermissions` немає — отже дозвіл). Без
      // цієї перевірки RevoGrid відкрив би редактор на сумі, а `captureEdit`
      // мовчки викинув би введене — правка, яка виглядає зробленою і нікуди
      // не доїжджає.
      readonly: ({ model }) =>
        isTotalsRow(model) || readOnly || !decide(slice, rowKeyOf(model), column).editable,

      cellProperties: ({ model }) => {
        // ⛔ `UI-08`: підсумок — не комірка документа. Ні станів
        // (`cellStateOf` рахує їх для рядка, якого в зрізі немає), ні
        // маркерів помилок, ні підказок про права: усе це стосувалося б
        // рядка, якого користувач не вводив.
        if (isTotalsRow(model)) {
          const total = totals.get(column.code);

          return {
            'data-grid-totals': 'cell',
            ...(total === undefined
              ? {}
              : { title: t('grid.totalsCellHint', { count: total.count }) }),
          };
        }

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

  // ⛔ Дефект, який це закриває: підпис рядка сервер РАХУЄ Й ЛОКАЛІЗУЄ
  // (`GetTableSliceHandler`: `Label: r.Def?.LabelL10n.Get(language)`), кладе в
  // контракт (`TableSliceDto.Label`) — а сітка документа не читала його
  // ЖОДНОГО РАЗУ. Для форми з фіксованими рядками це означає таблицю, у якій
  // рядки нічим не відрізняються: оператор бачить стовпчик чисел і не знає,
  // котре з них викиди, а котре — витрата палива. Показував підпис лише
  // адміністративний екран версії шаблону, тобто той, куди оператор не
  // заходить.
  //
  // ⚠ Колонка ПЕРША і тільки для читання. Перша — бо підпис ідентифікує
  // рядок, і місце ідентифікатора там, де око починає читати; тільки для
  // читання — бо це не дані документа, а опис структури: правити його можна
  // рівно там, де він заведений, у версії шаблону.
  return hasRowLabels(slice)
    ? [
        {
          prop: RowLabelProp,
          name: t('grid.rowLabelHeader'),
          readonly: true,
          size: widths[RowLabelProp] ?? RowLabelColumnWidth,

          // ⚠ Порожньої клітинки тут не буває: `rowLabelOf` завжди дає або
          // підпис, або технічний ключ. `title` — щоб довгий підпис можна було
          // прочитати цілком, не розтягуючи колонку.
          cellTemplate: (createElement, cell) => {
            const label = String(cell.model[RowLabelProp] ?? '');

            return createElement('span', { class: 'ecr-row-label', title: label }, label);
          },
        },
        ...dataColumns,
      ]
    : dataColumns;
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

    // ⚠ Підпис кладеться в модель ЗАВЖДИ, а колонка для нього з'являється лише
    // там, де є що показати (`hasRowLabels`). Умовне поле зробило б форму
    // рядка непостійною, а `RevoGrid` читає значення за іменем властивості —
    // відсутність поля й порожній підпис виглядали б однаково.
    model[RowLabelProp] = rowLabelOf(row);

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
 * @param rowOf Як дістати рядок із запису мапи: `overrides` тримає його в
 * ключі (`cellKey`).
 *
 * ⚠ Лишилося рівно одне застосування — `overrides`. Те саме правило для
 * незбережених правок тепер живе в сховищі документа
 * (`pendingStore.discardPendingRows`), і воно там СУВОРІШЕ: звіряє ще й
 * знімок надісланого, тобто не чіпає правку, зроблену вже після patch.
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

import { lazy, Suspense, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type JSX } from 'react';
import { isPeriodOutsideProject, nearestPeriod } from '@/features/documents/periodBounds';
import { formatPeriodKey } from '@/shared/format';
import { isHeaderFinding } from '@/features/documents/inspector/headerFinding';
import { validationCheckedAt } from '@/features/documents/inspector/validationCheckedAt';
import { Alert, Button, Code, Group, Skeleton, Stack, Tabs, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery } from '@tanstack/react-query';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { problemText } from '@/shared/ui/problemText';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { apiFetch, EcrApiError } from '@/api/client';
import type {
  DocumentPeriodRequest,
  DocumentSummary,
  DocumentTableDto,
  PeriodCalendarDto,
  ValidationResultResponse,
} from '@/api/types';
import {
  hasProjectWriteGrant,
  useBusinessKeyChangeAction,
} from '@/features/documents/BusinessKeyChangeAction';
import {
  DeleteDocumentPermission,
  useDeleteDocumentAction,
} from '@/features/documents/DeleteDocumentAction';
import { DocumentSaveState } from '@/features/documents/DocumentSaveState';
import { StaleResultsUndeterminedMark } from '@/features/documents/StaleResultsMark';
import { useDocumentLogActions } from '@/features/documents/DocumentLogActions';
import { useStaleResultsReminderHost } from '@/features/documents/useStaleResultsReminder';
import { useVersionMigrationAction } from '@/features/documents/VersionMigrationAction';
import { useEditAssignmentsAction } from '@/features/editAssignments/EditAssignmentsAction';
import { documentLockOf, hasLockedSheet, locksDataActions } from '@/features/documents/documentLock';
import { useDocumentPeriodReopen } from '@/features/documents/periodReopen/useDocumentPeriodReopen';
import { DocumentProgress } from '@/features/documents/DocumentProgress';
import { yearStateOfPeriods } from '@/features/documents/documentSheets';
import { permitRowWarningsOf, publishRowFindings } from '@/features/grid/rowAttentionStore';
import type { HeldEdit } from '@/features/grid/pendingStore';
import { editsDeferToServer, isEditable } from '@/features/workflow/sheetEditability';
import { fetchSubmissionTarget, submissionTargetKey, useApproverEditAvailability } from '@/features/workflow/api';
import {
  hasCalculationIssues,
  hasCalculationProblems,
  useUnmatchedRows,
  useYearUnmatchedRows,
  type MonthUnmatchedRows,
} from '@/features/documents/useUnmatchedRows';
import { can, useSession } from '@/shared/session/useSession';
import { localized } from '@/shared/i18n/localized';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { useNarrowScreen } from '@/shared/narrowScreen';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showApiError } from '@/shared/ui/notify';
import { PageHeader } from '@/shared/ui/PageHeader';
import { useProjectCurrentPeriodDefault } from '@/features/documents/useProjectCurrentPeriodDefault';
import {
  isYearLayout,
  yearAggregatePeriodState,
  yearAnchorCalendar,
  yearAnchorOf,
  yearAnchors,
} from '@/features/documents/yearLayout';
import { AllMonthsLink, YearLayoutChips } from '@/features/calcResults/DocumentResultsLinks';
import { SheetStrip } from '@/features/documents/SheetStrip';
import { copyFieldsOf, sheetTabKey } from '@/features/documents/sheetCopies/sheetCopyTypes';
import {
  chatMessageCountOf,
  groupBySheet,
  recalculatedAfterSubmissionOf,
  sheetItemsOf,
  stateOfGroup,
} from '@/features/documents/sheetCopies/sheetGroups';
import { parseChatKey } from '@/features/tableChat/chatKey';
import {
  clearScrollBeforePeriodSwitch,
  peekScrollBeforePeriodSwitch,
  rememberScrollBeforePeriodSwitch,
} from '@/features/documents/periodSwitchScroll';
import { useUrlNumber, useUrlParamsSetter, useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';
import { registerHeldEditRevealer, useSettledAction } from '@/features/grid/settleEdits';
import { NoticeLine } from '@/features/documents/notices/NoticeLine';
import { usePublishNotice } from '@/features/documents/notices/noticeStore';
import { holdScrollPosition } from '@/features/documents/holdScrollPosition';

/**
 * Панелі нижче — за `import()`, а не статичним імпортом (`D-132`).
 *
 * ⚠ Спільне для всіх чотирьох: жодна не потрібна в момент першого малюнка
 * сторінки. `ImportPanel` (✎ UI-14: тепер у `DocumentActionBar`) і `CalculationResultsPanel` рендерилися
 * УМОВНО (право/стан), тож користувач без права чи на поданому аркуші сьогодні
 * і так їх не бачить — статичний імпорт лише змушував ЙОГО бандл нести код,
 * якого він не покаже. `WorkflowHistory` і `DocumentVersionCompare` — пункти «More»
 * (`DocumentLogActions`) і лінивий діалог: до відкриття жодного запиту.
 *
 * ⚠ Стиль — той самий, що в `features/search/SearchLauncher.tsx`
 * (`loadX`/`lazy(async () => ...)`), а не інлайн `lazy(() => import(...).then(...))`
 * з `lazyDataSourceForm.ts`: тут немає окремого "прогріву" на hover/focus, бо
 * елементи не за кліком у панелі команд, а одразу в дереві сторінки.
 *
 * ⛔ `DocumentGrid`/`SheetTables` тут навмисно НЕ займані: той чанк має
 * власну, вже виміряну причину не використовувати `<Suspense>` (коментар
 * нижче й `features/grid/SheetTables.tsx`) — ця картка змінює лише сторінку
 * навколо нього.
 */

/** ✎ копії аркушів: дії «Add copy / Rename / Delete copy» над смугою вкладок — лінивий чанк (`D-132`). */
const SheetCopyBar = lazy(async () => ({
  default: (await import('@/features/documents/sheetCopies/SheetCopyBar')).SheetCopyBar,
}));

const loadCalculationResultsPanel =() => import('@/features/methodologies/CalculationResultsPanel');
const CalculationResultsPanel = lazy(async () => ({
  default: (await loadCalculationResultsPanel()).CalculationResultsPanel,
}));


/**
 * П'ята лінива панель — шапка документа (`GET/PATCH …/documents/{id}/header`).
 *
 * ⚠ Той самий прийом, що чотири вище: модуль вантажиться лише тоді, коли
 * панель ДІЙСНО з'являється на екрані. На відміну від журналу й порівняння версій, показ тут вирішує не право чи розгортання, а
 * ВІДПОВІДЬ сервера (порожній перелік полів — панелі немає) — тому запит
 * усередині компонента неминучий; лінивим лишається лише сам код панелі.
 */
const loadDocumentHeaderPanel = () => import('@/features/documents/DocumentHeaderPanel');
const DocumentHeaderPanel = lazy(async () => ({
  default: (await loadDocumentHeaderPanel()).DocumentHeaderPanel,
}));

/**
 * Інспектор документа справа: Issues / History / Info (`UI-25`) — лінивий чанк
 * (`D-132`). Він замінив вбудований перелік зауважень над сітками: макет
 * (`docs/design/hybrid`, KIT.md §4) тримає зауваження в закритому за
 * замовчуванням `.aside`, а над сітками — лише кнопку «K issues».
 */
const loadDocumentInspector = () => import('@/features/documents/inspector/DocumentInspector');
const DocumentInspector = lazy(async () => ({
  default: (await loadDocumentInspector()).DocumentInspector,
}));

/** ✎ TC: чат таблиці — кнопка «Discussion» і права панель; обидва в лінивому чанку (`D-132`), панель — у другому всередині. */
const loadTableChat = () => import('@/features/tableChat/TableChatEntry');
const TableChatButton = lazy(async () => ({ default: (await loadTableChat()).TableChatButton }));
const TableChatHost = lazy(async () => ({ default: (await loadTableChat()).TableChatHost }));

/**
 * Поле періоду в шапці — за `import()` (`D-132`): воно тягне `NumberInput`
 * (~8 КБ gzip), а маршрут стояв на 246 із 250.
 *
 * ⚠ На відміну від панелей вище, поле видиме в першому ж кадрі ДОКУМЕНТА. Тому
 * чанк починає вантажитись разом із модулем сторінки — паралельно із запитом
 * документа, до відповіді якого шапки однаково немає (`AsyncBoundary`). Поки
 * чанк не прийшов, місце тримає `Skeleton` того ж розміру — шапка не стрибає.
 */
const loadPeriodPicker = () => import('@/shared/ui/PeriodPicker');
void loadPeriodPicker();
const PeriodPicker = lazy(async () => ({ default: (await loadPeriodPicker()).PeriodPicker }));
// ✎ LAND-YEAR L7a: вибір місяця для «Reopen … for this document» документа «рік» — лише коли є що відкривати (D-132).
const YearMonthReopen = lazy(async () => ({
  default: (await import('@/features/documents/periodReopen/YearMonthReopen')).YearMonthReopen,
}));
// ✎ b14 doc-notices-compact (замість банера-списку L7c): відкриті для документа місяці року — один чип у шапці,
// окремим чанком і лише коли такі є (D-132).
const ReopenedMonthsChip = lazy(async () => ({
  default: (await import('@/features/documents/periodReopen/ReopenedMonthsChip')).ReopenedMonthsChip,
}));
/*
 * B113: заглушка — та сама коробка, що й сам вибір періоду (`.ecr-period`: висота `--ecr-ctl-height`, 28/36 px за
 * щільністю). Була 52 px: поки чанк `PeriodPicker` ще вантажився, шапка була вищою за остаточну, і коли чанк доїжджав,
 * рядок дій під нею підстрибував угору (стенд, чанк притримано: «More» y 152 → 148, тобто 4 px CLS).
 */
const periodPickerFallback = <Skeleton h="var(--ecr-ctl-height, 28px)" w={190} />;

/**
 * ✎ debt-1110: панель дій аркуша (`DocumentActionBar` + модель дій `SheetActions`, тулбар, вибір періоду подання) —
 * за `import()` тим самим прийомом, що й поле періоду: маршрут стояв на 249.6 з 250 КБ (`D-132`), а ця панель —
 * ~19 КБ сирого коду чанка сторінки. Чанк починає вантажитись разом із модулем сторінки, паралельно із запитом
 * документа, до відповіді якого панелі однаково немає; поки він не прийшов, місце тримає `Skeleton` висоти панелі.
 */
const loadDocumentActionBar = () => import('@/features/documents/DocumentActionBar');
void loadDocumentActionBar();
const DocumentActionBar = lazy(async () => ({ default: (await loadDocumentActionBar()).DocumentActionBar }));

// ✎ D-442 / AN-129: банер невідповідних рядків (D-14) — окремий чанк: він малюється лише за наявності таких
// рядків, а маршрут після П.1 і П.3 стояв рівно на межі 250 КБ (`D-132`).
// ✎ AN-129: смуга виконавців (EA-5b) — окремий чанк: без призначень вона нічого не малює (`return null`).
const ExecutorStrip = lazy(async () => ({
  default: (await import('@/features/editAssignments/ExecutorStrip')).ExecutorStrip,
}));
// ✎ B78: ОДНА панель «Проблеми розрахунку» замість банерів «Formula errors» і «Some rows are not included» — лінива,
// як були вони (бюджет маршруту 250 КБ, D-132).
// ✎ b14 doc-notices-compact: панелі місяців зведено в ОДНУ смугу (панелі — її «Technical details»), той самий чанк-підхід.
const CalculationIssuesStrip = lazy(async () => ({
  default: (await import('@/features/documents/calcIssuesStrip/CalculationIssuesStrip')).CalculationIssuesStrip,
}));

// PM-11: «З дозволу» + «Оновити з дозволу» — окремий чанк (бюджет маршруту D-132).
const PermitRefreshStrip = lazy(async () => ({
  default: (await import('@/features/permits/bindings/PermitRefreshStrip')).PermitRefreshStrip,
}));

// D-132 (batch-1118-1, bundle budget 250.2 KB > 250): the lock banner shows on this page only WITHOUT an active sheet
// (with a sheet the action bar draws it) — a rare path, so its own chunk instead of the route's static graph.
const DocumentLockBanner = lazy(async () => ({
  default: (await import('@/features/documents/DocumentLockBanner')).DocumentLockBanner,
}));
// The same D-132 fix: the service page is only for a missing document (404) - its own chunk.
const ServiceStatePage = lazy(async () => ({
  default: (await import('@/app/ServiceStatePage')).ServiceStatePage,
}));

// Feature (j) Ship 2 (jh2/jh3): rows to split after a mid-month header permit change — its own chunk.
const HeaderSplitBanner = lazy(async () => ({
  default: (await import('@/features/permits/history/HeaderSplitBanner')).HeaderSplitBanner,
}));

/**
 * Пропозиція повернути загублені правки — сьома лінива панель (`D-132`).
 *
 * ⚠ Без загублених правок банер не малює нічого (`return null`), тобто майже
 * завжди його код (разом із `restoreEdits`) сторінці не потрібен.
 */
const loadRestoreEditsBanner = () => import('@/features/grid/RestoreEditsBanner');
const RestoreEditsBanner = lazy(async () => ({
  default: (await loadRestoreEditsBanner()).RestoreEditsBanner,
}));

/**
 * Сітка вантажиться окремим чанком.
 *
 * ⛔ Не оптимізація «про запас», а ліки, прописані самим гейтом бюджету:
 * `RevoGrid` — 79,1 % джерел чанка сторінки, і саме через нього маршрут
 * важив 259,2 КБ при межі 250 (`D-132`, `H-4`). Статичний імпорт означав,
 * що ядро сітки вантажить КОЖЕН, хто відкрив документ, — разом із тими,
 * хто дивиться його зведення і до таблиць не доходить.
 *
 * ⚠ Видимої затримки це не додає, і ось чому: сітка й до того не могла
 * намалюватися раніше за свій зріз даних — вона тягне його власним
 * запитом. Чанк вантажиться паралельно з тим самим очікуванням.
 *
 * ⛔ Чанк той самий, а от `React.lazy` і `<Suspense>` ПРИБРАНО — і це не
 * стиль, а єдине, що лікує зміряний дефект. Числа, спосіб вимірювання і
 * перебрані (та відкинуті) гіпотези — у `features/grid/SheetTables.tsx`;
 * коротко: під `<Suspense>` сітки не з'являлися НІКОЛИ на документі
 * чинного розміру, і жодне перекладання самих меж цього не міняло.
 *
 * ⚠ Динамічний `import()` в ефекті дає рівно ту саму окрему точку розбиття,
 * що й `lazy` (Vite/Rollup ріже чанк за виразом `import()`, а не за тим, хто
 * його обгортає), тому бюджет `D-132`/`H-4` лишається виконаним. Різниця в
 * тому, КОЛИ монтуються сітки: у звичайній фіксації після `setState`, а не
 * всередині повторної спроби межі очікування.
 */
// ✎ `UI-22`: чанк той самий, вхід — робоче місце аркуша (дерево таблиць + одна таблиця), яке
// всередині малює `SheetTables`.
type SheetTablesComponent = typeof import('@/features/grid/SheetWorkspace')['SheetWorkspace'];
type PendingHostComponent = typeof import('@/features/grid/DocumentPendingHost')['DocumentPendingHost'];

interface GridModuleState {
  readonly component: SheetTablesComponent | null;
  /** Хост незбережених правок (`D14-12`): приходить РАЗОМ із сіткою, монтується перед нею. */
  readonly pendingHost: PendingHostComponent | null;
  readonly error: unknown;
}

// Невиконаний запит переходу до комірки не переживає сторінку (ФВ-5.6).
function useClearCellNavigationOnUnmount(): void {
  useEffect(() => () => {
    void import('@/features/grid/cellNavigation').then((module) => { module.clearCellNavigation(); });
  }, []);
}

function useSheetTablesModule(): GridModuleState & { readonly reload: () => void } {
  const [state, setState] = useState<GridModuleState>({ component: null, pendingHost: null, error: null });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let alive = true;

    // ⚠ Компонент лежить у ПОЛІ об'єкта стану, а не в стані напряму: `useState`
    // трактує функцію як апдейтер, і компонент (він теж функція) інакше був би
    // ВИКЛИКАНИЙ замість того, щоб бути збереженим.
    void Promise.all([import('@/features/grid/SheetWorkspace'), import('@/features/grid/DocumentPendingHost')]).then(
      ([module, host]) => {
        if (alive) setState({ component: module.SheetWorkspace, pendingHost: host.DocumentPendingHost, error: null });
      },
      (error: unknown) => {
        // ⛔ Мовчазний провал тут коштував би дорожче за будь-який інший:
        // екран лишився б із заглушками назавжди, і це виглядало б рівно як
        // дефект, який ця картка й закриває.
        if (alive) setState({ component: null, pendingHost: null, error });
      },
    );

    return () => {
      alive = false;
    };
  }, [attempt]);

  return { ...state, reload: () => setAttempt((value) => value + 1) };
}

/**
 * Екран документа: вибір періоду, вкладки аркушів, таблиці, робочий процес.
 *
 * ⚠ Період — не фільтр показу, а **частина адреси даних**: екземпляри таблиць
 * і стан затвердження існують окремо на кожен період (R-A6). Тому зміна
 * періоду перечитує все, а не ховає рядки.
 *
 * ⛔ Таблиці беруться з `GET /api/v1/documents/{id}/tables`. До аудиту
 * (`A7-05`) екран чекав, що `GET /api/v1/documents/{id}` віддасть аркуші з
 * `tableInstanceId`, а той віддає `DocumentSummary` — бізнес-ключ і зведений
 * стан. Grid отримував `undefined` замість екземпляра таблиці.
 */
export function DocumentPage(): JSX.Element {
  const { id } = useParams();
  const documentId = Number(id);
  const session = useSession();

  // ⛔ Період і аркуш — в адресі (`ФВ-14.29`). Посилання на документ без них
  // відкриває інший період і інший аркуш, ніж той, про який ішлося.
  const [urlPeriod, setUrlPeriod] = useUrlNumber('periodKey');
  const [sheet, setSheet] = useUrlState('sheet');
  // `UI-22`: вибрана таблиця й режим показу читаються тут лише для заглушки чанка сітки.
  const [tableParam] = useUrlState('table');
  const [viewParam] = useUrlState('view');
  // ✎ D-6: місяць із посилання черги погодження — `?periodKey` річної сторінки нормалізується до якоря, тож місяць окремо.
  const [decideParam, setDecideParam] = useUrlNumber('decide');
  // ✎ Огляд M4 (4): місяць із посилання на річний документ (`?periodKey=202610`) — адреса нормалізується до якоря, а
  // місяць зберігається тут, щоб сітка перейшла до його колонки (одноразово, потім прибирається з адреси).
  const [focusMonth, setFocusMonth] = useUrlNumber('focusMonth');
  const setUrlParams = useUrlParamsSetter();
  const periodKey = urlPeriod ?? currentPeriodKey();
  // ✎ PERIODNAV: позицію прокрутки беремо ДО зміни адреси (див. `periodSwitchScroll`), а не з рендера нового періоду.
  const setPeriodKey = useCallback(
    (value: number | null) => {
      rememberScrollBeforePeriodSwitch(documentId);
      setUrlPeriod(value);
    },
    [documentId, setUrlPeriod],
  );

  const summary = useQuery({
    queryKey: ['document', documentId, periodKey],
    queryFn: () =>
      apiFetch<DocumentSummary>(`/api/v1/documents/${documentId}?periodKey=${periodKey}`),
  });

  const tables = useQuery({
    queryKey: ['document-tables', documentId, periodKey],
    queryFn: () =>
      apiFetch<DocumentTableDto[]>(
        `/api/v1/documents/${documentId}/tables?periodKey=${periodKey}`,
      ),
  });

  /*
   * ⛔ Останній результат перевірки ЧИТАЄТЬСЯ (директива №09 `W8` п.3,
   * `S-19`). Підсумок зберігається на сервері (`ФВ-5.19`), і доти його не
   * читав ніхто: перелік зауважень жив рівно до перезавантаження сторінки, а
   * щоб побачити його знову, оператор мусив ЗАПУСТИТИ перевірку заново — на
   * великому документі це три секунди й повний прогін правил заради списку,
   * який уже пораховано.
   *
   * ⚠ «Ще не перевіряли» — `validated: false` у відповіді (`X-32`; доти —
   * `404`), і в стані це `null`. «Зауважень немає» показувати замість цього не
   * можна — зелений напис під документом, якого ніхто не перевіряв,
   * повідомляє неправду про готовність.
   */
  /*
   * ✎ LAND-YEAR L7b: документ «рік» (`YearWithMonthColumns`) — Issues за ВСІ місяці року: `year=true` віддає останні
   * результати перевірки кожного місяця разом, кожне зауваження з `periodKey` прогону (сервер L6, дедуплікація там).
   * Доти Issues показував лише прогін якоря — зауваження, знайдені в жовтні, не було видно ніде. Ключ — під тим самим
   * префіксом `['validation', id, period]`, тож скидання після дій (дозвіл, перевірка) зачіпає обидва. Копія на місяць —
   * запит і ключ як були.
   */
  const yearDocument = isYearLayout(summary.data?.templateLayout);
  const lastValidation = useQuery({
    queryKey: yearDocument ? ['validation', documentId, periodKey, 'year'] : ['validation', documentId, periodKey],
    queryFn: () =>
      apiFetch<ValidationResultResponse>(
        `/api/v1/documents/${documentId}/validation?periodKey=${periodKey}${yearDocument ? '&year=true' : ''}`,
      ),
    retry: false,
  });

  /**
   * Адреса даних, до якої СТОСУЄТЬСЯ ручний прогін перевірки.
   *
   * ⚠ Період — не фільтр показу, а частина адреси (коментар компонента вище),
   * тож і результат перевірки адресний: «3 помилки» без цієї пари — це число
   * без предмета.
   */
  const scope = `${String(documentId)}:${String(periodKey)}`;

  /*
   * Свіжий прогін перекриває прочитаний: після натискання «Перевірити» на
   * екрані має бути те, що щойно порахували, а не те, що лежало в базі.
   *
   * ⛔ Аудит 2026-09-16 §10.7: тут лежав голий `ValidationResultResponse`, і
   * ніщо не скидало його при зміні документа/періоду — а показувався він
   * ПОПЕРЕД прочитаного (`fresh ?? lastValidation.data`). Оператор перевіряв
   * 202401, бачив «3 помилки», міняв період у заголовку — `summary`/`tables`/
   * `lastValidation` коректно перезапитувались, а «3 помилки» лишалися під
   * періодом, який НІХТО не перевіряв. Саме той випадок, проти якого
   * застерігає коментар до `lastValidation` вище, лише з протилежним знаком:
   * не зелений напис під неперевіреним, а червоний.
   *
   * ⚠ Результат зберігається РАЗОМ з адресою, а не скидається ефектом на
   * зміну `scope`. Різниця не стилістична: `scope` на момент ВІДПОВІДІ вже
   * може бути іншим, ніж на момент запиту (оператор змінив період, поки
   * перевірка йшла), і ефект-скидач тут не допоміг би — він відпрацював би
   * ДО того, як прийде відповідь, і та все одно лягла б на новий період.
   * Тому адресу несе сама мутація (її змінна), а показ порівнює її з поточною.
   */
  // ✎ UAT 08.11 (d4): `at` — коли прогін у цій вкладці завершився (сервер час збереженого результату не віддає).
  const [fresh, setFresh] = useState<{ scope: string; result: ValidationResultResponse; at: Date } | null>(
    null,
  );
  // `UI-25`: кожна завершена перевірка — сигнал інспектору відкрити Issues, якщо є що.
  const [validatedSeq, setValidatedSeq] = useState(0);
  const [headerOpenRequest, setHeaderOpenRequest] = useState(0);

  const validate = useMutation({
    // ⛔ N3-01: адреса (`scope`, `documentId`, `periodKey`) - ЗМІННА мутації, знята в мить кліку. Кнопка спершу чекає
    // збереження набраного (до 3 с); період із замикання виконувався б уже ІНШИЙ, якщо за цей час його змінили.
    mutationFn: (target: { scope: string; documentId: number; periodKey: number }) =>
      apiFetch<ValidationResultResponse>(
        `/api/v1/documents/${target.documentId}/validate`,
        {
          method: 'POST',
          // ⚠ Період — у ТІЛІ. До `A7-28` сервер читав його з рядка запиту, і
          // валідація мовчки йшла по періоду 0, відповідаючи «помилок немає».
          body: JSON.stringify({ periodKey: target.periodKey } satisfies DocumentPeriodRequest),
        },
      ),
    onSuccess: (result, target) => {
      setFresh({ scope: target.scope, result, at: new Date() });
      // ✎ LAND-YEAR L7b: документ «рік» — прогін пише результат ОДНОГО періоду (якоря), а Issues показує рік: спершу
      // перечитати річний результат (у ньому вже цей прогін), потім сигнал інспектору — інакше він рахував би старе.
      if (yearDocument) {
        void lastValidation.refetch().then(() => setValidatedSeq((value) => value + 1));
      } else {
        setValidatedSeq((value) => value + 1);
      }

      const errors = result.messages.filter((message) => message.severity === 'Error');

      // ⚠ Тост ЛИШАЄТЬСЯ, але тепер він лише повідомляє, що перевірка
      // завершилася: сам перелік — на екрані, під заголовком. Число без
      // переліку не веде до жодної дії (`ФВ-14.24`).
      notifications.show({
        color: errors.length === 0 ? 'green' : 'statusError',
        message:
          errors.length === 0
            ? t('document.validationClean')
            : t('document.validationErrors', { count: errors.length }),
      });
    },
    onError: showApiError,
  });
  // AN-28 P2-2: зайнятість і single-flight і на час збереження набраного.
  const validateAction = useSettledAction(validate.isPending);
  const validateLoading = usePendingLoading(validate.isPending);

  /**
   * Що показувати в панелі: свіже — **лише для своєї адреси** — інакше
   * прочитане, інакше нічого.
   */
  // ✎ F13 p223 (1): a permit change re-runs the stored check on the server and refetches it — a stored result read AFTER
  // this tab's own run is newer and wins (else Issues kept the undone change's PERMIT-SPLIT).
  // ✎ LAND-YEAR L7b: документ «рік» — свіжий прогін (лише якір) не підміняє річний перелік; його вже перечитано вище.
  const freshForScope =
    !yearDocument && fresh?.scope === scope && !(lastValidation.data?.validated === true && lastValidation.dataUpdatedAt > fresh.at.getTime())
      ? fresh.result
      : null;
  const shownValidation =
    freshForScope ?? (lastValidation.data?.validated === false ? null : (lastValidation.data ?? null));

  // ✎ F13-p210: попередження правила дозволу (рядкові) з ПОКАЗАНОГО результату — сіткам, щоб рядок їх показав;
  // той самий результат, що в Issues, без нового запиту.
  useEffect(() => {
    publishRowFindings(
      shownValidation === null
        ? null
        : { periodKey: shownValidation.periodKey, findings: permitRowWarningsOf(shownValidation.messages) },
    );
  }, [shownValidation]);
  useEffect(() => () => publishRowFindings(null), []);

  /*
   * ⛔ «Прочитати не вдалося» — це НЕ «ще не перевіряли», і до цього місця
   * обидва стани були для екрана одним і тим самим: `403`, `500` і обрив
   * мережі дають те саме
   * `data === undefined`, тобто `shownValidation === null`, тобто панель
   * зауважень не малювалася ЗОВСІМ. Документ, у якому на сервері вже лежать
   * БЛОКУВАЛЬНІ помилки, виглядав рівно як неперевірений і чистий: оператор
   * тиснув «Подати» й діставав відмову `ECR-SUB-*` без жодного попередження на
   * екрані — або вважав документ готовим.
   *
   * ⚠ Це той самий дефект, проти якого застерігає коментар до запиту вище
   * («зелений напис під документом, якого ніхто не перевіряв»), лише з
   * протилежним знаком: не зелений напис під неперевіреним, а порожнеча під
   * перевіреним. Стани тепер три, і в цьому порядку: відмова → очікування →
   * дані.
   *
   * ⚠ `404` лишається «ще не перевіряли» — саме так відповідає
   * `DocumentsController.LastValidation`, і банер під кожним документом, якого
   * ніхто не перевіряв, був би тим самим шумом, лише червоним.
   *
   * ⚠ Свіжий прогін ЦІЄЇ адреси знімає невідомість: результат уже на екрані,
   * і невдале читання того, що лежало в базі, більше нікого не вводить в
   * оману.
   *
   * ⛔ Саме `ErrorAlert`, а не `AsyncBoundary`: обгортка малює власний
   * `<Title order={4}>` посеред сторінки, у якої заголовок уже є, і рве
   * `heading-order` (гейти `a11y (dark)`/`a11y (light)`).
   */
  const unreadableValidation =
    freshForScope !== null || isNotValidatedYet(lastValidation.error)
      ? null
      : (lastValidation.error ?? null);

  /*
   * ⛔ `useMemo`, а не виклик на кожному рендері, — і це не мікрооптимізація.
   * `groupBySheet` будує НОВІ масиви щоразу, а `active.tables` іде пропом у
   * `SheetTables`, де від його ІДЕНТИЧНОСТІ залежить ефект, що заводить
   * `IntersectionObserver`. Нестабільний проп означав би, що спостерігача
   * знищують і створюють наново на кожен рендер сторінки (а їх тут кілька
   * поспіль: три запити приходять у різний час), — а перше повідомлення
   * спостерігача АСИНХРОННЕ, тож черга «створили → знищили → створили» здатна
   * не доставити його жодного разу. Сітки не з'явилися б зовсім, і виглядало б
   * це рівно як дефект, який закрив #295.
   *
   * ⚠ `tables.data` від React Query стабільний між рендерами, доки не прийшла
   * нова відповідь, — тобто мемоізація тут справді тримає ідентичність, а не
   * створює її видимість.
   */
  const sheets = useMemo(() => groupBySheet(tables.data ?? []), [tables.data]);
  // TC02: a mention link names a copy's table (`?table=КОД#sheetCopyId`) and carries no `?sheet=` (the sheet code of
  // the copy is not in the mention): without an explicit sheet that copy's tab opens.
  const linkedCopyId = sheet === null || sheet === '' ? parseChatKey(tableParam ?? '').sheetCopyId : null;
  const active =
    sheets.find((s) => s.key === sheet) ??
    (linkedCopyId === null ? undefined : sheets.find((s) => s.sheetCopyId === linkedCopyId)) ??
    sheets[0];
  const activeCode = active?.key;

  // AN-28 P2-1: дія заблокована утриманою (відхиленою) коміркою - показати її:
  // аркуш, прокрутка, фокус і підсвітка - тим самим шляхом, що й зауваження (`ФВ-5.6`).
  //
  // ⛔ N3-06: утримана правка ІНШОГО періоду раніше мовчки ігнорувалась - дія блокована, а
  // показати причину нікуди. Тепер період перемикається, а сама комірка показується, щойно
  // таблиці цього періоду прочитано (`heldToReveal`): `tables.data` до того - чужого періоду.
  const heldToReveal = useRef<HeldEdit | null>(null);

  // ✎ UAT 08.11 b2: той самий шлях відкриває рядок із банера «рядки без правила» (`columnCode = null` - перша колонка).
  const revealRow = useCallback(
    (tableInstanceId: number, rowKey: string, columnCode: string | null): void => {
      const target = tables.data?.find((table) => table.tableInstanceId === tableInstanceId);
      if (target === undefined) return;

      const targetKey = sheetTabKey(target.sheetCode, copyFieldsOf(target).sheetCopyId);
      if (targetKey !== activeCode) setSheet(targetKey);
      void import('@/features/grid/cellNavigation').then((module) =>
        module.requestCellNavigation({
          tableDefId: target.tableDefId,
          tableInstanceId: target.tableInstanceId,
          rowKey,
          columnCode,
        }),
      );
    },
    [tables.data, activeCode, setSheet],
  );
  const revealHeld = useCallback(
    (held: HeldEdit): void => revealRow(held.tableInstanceId, held.edit.rowKey, held.edit.columnCode),
    [revealRow],
  );
  const openUnmatchedRow = useCallback(
    // ✎ 10.11: відхилений правилом категорії рядок відкривається на колонці, яку треба заповнити.
    (tableInstanceId: number, rowKey: string, columnCode: string | null = null): void =>
      revealRow(tableInstanceId, rowKey, columnCode),
    [revealRow],
  );
  const navigate = useNavigate();

  useEffect(
    () =>
      registerHeldEditRevealer((held) => {
        if (held.periodKey !== periodKey) {
          heldToReveal.current = held;
          setPeriodKey(held.periodKey);

          return;
        }

        revealHeld(held);
      }),
    [periodKey, setPeriodKey, revealHeld],
  );

  useEffect(() => {
    const held = heldToReveal.current;
    if (held === null || held.periodKey !== periodKey || tables.data === undefined) return;

    heldToReveal.current = null;
    revealHeld(held);
  }, [periodKey, tables.data, revealHeld]);

  // ⚠ Стан береться з `SheetStates` документа за ключом аркуша (`КОД` для оригіналу, `КОД#sheetCopyId` для копії): скалярного
  // статусу документа не існує (D-93) — аркуші за один період бувають у
  // різних станах одночасно.
  const state =
    active === undefined || summary.data === undefined
      ? ''
      : stateOfGroup(active, summary.data.sheetStates);
  // ⛔ Редагованість — питання ОДНОГО стану аркуша, і відповідає на нього
  // таблиця переходів (`features/workflow/transitions.ts`), а не порівняння
  // рядків тут. Порівняння на місці — це друга копія машини станів домену, і
  // розійшлася б вона мовчки: `Rejected` виглядає як «не Draft», але
  // редагувати відхилений аркуш і треба, інакше виправити зауваження нічим.
  /*
   * ⛔ `F-18`: стан проєкту й періоду. Доти екран знав лише стан АРКУША — і в
   * закритому періоді чи архівному проєкті показував «Submit», «Import» і
   * «Recalculate» активними, без жодного банера, а сервер на кожну відмовляв.
   *
   * ⚠ Обидва запити — з тими самими ключами й адресами, що й перелік
   * документів (`DocumentsPage`: `['projects']`, `['periods', id]`), тож кеш
   * спільний, і перехід із переліку в документ нового запиту не робить.
   * Доки відповіді немає — причини немає: закрити дії через невідомість
   * означало б сховати їх на кожному відкритті на час запиту.
   */
  // ⛔ PERIODNAV: період поза проєктом (`ECR-PRD-0422`) лишає екран без `summary`, а отже без проєкту й
  // календаря - тупик. Проєкт беремо з картки БЕЗ періоду (`periodKey` необов'язковий), а на час перемикання
  // періоду тримаємо останній відомий (інакше календар зникає на кожному кроці).
  // ⚠ Сервер відхиляє період там, де вперше перевіряє його (`…/tables`, інколи й картка); `summary` при цьому може
  // бути вже прочитаним - тоді проєкт відомий і без запиту нижче.
  const periodRejected =
    (summary.data === undefined && isPeriodOutsideProject(summary.error)) ||
    (tables.data === undefined && isPeriodOutsideProject(tables.error));
  const baseSummary = useQuery({
    queryKey: ['document', documentId, 'base'],
    queryFn: () => apiFetch<DocumentSummary>(`/api/v1/documents/${documentId}`),
    enabled: periodRejected && summary.data === undefined,
  });
  const knownProject = useRef<{ documentId: number; projectId: number } | null>(null);
  const loadedProjectId = summary.data?.projectId ?? baseSummary.data?.projectId;
  if (loadedProjectId !== undefined) knownProject.current = { documentId, projectId: loadedProjectId };
  const projectId = knownProject.current?.documentId === documentId ? knownProject.current.projectId : null;

  // ✎ B78 F13 P3-2: «Відкрити HSE400» — документ року в проєкті з шаблоном HSE400 (спершу цей проєкт); немає — `false`
  // (панель так і каже), відмова API — виняток (панель показує помилку). За `import()` — бюджет маршруту D-132.
  // ✎ W15z M4: `target` — місяць панелі (документ «рік» має панель на кожен місяць); без нього — період сторінки.
  const openHse400 = useCallback(async (target: number = periodKey): Promise<boolean> => {
    const { findHse400Document } = await import('@/features/documents/findHse400Document');
    const found = await findHse400Document(projectId, Math.trunc(target / 100), documentId, target);
    if (found === null) return false;
    // ✎ Огляд b11 (G): місяць — явно (`focusMonth`): річний документ веде до колонки й тоді, коли місяць — якір.
    void navigate(`/documents/${String(found)}?periodKey=${String(target)}&focusMonth=${String(target % 100)}`);

    return true;
  }, [documentId, navigate, periodKey, projectId]);
  // ✎ B80 N4: «Відкрити HSE400» причини джерела — місяць документа-джерела, який назвав сервер.
  const openSource = useCallback(
    (sourceId: number, sourcePeriod: number) =>
      void navigate(`/documents/${String(sourceId)}?periodKey=${String(sourcePeriod)}&focusMonth=${String(sourcePeriod % 100)}`),
    [navigate],
  );

  const calendar = useQuery({
    queryKey: ['periods', projectId],
    queryFn: () => apiFetch<PeriodCalendarDto>(`/api/v1/projects/${String(projectId ?? 0)}/periods`),
    enabled: projectId !== null,
  });

  // ✎ D-474, п. 5: документ із розкладкою «рік, місяці в колонках» — одна копія на рік; без `?periodKey` він
  // відкривається на якорі року поточного періоду, а не на поточному місяці. ✎ LAND-YEAR L7a: явний `?periodKey`
  // місяця теж веде на якір (`yearAnchorOf` нижче); подання за місяцем — власний вибір цілі (`submission-target`).
  const yearLayout = isYearLayout((summary.data ?? baseSummary.data)?.templateLayout);
  useProjectCurrentPeriodDefault(
    urlPeriod,
    yearLayout ? yearAnchorCalendar(calendar.data?.periods) : calendar.data?.periods,
    setPeriodKey,
  );

  // PERIODNAV: допустимі періоди проєкту (за зростанням) - стрілки й сітка не виходять за них.
  const projectPeriods = useMemo(
    () => calendar.data?.periods?.map((period) => period.periodKey).sort((a, b) => a - b),
    [calendar.data],
  );
  // ✎ LAND-YEAR L7a: документ «рік» вибирає лише якорі років — місяць такого документа не окрема копія (п. 2 плану:
  // запасний вибір періоду теж без місяців).
  const pickablePeriods = yearLayout ? yearAnchors(projectPeriods) : projectPeriods;
  // ✎ LAND-YEAR L7a (координатор 17.11): місяці року документа «рік» для «Reopen … for this document» — з правом
  // `Period.Reopen` і лише коли в році є закритий місяць; інакше вибору немає (копія на місяць — банер, як було).
  // ✎ Огляд b11 (G): мемоізовано — від нього залежать запити місяців за стелею записів року.
  const yearMonths = useMemo(
    () =>
      yearLayout
        ? (calendar.data?.periods ?? [])
            .filter((period) => Math.floor(period.periodKey / 100) === Math.floor(periodKey / 100))
            .sort((a, b) => a.periodKey - b.periodKey)
        : [],
    [yearLayout, calendar.data, periodKey],
  );
  const yearReopenMonths =
    yearLayout && can(session.data, 'Period.Reopen') && yearMonths.some((period) => period.state === 'Closed')
      ? yearMonths
      : null;
  const lastValidPeriod = pickablePeriods?.[pickablePeriods.length - 1] ?? null;
  const nearestValid =
    periodRejected && projectPeriods !== undefined && !projectPeriods.includes(periodKey)
      ? nearestPeriod(projectPeriods, periodKey)
      : null;

  /*
   * ✎ LAND-YEAR L7a (п. 1 плану): документ «рік» — одна копія на рік, тож будь-який `?periodKey` місяця (смуга року в
   * переліку, посилання, закладка, згадка в чаті) веде на ЯКІР року (найменший місяць року в календарі, як на сервері).
   * Доти `?periodKey=202609` відкривав ту саму річну сітку «за вересень»: шапка, Issues, журнали й перевірка жили в
   * іншому періоді, ніж показувала людина сусідові, — і це був той самий перемикач місяця, лише через адресу.
   * ⚠ Календар ще не прийшов або місяців цього року в ньому немає — нікуди не ведемо (`yearAnchorOf` → `null`).
   * Копія на місяць — без змін.
   */
  const yearAnchor = yearLayout ? yearAnchorOf(projectPeriods, periodKey) : null;
  // ✎ Огляд M4 (4): явний місяць в адресі (не умовчання) — у `focusMonth`, тим самим переходом адреси.
  // ✎ Огляд b11 #3 (п. 5): звичайне відкриття якоря (`?periodKey=січень` — реєстр, дашборд, вибір періоду) НЕ стрибає;
  // до колонки січня веде лише явний `focusMonth` (посилання з панелі F5-R, «Open HSE400»).
  useEffect(() => {
    if (yearAnchor === null || yearAnchor === periodKey) return;
    if (urlPeriod === null) {
      setPeriodKey(yearAnchor);
      return;
    }
    setUrlParams({ periodKey: String(yearAnchor), focusMonth: String(urlPeriod % 100) });
  }, [yearAnchor, periodKey, setPeriodKey, urlPeriod, setUrlParams]);

  // Адреса з періодом поза проєктом (сервер його відхилив) сама веде на найближчий допустимий; кнопка в стані
  // помилки нижче - запасний шлях, якщо календар не завантажився.
  useEffect(() => {
    if (nearestValid !== null) setPeriodKey(nearestValid);
  }, [nearestValid, setPeriodKey]);

  // PERIODNAV: перемикання періоду міняє ключі запитів, і сторінка на мить стає скелетом (нижчим за документ) -
  // браузер притискає прокрутку, а ефекти монтування (фокус заголовка, вкладка аркуша) її зсувають. Пам'ятаємо
  // позицію на момент зміни періоду й повертаємо, щойно документ знову намальовано.
  const scrollAtSwitch = useRef<{ periodKey: number; y: number } | null>(null);
  const seenPeriod = useRef(periodKey);
  if (seenPeriod.current !== periodKey) {
    seenPeriod.current = periodKey;
    scrollAtSwitch.current = { periodKey, y: peekScrollBeforePeriodSwitch(documentId) ?? window.scrollY };
  }
  const periodSettled = summary.data !== undefined && tables.data !== undefined;
  // ✎ PERIODNAV: поки новий період вантажиться, `AsyncBoundary` заміняє ВЕСЬ документ скелетом, нижчим за вікно, - і
  // `scrollHeight` падає, а браузер притискає прокрутку до 0. Скелет тримає висоту останнього намальованого документа.
  const documentBox = useRef<HTMLDivElement>(null);
  const settledHeight = useRef(0);
  useLayoutEffect(() => {
    if (periodSettled && documentBox.current !== null) settledHeight.current = documentBox.current.offsetHeight;
  });
  useLayoutEffect(() => {
    const saved = scrollAtSwitch.current;
    if (saved === null || saved.periodKey !== periodKey || !periodSettled) return;
    scrollAtSwitch.current = null;
    clearScrollBeforePeriodSwitch();

    // Висота документа добирається ще якийсь час (слоти таблиць, зрізи сітки, смуга аркушів): утримуємо позицію, доки
    // людина сама не прокрутила (колесо, дотик, клавіша, клік) - її рух важливіший за збережену позицію.
    // ✎ Борги 27.11 (п. 8): за часом, а не 40 кадрів - див. `holdScrollPosition`.
    return holdScrollPosition(saved.y);
  }, [periodKey, periodSettled]);

  // ⛔ D1-02: дата чинності довідників у сітці — календарний кінець періоду ІЗ СЕРВЕРА.
  // `periodKey` для квартальних/річних проєктів — не `YYYYMM`, тож виводити дату з нього не можна.
  const periodEnd = calendar.data?.periods?.find((period) => period.periodKey === periodKey)?.periodEnd ?? null;

  // Приймальна 08.11 (i): закритий місяць, відкритий лише для цього документа, — Grace для нього (як на сервері).
  // ✎ D-474: розкладка «рік» — замок документа за річним агрегатом місяців (`AnnualPeriodRule`), а не за станом якоря;
  // колонки закритих місяців замикає сервер у `cellPermissions` зрізу.
  const periodReopen = useDocumentPeriodReopen({
    documentId,
    periodKey,
    periodState: yearLayout
      ? yearAggregatePeriodState(calendar.data?.periods, periodKey)
      : calendar.data?.periods?.find((period) => period.periodKey === periodKey)?.state,
    timeZoneId: calendar.data?.timeZoneId,
  });

  const lock = documentLockOf({
    // ⛔ PSS-19a: стан проєкту — з картки документа (`projectStatus`). Доти сторінка читала ВЕСЬ перелік проєктів (до
    // 20 сторінок по 500, послідовно) заради стану одного — замка дій в архівованому проєкті.
    projectStatus: (summary.data ?? baseSummary.data)?.projectStatus,
    periodState: periodReopen.periodState,
    sheetState: state,
  });

  /*
   * ✎ `UI-42`: на вузькому екрані (≤ 640 px, макет) документ — лише для читання з банером
   * (`DIRECTIVE-15-FRONTEND.md`: «Сітка документа на телефоні — читання, не редагування»,
   * `docs-narrow-note`). Набір у сітку пальцем на 500 px — шлях до помилкових чисел у звіті.
   * ⚠ Дії робочого процесу (подати, затвердити) лишаються: вони не вводять чисел, а
   * погоджувач із телефона — саме той, кому вузький екран і потрібен.
   */
  const narrow = useNarrowScreen();
  // ✎ D-474: розкладка «рік» — статус аркуша ЯКОРЯ (місяця, на якому відкрито рік) не замикає річний аркуш: сервер
  // судить місячну колонку m за статусом аркуша місяця m, а не-місячний ввід і рядки — за річним агрегатом статусів
  // (`AnnualPeriodRule.AggregateStatus`). Ці рішення приходять у `cellPermissions` зрізу; дії зі зміни даних вирішує
  // сервер так само. Копія на місяць — як було: стан переглянутого аркуша.
  const sheetLocksEdits = !yearLayout && !isEditable(state);
  const readOnly = sheetLocksEdits || locksDataActions(lock) || narrow;
  /*
   * ✎ D-442: значення ПОДАНОГО аркуша править погоджувач поточного кроку — це знає лише сервер, тож сітку
   * не замикаємо цілком: рішення зрізу дають погоджувачеві редаговні комірки, решті — `DocumentSubmitted`
   * (штриховка, замок, підказка, як і доти). Дії зі зміни даних (`readOnly` вище) лишаються закриті —
   * крім імпорту Excel погоджувачем (✎ D-445, `approverImport` у `DocumentActionBar`).
   *
   * ✎ борг D-445: «погоджувач ПОТОЧНОГО кроку» — рішення сервера (`GET …/approver-edit`, те саме правило, що й запис).
   * Доти погоджувач ІНШОГО кроку бачив «Імпорт…» і редаговну сітку, а сервер відмовляв. Поки відповіді немає —
   * лише читання: відкрити сітку через невідомість означало б показати дію, якої може не бути.
   */
  const deferredToServer = editsDeferToServer(state) && active !== undefined;
  const approverEdit = useApproverEditAvailability(documentId, active?.sheetDefId ?? 0, periodKey, deferredToServer, active?.sheetCopyId ?? null);
  const approverEditable =
    deferredToServer && approverEdit.data?.canApproverEdit === true && !locksDataActions(lock) && !narrow;
  const gridReadOnly = readOnly && !approverEditable;

  // ⚠ Викликається БЕЗУМОВНО і до будь-якого розгалуження показу: правило
  // хуків не знає про `AsyncBoundary` нижче.
  const gridModule = useSheetTablesModule();
  useClearCellNavigationOnUnmount();

  /*
   * ⛔ Незбережені правки належать ДОКУМЕНТУ, а не сітці (`D14-12`). Хук
   * відкриває сховище на цей документ, бере на себе збереження зрізів, чиї
   * сітки вже розмонтовані (перемикання аркуша, прокрутка — `SheetTables`), і
   * везе все незбережене перед закриттям вкладки. До нього кожна сітка робила
   * це за себе: правка молодша за 500 мс зникала при перемиканні аркуша
   * мовчки, а `beforeunload` віз лише ту сітку, у якій стояв курсор (`W-02`).
   *
   * ⚠ Хук тепер у `DocumentPendingHost` (лінивий чанк разом із сіткою, `useSheetTablesModule`): статичний
   * `autosave.ts` важив ≈4 КБ gzip і виводив маршрут за межу `D-132`. Хост монтується нижче, перед сіткою.
   */

  // Видалення чернетки: пункт — у меню «More», відмова сервера — банером під рядком дій.
  const deletion = useDeleteDocumentAction({
    documentId,
    document: summary.data,
    sheetCodes: sheets.map((s) => s.code),
    allowed: can(session.data, DeleteDocumentPermission),
  });

  // Зміна номера справи (бізнес-ключа, ФВ-3.9): пункт — у меню «More», `rekeyStale`
  // — банером під рядком дій; решта відмов лишається в самому діалозі (форма, яку
  // можна виправити).
  const businessKeyChange = useBusinessKeyChangeAction({
    documentId,
    document: summary.data,
    periodKey,
  });

  // Перенос на нову версію шаблону (ФВ-7.5): пункт — у меню «More», звіт сухого
  // прогону й відмови — у самому діалозі.
  const versionMigration = useVersionMigrationAction({ documentId, document: summary.data });

  // «Доступ до редагування» (EA-5a): пункт «More → Edit access» і лінива шторка передачі місяця.
  const editAssignments = useEditAssignmentsAction({
    documentId,
    document: summary.data,
    sheets,
  });

  // «History» і «Compare versions» — пункти «More» + лінивий діалог (макет: блоків між шапкою й сіткою немає).
  // ✎ LAND-YEAR L7b: документ «рік» — журнал переходів усіх місяців року, згрупований за місяцем.
  const documentLog = useDocumentLogActions({ documentId, periodKey, year: yearLayout });

  const staleReminderHost = useStaleResultsReminderHost(documentId);
  // ✎ ui-sheet-bottom (рішення людини 28.10): назва пункту «More» («Recalculate calculations») бере застарілість з
  // картки документа (поле resultsStale: сервер виводить її тим самим правилом, що й свіжість чисел, включно з
  // довідниками й звуженими читачами), БЕЗ запиту calculation-results при відкритті документа (на Land до 174 тис.
  // рядків). Окремий бейдж прибрано: банер під рядком дій каже те саме. Числа панель вантажить лише після розгортання.
  const calculationsStale = summary.data?.resultsStale === true;
  // ✎ D14 + AN-69 (UAT 08.11 b1): рядки без правила розрахунку - інформація під рядком дій, не помилка.
  const unmatchedRows = useUnmatchedRows(
    documentId,
    periodKey,
    active?.sheetCopyId ?? null,
    can(session.data, 'Calculation.View'),
  );
  // ✎ W15z M4 (P-1122-4): документ «рік» — проблеми ВСІХ місяців року (F5-R, StepError, помилки формул), панель на
  // кожен місяць із проблемами; запит лише на якорі (до переходу на якір — нічого). Копія на місяць — одна панель, як було.
  // ✎ Огляд M4 (1): якоря не знати (календар не прийшов чи відмовив) або запит року відмовив — панель поточного
  // періоду, як до M4 (проблеми не ховаються). Право — `Document.View`, як у сервера для calculation-issues; час прогону
  // (`Calculation.View`) рік не запитує.
  const scheduledMonths = useMemo(
    () => new Set((calendar.data?.periods ?? []).filter((period) => period.state === 'Scheduled').map((period) => period.periodKey)),
    [calendar.data],
  );
  // ✎ Огляд b11 (G): місяці року з календаря, крім Scheduled, — для окремого читання місяців за стелею записів року.
  const openYearMonths = useMemo(
    () => yearMonths.filter((period) => period.state !== 'Scheduled').map((period) => period.periodKey),
    [yearMonths],
  );
  const yearIssues = useYearUnmatchedRows(
    documentId,
    periodKey,
    active?.sheetCopyId ?? null,
    yearLayout && yearAnchor === periodKey,
    unmatchedRows.finishedAt,
    openYearMonths,
  );
  const issuePanels: readonly MonthUnmatchedRows[] =
    yearLayout && yearAnchor !== null && !yearIssues.failed
      ? // ✎ Огляд M4 (5): місяць Scheduled (ще не відкрито, D-575) — порожні комірки й жодних помилок.
        yearIssues.months.filter((month) => !scheduledMonths.has(month.periodKey))
      : hasCalculationIssues(unmatchedRows)
        ? [{ periodKey, rows: unmatchedRows }]
        : [];

  // ✎ Огляд M4 (4): посилання на місяць річного документа — сітка переходить до колонки цього місяця в першій таблиці
  // аркуша, що її має (місяці колонок — з `submission-target` аркуша, той самий запит, що в рядку дій), на перший рядок.
  const focusPending = yearLayout && focusMonth !== null && yearAnchor === periodKey && active !== undefined;
  const focusIssued = useRef<string | null>(null);
  const focusTarget = useQuery({
    queryKey: submissionTargetKey(documentId, active?.sheetDefId ?? 0, periodKey, active?.sheetCopyId ?? null),
    queryFn: () => fetchSubmissionTarget(documentId, active?.sheetDefId ?? 0, periodKey, active?.sheetCopyId ?? null),
    enabled: focusPending,
  });
  useEffect(() => {
    // ✎ Огляд b11 (G): `submission-target` відмовив — прибрати `focusMonth`, щоб він не спрацював пізніше (інший аркуш).
    if (focusPending && focusTarget.isError) {
      setFocusMonth(null);
      return;
    }
    if (!focusPending || active === undefined || focusTarget.data === undefined) return;
    // ✎ W15z M4 (репетиція): статуси `monthColumns` має лише МІСЯЧНИЙ календар — річний проєкт HSE400 (PeriodKind.Yearly)
    // їх не має (порожньо), тож місяць колонки — з `monthColumnCodes` (факт шаблону); старіший сервер — `monthColumns`.
    const months = focusTarget.data.monthColumnCodes ?? focusTarget.data.monthColumns;
    if (!Array.isArray(months)) return;
    setFocusMonth(null);
    // Одноразово: поки адреса ще несе `focusMonth` (оновлення не застосовано), ефект може спрацювати вдруге.
    const issued = `${String(documentId)}:${String(periodKey)}:${String(focusMonth)}`;
    if (focusIssued.current === issued) return;
    focusIssued.current = issued;
    const table = active.tables.find((candidate) =>
      months.some((month) => month.tableDefId === candidate.tableDefId && month.monthNumber === focusMonth),
    );
    const column = months.find((month) => month.tableDefId === table?.tableDefId && month.monthNumber === focusMonth);
    if (table === undefined || column === undefined) return;
    void import('@/features/grid/cellNavigation').then((module) =>
      module.requestCellNavigation({
        tableDefId: table.tableDefId,
        tableInstanceId: table.tableInstanceId,
        rowKey: module.FirstRowKey,
        columnCode: column.columnCode,
      }),
    );
  }, [focusPending, focusTarget.data, focusTarget.isError, active, focusMonth, setFocusMonth, documentId, periodKey]);
  // Новий запит переходу (інша адреса з `focusMonth`) — знову дозволено.
  useEffect(() => {
    if (focusMonth === null) focusIssued.current = null;
  }, [focusMonth]);
  // ✎ Огляд b11 (G): документ не «рік» — `focusMonth` нічого не значить, прибрати з адреси.
  const layoutKnown = (summary.data ?? baseSummary.data) !== undefined;
  useEffect(() => {
    if (layoutKnown && !yearLayout && focusMonth !== null) setFocusMonth(null);
  }, [layoutKnown, yearLayout, focusMonth, setFocusMonth]);

  /*
   * ✎ b14 documents-board-yearly (людина 25.11): «Sheet: Draft» без місяця не казав, ЧИЙ це стан — у документі «рік»
   * це був стан аркуша за ЯКІР (січень), хоч документ «Partly submitted», а вересень перевідкрито. Тепер чип каже період:
   * - «рік»: стан аркуша ЗА РІК — зведення станів місяців (`submission-target.monthColumns`, той самий запит і ключ, що в
   *   рядку дій) по розпочатих місяцях тим самим правилом, що й стан документа за рік у переліку (`yearStateOfPeriods`);
   *   місяців немає (річний проєкт без місячних колонок, збій, ще в дорозі) — стан якоря з назвою його місяця;
   * - копія на місяць: «September 2026», а місяць, відкритий лише для цього документа, — «· Reopened for this document».
   */
  const yearSheetTarget = useQuery({
    queryKey: submissionTargetKey(documentId, active?.sheetDefId ?? 0, periodKey, active?.sheetCopyId ?? null),
    queryFn: () => fetchSubmissionTarget(documentId, active?.sheetDefId ?? 0, periodKey, active?.sheetCopyId ?? null),
    enabled: yearLayout && active !== undefined && yearAnchor === periodKey,
  });
  const yearSheetMonths = useMemo(() => {
    const columns = yearLayout && Array.isArray(yearSheetTarget.data?.monthColumns) ? yearSheetTarget.data.monthColumns : [];
    const byKey = new Map<number, string>();
    for (const column of columns) if (!byKey.has(column.periodKey)) byKey.set(column.periodKey, column.sheetStatus);
    return [...byKey].map(([key, value]) => ({ periodKey: key, state: value }));
  }, [yearLayout, yearSheetTarget.data]);
  const startedYearMonths = useMemo(() => new Set(openYearMonths), [openYearMonths]);
  const sheetYearState =
    yearSheetMonths.length > 0 && calendar.data !== undefined ? yearStateOfPeriods(yearSheetMonths, startedYearMonths) : undefined;
  const periodReopenedForDocument =
    !yearLayout &&
    calendar.data?.periods?.find((period) => period.periodKey === periodKey)?.state === 'Closed' &&
    periodReopen.periodState === 'Grace';
  const sheetStateScope =
    sheetYearState !== undefined
      ? t('calcResults.yearChip', { year: Math.trunc(periodKey / 100) })
      : `${formatPeriodKey(periodKey, (summary.data ?? baseSummary.data)?.periodKind ?? undefined)}${
          periodReopenedForDocument ? ` · ${t('documents.strip.reopened')}` : ''
        }`;

  const refetchBoth = (): void => {
    void summary.refetch();
    void tables.refetch();
  };

  // PERIODNAV: період поза проєктом - не глухий кут: повідомлення сервера лишається, а поруч - перехід до
  // найближчого допустимого періоду й вибір періоду (лише з переліку проєкту).
  // ✎ LAND-YEAR L7a: документ «рік» — і кнопка переходу, і вибір ведуть на якір року, не на місяць.
  const recoveryTarget =
    (yearLayout && nearestValid !== null ? yearAnchorOf(projectPeriods, nearestValid) : nearestValid) ?? lastValidPeriod;
  const recovery =
    periodRejected && projectPeriods !== undefined && recoveryTarget !== null ? (
      <Stack data-testid="period-recovery">
        <ErrorAlert error={summary.error ?? tables.error} onRetry={refetchBoth} />
        <Group align="flex-end">
          <Button onClick={() => setPeriodKey(recoveryTarget)} data-testid="period-recovery-go">
            {t('document.periodGoTo', { period: formatPeriodKey(recoveryTarget) })}
          </Button>
          <Suspense fallback={periodPickerFallback}>
            <PeriodPicker size="xs" miw={110} value={periodKey} periods={pickablePeriods} onChange={(value) => setPeriodKey(value ?? periodKey)} />
          </Suspense>
        </Group>
      </Stack>
    ) : null;

  // ✎ DOCUX L7: смуга «Calculation: rows not included…» — у рядку повідомлень (пріоритет 6), а не окремим блоком.
  usePublishNotice(
    'calc-issues',
    issuePanels.length === 0
      ? null
      : {
          priority: 6,
          render: () => (
            <Suspense fallback={<div style={{ height: 32 }} aria-hidden="true" />}>
              <CalculationIssuesStrip
                userId={session.data?.userId}
                documentId={can(session.data, 'Calculation.View') ? documentId : undefined}
                memoryDocumentId={documentId}
                months={issuePanels}
                tables={tables.data ?? []}
                onOpenCell={openUnmatchedRow}
                onOpenHse400={openHse400}
                onOpenSource={openSource}
                narrow={narrow}
              />
            </Suspense>
          ),
        },
  );

  /*
   * D10 (a11y): документа немає (`404`, напр. `ECR-DOC-0404`) — службова сторінка з заголовком
   * сторінки, як `/404` (`NotFoundPage`), а не голий `ErrorAlert` без жодного heading. «Повторити»
   * тут не допоможе, тож замість нього — шлях назад до переліку документів.
   */
  const missingError = summary.data === undefined ? summary.error : null;
  if (missingError instanceof EcrApiError && missingError.problem.status === 404) {
    return <DocumentNotFound error={missingError} />;
  }

  // ⚠ Хост правок — на рівні сторінки, а не всередині `AsyncBoundary`/`recovery`: правки, утримані при зміні періоду,
  // переживають і відмову періоду, і перезапит (`DocumentPage.heldEditOtherPeriod`). Монтується перед сіткою.
  const PendingHost = gridModule.pendingHost;

  return (
    <>
    {PendingHost !== null && <PendingHost documentId={documentId} ownerUserId={session.data?.userId} />}
    {recovery ?? (
    /*
     * ⛔ Обгортка навколо ВСЬОГО екрана: заголовок — це бізнес-ключ документа,
     * тобто теж дані. Помилки обох запитів зведені разом, бо екран без
     * таблиць — це не документ, а його назва.
     *
     * ⚠ Документ БЕЗ аркушів за обраний період — окремий стан, не помилка:
     * період міг ще не відкритися. Порожні вкладки без пояснення виглядали б
     * як несправність (`A7-30` показав, що так і буває насправді).
     */
    <div ref={documentBox} style={periodSettled ? undefined : { minHeight: settledHeight.current }} data-document-box="">
    <AsyncBoundary<DocumentSummary>
      isPending={summary.isPending || tables.isPending}
      // AN-39/L8-11: збій ФОНОВОГО перезапиту (є `data`) не підміняє сторінку на помилку -
      // це розмонтувало б сітки, редактор, Undo і панель конфлікту; він іде банером нижче.
      error={
        (summary.data === undefined ? summary.error : null) ?? (tables.data === undefined ? tables.error : null)
      }
      data={summary.data}
      isEmpty={() => sheets.length === 0}
      emptyTitle={t('document.noSheets')}
      emptyHint={t('document.noSheetsHint')}
      skeleton="table"
      onRetry={refetchBoth}
    >
      {(document) => (
    <Stack>
      {(summary.error ?? tables.error) !== null && (
        <ErrorAlert error={summary.error ?? tables.error} onRetry={refetchBoth} />
      )}
      <PageHeader
        // ✎ b4b: «← Back to Documents» (KIT §1.5, макет `screen-document.js` `back`).
        back={{ label: t('nav.backToDocuments'), href: '/' }}
        // ⛔ Директива "людське ім'я документа": ім'я ПОРУЧ із бізнес-ключем,
        // а не замість нього — ключ лишається видимим завжди.
        title={
          localized(document.nameL10n).length > 0
            ? `${localized(document.nameL10n)} · ${document.businessKey}`
            : document.businessKey
        }
        // ⛔ У шапці — лише період: заголовок і поле періоду — один блок, а
        // дії — окремий рядок нижче (`DocumentToolbar`). Разом в одному
        // пласкому рядку висока колонка періоду й переноси по одній кнопці
        // давали «Delete document» самотою під полем періоду (знімок людини).
        actions={
          /* ⛔ UI-06: `NumberInput` → `PeriodPicker` (`DIRECTIVE-15-FRONTEND.md:129`).
              `value ?? periodKey` зберігає стару поведінку очищеного поля:
              воно НЕ звужувало документ до «без періоду» (тут період
              обов'язковий — `urlPeriod ?? currentPeriodKey()` нижче), а
              просто лишало те, що вже було.
              ✎ D-474, п. 5: розкладка «рік» — одна копія, перемикача місяця немає. */
          yearLayout ? (
            // CALC-RESULTS (макет, варіант A): «Year YYYY» замість перемикача місяця (L7a: меню «Calculated» прибрано).
            <Group gap="xs" wrap="nowrap">
              <YearLayoutChips periodKey={periodKey} />
              {/* ✎ b14 doc-notices-compact: відкриті для документа місяці року — чип із діапазонами (замість банера-списку). */}
              {(summary.data?.reopenedPeriods?.length ?? 0) > 0 && (
                <Suspense fallback={null}>
                  <ReopenedMonthsChip periods={summary.data?.reopenedPeriods ?? []} timeZoneId={calendar.data?.timeZoneId} />
                </Suspense>
              )}
              {/* ✎ LAND-YEAR L7a: відкрити закритий МІСЯЦЬ року для цього документа — вибір місяця замість ?periodKey. */}
              {yearReopenMonths !== null && (
                <Suspense fallback={null}>
                  <YearMonthReopen
                    documentId={documentId}
                    months={yearReopenMonths}
                    timeZoneId={calendar.data?.timeZoneId}
                    reopened={(summary.data?.reopenedPeriods ?? []).map((period) => period.periodKey)}
                  />
                </Suspense>
              )}
            </Group>
          ) : (
          <Suspense fallback={periodPickerFallback}>
            <PeriodPicker
              size="xs"
              miw={110}
              value={periodKey}
              periods={projectPeriods}
              onChange={(value) => setPeriodKey(value ?? periodKey)}
            />
          </Suspense>
          )
        }
      />

      {/* ✎ UI-14 (макет `screen-document.js`, `renderActions`): одна головна
          дія, ≤ 2 другорядні, решта — у меню «More»; ліворуч — стан словами.
          Діалоги рідкісних дій — поза меню: меню розмонтовує вміст, щойно
          закривається, тобто саме тоді, коли діалог мав би відкритися. */}
      {active !== undefined && (
        <Suspense fallback={<Skeleton h={36} mb="sm" data-document-action-bar-pending="" />}>
          <DocumentActionBar
            documentId={documentId}
            periodKey={periodKey}
            sheetDefId={active.sheetDefId}
            sheetCode={active.code}
            sheetCopyId={active.sheetCopyId}
            sheetName={active.name}
            state={state}
            lock={lock}
            readOnly={readOnly}
            // ✎ UAT-recalc-ux, F13 p218 #788: позначка «перераховано після подання» — за ключем стану вкладки (копія — свій ключ).
            recalculatedAfterSubmission={recalculatedAfterSubmissionOf(active, summary.data?.sheets ?? [])}
            language={session.data?.language ?? 'en'}
            // ⛔ Імпорт лише туди, куди можна писати (`ФВ-5.20a`). ✎ D-445: поданий аркуш імпортує погоджувач
            // поточного кроку — як править його в сітці; хто саме — вирішує сервер (перегляд покаже відмову решті).
            canImport={can(session.data, 'Document.Import')}
            approverImport={approverEditable}
            canExport={can(session.data, 'Document.Export')}
            validate={{
              loading: validateLoading || validateAction.settling,
              run: () => validateAction.run(() => validate.mutateAsync({ scope, documentId, periodKey }), { readOnly: true }),
            }}
            calculationsStale={calculationsStale}
            documentItems={[...documentLog.menuItems, businessKeyChange.menuItem, versionMigration.menuItem, editAssignments.menuItem, deletion.menuItem]}
            resultsStale={document.resultsStale ?? null}
            resultsStaleSince={document.resultsStaleSince ?? null}
            // ✎ DOCUX: проблеми розрахунку в рядку повідомлень — чип розрахунку каже те саме («with issues»).
            calcIssues={issuePanels.some((month) => hasCalculationProblems(month.rows))}
            staleReminderHost={staleReminderHost}
            periodAction={periodReopen.lockAction}
            yearLayout={yearLayout}
            decideMonth={decideParam}
            // ✎ D-6 (п. 9): `?decide` діє один раз — застосовано (чи аркуш змінився) — прибрати з адреси.
            onDecideConsumed={() => setDecideParam(null)}
            status={
              <>
                {/* ✎ UI-15: чип стану аркуша і заповненість одним рядком
                    (макет `renderState`/`renderProgress`). */}
                <DocumentProgress
                  documentId={documentId}
                  periodKey={periodKey}
                  sheets={sheetItemsOf(sheets, document.sheetStates)}
                  activeCode={active.key}
                  // ✎ UI-25 (інспектор): «K issues» — кнопка інспектора нижче, лічильник
                  // за ВИДИМИМИ таблицями (`inspectorModel.ts`); тут другого немає.
                  issues={null}
                  onShowIssues={() => undefined}
                  // ✎ B143 (B75): шаблон без подання й затвердження — «No approval» замість стану аркуша.
                  workflowOff={document.workflowMode === 'None'}
                  stateScope={sheetStateScope}
                  stateOverride={sheetYearState}
                />
                <DocumentSaveState readOnly={gridReadOnly} periodKey={periodKey} />
                {/* ✎ DOCUX L8 (№9): копія на місяць — «Reopened until …» чипом у рядку стану (рік — `ReopenedMonthsChip` у шапці). */}
                {yearLayout ? null : periodReopen.banner}
                {/* PM-11 → ✎ DOCUX L8 (№11): «з дозволу» — чип у рядку стану (одна таблиця — чип і «Refresh»; кілька — перелік
                    у спливаючому вікні). Чанк вантажиться лише коли така таблиця є; додаткових запитів немає. */}
                {active.tables.some((table) => table.permitBinding != null) && (
                  <Suspense fallback={null}>
                    <PermitRefreshStrip documentId={documentId} periodKey={periodKey} tables={active.tables} readOnly={readOnly} />
                  </Suspense>
                )}
                {/* ✎ D-446: застарілість не визначена — частину документа читачеві закрито (те саме, що в рядку переліку). */}
                {document.resultsStaleUndetermined === 'PartiallyHidden' && <StaleResultsUndeterminedMark />}
              </>
            }
          />
        </Suspense>
      )}

      {/* ✎ B78: помилки формул (C-1, блокують подання) і рядки поза розрахунком — рядок на причину.
          ✎ b14 doc-notices-compact: усі місяці (W15z M4: «рік» — кожен місяць із проблемами) — ОДНА смуга в рядок,
          згорнута; «Details» — матриця «причина × місяць», панелі місяців — у її «Technical details». Поки чанк
          вантажиться, місце тримає коробка висоти смуги (сітка не стрибає). */}
      {/* ✎ DOCUX L7: над сіткою — ОДИН рядок повідомлень (найважливіше + «+N more» → вкладка Status інспектора). Смуга
          проблем розрахунку — повідомлення пріоритету 6 (`calcIssuesNotice` вище). */}
      <NoticeLine />

      {/* ✎ DOCUX Q5 (рішення людини 26.11): смуги «перерахунок проєкту» тут немає — вона на дошці документів; стан ЦЬОГО
          документа каже чип розрахунку в рядку стану. */}

      {/* ⛔ `F-18`: ЧОМУ тут нічого не змінити — одразу під рядком дій, до
          будь-якої сітки: сірі комірки без пояснення читаються як збій.
          ✎ UI-26: за наявного аркуша банер малює панель дій (з контекстом). */}
      {active === undefined && (
        <Suspense fallback={null}>
          <DocumentLockBanner lock={lock} periodKey={periodKey} periodAction={periodReopen.lockAction} yearLayout={yearLayout} />
        </Suspense>
      )}

      {/* ✎ L7c → b14 doc-notices-compact: «рік» — відкриті місяці року лише чипом у шапці (банер-список прибрано);
          копія на місяць — банер свого місяця, як було (у ньому причина й хто відкрив). */}
      {periodReopen.dialog}

      {/* EA-5b: передача місяця - «Місяць передано вам», «Заповнено», коментар повернення. Права комірок рішає сервер. */}
      <Suspense fallback={null}>
        <ExecutorStrip documentId={documentId} periodKey={periodKey} />
      </Suspense>


      {/* `UI-42`: чому тут нічого не змінити на телефоні — тим самим місцем, що й інші блокування.
          Аркуш і так закритий (банер вище) — другий банер не потрібен. */}
      {narrow && lock === null && isEditable(state) && (
        // ⚠ `Alert`, а не `shared/ui/Banner`: той самий вигляд тону `info` (`brand`, light), але без
        // зайвого модуля в бюджеті маршруту (`D-132`), як і `DocumentLockBanner` поруч.
        <Alert color="brand" variant="light" title={t('document.narrow.title')} data-testid="docs-narrow-note">
          {t('document.narrow.text')}
        </Alert>
      )}

      {businessKeyChange.dialog}

      {versionMigration.dialog}

      {editAssignments.drawer}

      {deletion.dialog}

      {businessKeyChange.refusal}

      {deletion.refusal}

      {/* ⛔ `ФВ-3.6`, `D14-12` крок 3: правки, що загинули з сесією, лежать у
          вкладці (`features/grid/lostEdits.ts`) і чекають ТУТ — раніше їх
          нікуди було покласти. Банер над сітками, а не під ними: пропозиція,
          яку видно лише після прокрутки до дев'яносто першої таблиці, — це
          пропозиція, якої немає.

          ⚠ Статичний імпорт бюджету чанка не чіпає (`D-132`): компонент
          працює зі сховищем правок і зрізом, ядра `RevoGrid` не торкаючись. */}
      <Suspense fallback={null}>
        <RestoreEditsBanner documentId={documentId} userId={session.data?.userId} />
      </Suspense>

      {/* ⛔ Шапка документа: поля версії шаблону з поточними значеннями.
          Компонент сам вирішує, чи малюватися — порожній перелік полів
          означає «у цього документа шапки немає», не помилку (контракт
          `GET …/header`). Право редагування — той самий грант `Write` на
          проєкт, що й `PATCH …/cells` (`hasProjectWriteGrant`,
          `BusinessKeyChangeAction.tsx`), без окремого функціонального права. */}
      <Suspense fallback={null}>
        <HeaderSplitBanner
          documentId={documentId}
          periodKey={periodKey}
          tables={tables.data}
          readOnly={readOnly}
          onReveal={openUnmatchedRow}
        />
      </Suspense>

      <Suspense fallback={null}>
        <DocumentHeaderPanel
          documentId={documentId}
          periodKey={periodKey}
          businessKey={document.businessKey}
          templateVersion={document.templateVersion ?? undefined}
          templateCode={document.templateCode ?? undefined}
          showRequiredErrors={validatedSeq > 0}
          // ✎ UI-16: згорнута з підсумком; розгортається сама, коли поле
          // потребує уваги (обов'язкове порожнє, недійсна дата, незбережене).
          collapsible
          openRequest={headerOpenRequest}
          // AN-39/L8-13: сервер править шапку за `EditRules.CanEdit` документа цілком - не в
          // симуляції, не в архівному проєкті, не за поданого/затвердженого аркуша.
          canEdit={
            hasProjectWriteGrant(session.data, document.projectId) &&
            session.data?.isSimulation !== true &&
            lock !== 'projectArchived' &&
            !hasLockedSheet(document.sheetStates) &&
            !narrow
          }
        />
      </Suspense>

      {/* ✎ UI-15: `SheetFillSummary` (`BE-10`) — тепер у рядку прогресу
          шапки (`DocumentProgress`), поруч зі смужкою аркушів і числом
          зауважень: усі три кажуть про документ за період цілком. */}

      {/* ⚠ Причина стоїть РІВНО ТАМ, де мала б стояти панель зауважень, і
          перед нею: «зауваження прочитати не вдалося» — твердження про той
          самий предмет, і побачити його має той, хто прийшов дивитися на
          зауваження, а не той, хто відкрив консоль.

          ⚠ Панель нижче лишається БЕЗУМОВНОЮ: якщо відмовив лише ПОВТОРНИЙ
          запит, прочитане раніше нікуди не поділося (React Query тримає
          `data`), і ховати його заради банера означало б втратити відомі
          зауваження. Тоді на екрані обидва — «ось що ми знали» і «оновити не
          вдалося», а не одне замість одного. */}
      {unreadableValidation !== null && (
        <ErrorAlert
          error={unreadableValidation}
          onRetry={() => {
            void lastValidation.refetch();
          }}
        />
      )}

      {/* ⚠ Панель — ПІД заголовком і НАД вкладками: зауваження стосуються
          документа за період цілком, а не активного аркуша, і сховати їх під
          вкладку означало б показувати їх лише тому, хто вгадав, куди
          дивитися.

          ⚠ Доступність кнопки «Подати» від цього стану НЕ залежить і не
          залежала: її показ рахує `SheetActions` за станом аркуша й рівнем
          гранта (`features/workflow/SheetActions.tsx`), валідації він не
          питає. Тобто невідомість кнопки не ВІДКРИВАЄ — вона лише лишала
          оператора без єдиного попередження перед натисканням; банер вище це
          й закриває. Гейт подання за помилками — на сервері (`ECR-SUB-*`). */}
      {/* `UI-25`: зауваження — в інспекторі справа (закритий за замовчуванням);
          тут лише кнопки «K issues» і «History». Лічильник — лише за ВИДИМИМИ
          таблицями (`inspectorModel.ts`). */}
      <Suspense fallback={null}>
        <DocumentInspector
          documentId={documentId}
          periodKey={periodKey}
          yearLayout={yearLayout}
          tables={tables.data ?? []}
          messages={shownValidation?.messages ?? null}
          checkedAt={validationCheckedAt(!yearDocument && fresh?.scope === scope ? fresh : null, lastValidation.data)}
          validatedSeq={validatedSeq}
          // ✎ DOCUX (огляд UI 27.11): Issues називає проблеми розрахунку з рядка повідомлень і веде у Status.
          calcIssues={issuePanels}
          extraTriggers={
            <Suspense fallback={null}>
              <TableChatButton documentId={documentId} />
            </Suspense>
          }
          sheet={active === undefined ? null : { sheetDefId: active.sheetDefId, code: active.code, name: active.name }}
          onOpenCell={(target) => {
            // Журнал аркуша веде в комірку ТІЄЇ Ж таблиці активного аркуша: той самий шлях, що й зауваження.
            const table = tables.data?.find(
              (candidate) => candidate.sheetCode === target.sheetCode && candidate.tableCode === target.tableCode,
            );
            if (table === undefined) return;

            void import('@/features/grid/cellNavigation').then((module) =>
              module.requestCellNavigation({
                tableDefId: table.tableDefId,
                tableInstanceId: table.tableInstanceId,
                rowKey: target.rowKey,
                columnCode: target.columnCode,
              }),
            );
          }}
          onSelectFinding={(finding) => {
            // Знахідка шапки (`tableDefId` 0) веде до панелі шапки, а не в клітинку.
            if (isHeaderFinding(finding)) {
              setHeaderOpenRequest((value) => value + 1);
              return;
            }
            // ⛔ `ФВ-5.6`: спершу аркуш зауваження, потім запит переходу. Модуль
            // переходу — за `import()`: він живе в чанку сітки, не сторінки
            // (`D-132`), і сітки однаково без нього не з'являться.
            const target = tables.data?.find((table) => table.tableDefId === finding.tableDefId);
            if (target === undefined) return;

            // ⚠ Лише коли аркуш інший: зміна адреси — це навігація, і на
            // активному аркуші вона нічого не дає, крім зайвого рендеру сторінки.
            const targetKey = sheetTabKey(target.sheetCode, copyFieldsOf(target).sheetCopyId);
            if (targetKey !== active?.key) setSheet(targetKey);
            void import('@/features/grid/cellNavigation').then((module) =>
              module.requestCellNavigation(finding),
            );
          }}
        />
      </Suspense>

      <Suspense fallback={null}>
        <TableChatHost
          documentId={documentId}
          periodKey={periodKey}
          yearLayout={yearLayout}
          tables={tables.data ?? []}
          activeTables={active?.tables ?? []}
        />
      </Suspense>

      {documentLog.dialog}

      {/* ✎ b4b: смуга аркушів — ПІД сіткою, як у макеті (`screen-document.js`
          `.statusbar` > `.sheet-tabs`; KIT §1.5), а не вкладками над нею. Список
          стоїть після панелей і липне до низу вікна (`documentSheetStrip.css`).
          ⛔ Аркуші — лише ті, що повернув API (видимі цьому користувачу, P1). */}
      {/* Чанк вантажиться лише для аркуша, що копіюється, або копії. */}
      {active !== undefined && (active.isCopyable || active.sheetCopyId !== null) && (
        <Suspense fallback={null}>
          <SheetCopyBar
            documentId={documentId}
            target={{
              sheetDefId: active.sheetDefId,
              sheetCode: active.code,
              label: active.name,
              groupNameValues: active.groupNameValues,
              sheetCopyId: active.sheetCopyId,
              isCopyable: active.isCopyable,
              chatMessageCount: chatMessageCountOf(active),
            }}
            canAdd={!readOnly}
            onSelect={setSheet}
          />
        </Suspense>
      )}

      <Tabs value={active?.key ?? null} onChange={setSheet} inverted>

        {/* ⛔ Кожна вкладка має СВОЮ панель: Mantine ставить вкладці
            `aria-controls` на id панелі, і без панелі посилання висіло в
            повітрі (axe `aria-valid-attr-value`, critical; спіймано, щойно
            гейт почав чекати даних). Неактивні — порожні: `keepMounted` у
            `Tabs` не ввімкнено, тож вміст вони не рендерять.

            ⚠ Активна панель — ОКРЕМИЙ елемент на сталому місці дерева, а не
            елемент того ж списку з ключем аркуша: інакше перемикання аркуша
            перемонтовувало б сітку (важкий RevoGrid і стан лінивого
            монтажу). Так, як і раніше, сітка лише отримує нові `tables`. */}
        {sheets
          .filter((s) => s.key !== active?.key)
          .map((s) => (
            <Tabs.Panel key={s.key} value={s.key}>
              {null}
            </Tabs.Panel>
          ))}

        {/* ⚠ `pt="md"` і `Stack` відтворюють відступи, які раніше давав
            зовнішній `Stack` сторінки між вкладками й сіткою. */}
        {active !== undefined && (
          <Tabs.Panel value={active.key} pt="md">
            <Stack>
              {/* ⚠ Три стани чанка сітки замість `<Suspense>`: вантажиться —
                  заглушка ПО ОДНІЙ НА ТАБЛИЦЮ (кількість таблиць відома до
                  завантаження чанка, і одна смужка замість дев'яноста однієї
                  збрехала б про розмір сторінки, яка зараз з'явиться); не
                  завантажився — помилка з кнопкою «повторити», бо порожній
                  екран із заглушками тут не відрізнити від того самого
                  дефекту, який ця картка закриває; завантажився — таблиці. */}
              {gridModule.error !== null && (
                <ErrorAlert error={gridModule.error} onRetry={gridModule.reload} />
              )}

              {/* ⚠ Заглушка чанка повторює розкладку `SheetTables`: заголовок
                  таблиці ПЛЮС смужка зарезервованої висоти. Обидва — не
                  прикраса. Заголовок робить осмисленою прокрутку по ще не
                  завантаженій сторінці (і з'являється він одразу, а не після
                  чанка сітки), а висота слота мусить збігатися з тією, яку
                  тримає `SheetTables` (`TableSlotMinHeight`), інакше поява
                  чанка перекладає всю сторінку під курсором. Числове значення
                  тут не імпортується з модуля сітки навмисно: це той самий
                  модуль, який ця гілка й чекає, і статичний імпорт із нього
                  повернув би `RevoGrid` у чанк маршруту (`D-132`). */}
              {gridModule.error === null && gridModule.component === null && (
                <Stack gap="xs">
                  {/* ⚠ `UI-22`: у режимі «одна таблиця» заглушка — теж одна. */}
                  {(viewParam === 'all'
                    ? active.tables
                    : // ⚠ Спрощений `resolveTable` (`tableTreeModel.ts`) інлайном: модуль дерева живе в
                      // чанку сітки, статичний імпорт додав би сторінці окремий чанк (`D-132`).
                      [active.tables.find((table) => table.tableCode === tableParam) ?? active.tables[0]].filter(
                        (table) => table !== undefined,
                      )
                  ).map((table) => (
                    <Stack
                      key={table.tableInstanceId}
                      gap="xs"
                      style={{ minHeight: 'calc(70vh + 96px)' }}
                    >
                      <Text fw={600}>{localized(table.tableNameL10n)}</Text>
                      <Skeleton height="60vh" radius="sm" />
                    </Stack>
                  ))}
                </Stack>
              )}

              {gridModule.component !== null && (
                <gridModule.component
                  documentId={documentId}
                  periodKey={periodKey}
                  periodEnd={periodEnd}
                  readOnly={gridReadOnly}
                  sheetState={state}
                  tables={active.tables}
                  tableBarEnd={
                    can(session.data, 'Calculation.View') ? (
                      <AllMonthsLink documentId={documentId} periodKey={periodKey} yearLayout={yearLayout} />
                    ) : undefined
                  }
                  // ✎ ui-sheet-bottom: смуга аркушів — у нижньому рядку панелі таблиці, поруч із її прокруткою.
                  bottomBar={(scroll) => (
                    <SheetStrip sheets={sheetItemsOf(sheets, document.sheetStates)} activeCode={active.key} onSelect={setSheet}>
                      {scroll}
                    </SheetStrip>
                  )}
                />
              )}
            </Stack>
          </Tabs.Panel>
        )}

        {/* Поки чанк сітки вантажиться (або аркуша немає) — смуга окремо, як раніше. */}
        {(gridModule.component === null || active === undefined) && (
          <SheetStrip
            sheets={sheetItemsOf(sheets, document.sheetStates)}
            activeCode={active?.key}
            onSelect={setSheet}
          />
        )}
      </Tabs>

      {/* ⚠ Без аркушів панелі немає, а відмова чанка має лишатися видимою —
          як і до появи панелі. Заглушка й сітка без аркуша не малювалися й
          раніше. */}
      {active === undefined && gridModule.error !== null && (
        <ErrorAlert error={gridModule.error} onRetry={gridModule.reload} />
      )}

      {/* ⛔ Числа методологій — окремо від сітки, і це `D-69`: у комірку
          вони не потрапляють ніколи, а приходять у документ посиланням через
          прив'язку. Доти це число не показував жоден екран.

          ⚠ Під правом `Calculation.View`, як і сусідні дії вище. Панель
          стояла БЕЗУМОВНО, і оператор без цього права бачив на ВЛАСНОМУ
          документі плашку `403 Calculation.View` — там, де решта недоступного
          просто не малюється. Знайдено проходом інтерфейсу як користувач
          (`docs/build/UI-WALKTHROUGH.md`, F2): жоден компонентний тест цього
          не бачив, бо кожен із них монтує панель напряму. */}
      {/* ✎ LAND-YEAR L7a (п. 9 плану): документ «рік» — числа методологій УЖЕ в місячних колонках аркуша (накладення
          за власником, L2b), а панель показувала б прогін одного якоря під виглядом «результатів документа». Усі місяці —
          «All months» у смузі таблиці (сторінка результатів за рік лишається). Копія на місяць — панель як була. */}
      {can(session.data, 'Calculation.View') && !yearLayout && (
        <Suspense fallback={null}>
          <CalculationResultsPanel documentId={documentId} periodKey={periodKey} />
        </Suspense>
      )}
    </Stack>
      )}
    </AsyncBoundary>
    </div>
    )}
    </>
  );
}

/**
 * Чи означає відмова читання «перевірку ще не запускали».
 *
 * ⛔ Рівно `404` і рівно від НАШОГО API. Усе інше — `403` (права на читання
 * підсумку), `500`, обрив мережі, відповідь проксі — це «невідомо», а
 * «невідомо», показане як «нічого», і є та сама неправда про готовність
 * документа, проти якої написаний коментар до запиту.
 */
function isNotValidatedYet(error: unknown): boolean {
  return error instanceof EcrApiError && error.problem.status === 404;
}

/**
 * Поточний період як `Year*100 + Sequence` (R-A6).
 *
 * ⚠ Це лише **початкове значення поля**, а не бізнес-правило: справжній
 * поточний період задає календар проєкту, і в квартальному проєкті номер
 * місяця йому не дорівнює. Користувач бачить число і може його змінити.
 */
function currentPeriodKey(): number {
  const now = new Date();

  return now.getUTCFullYear() * 100 + (now.getUTCMonth() + 1);
}

/**
 * Документа немає (D10, a11y): службова сторінка `ServiceStatePage` — той самий вигляд, фокус на
 * заголовку й оголошення, що в `/404`. Назва — з каталогу за кодом відмови (`problemText`), код і
 * кореляція — для підтримки (як в `ErrorAlert`), дія — назад до переліку документів.
 */
function DocumentNotFound({ error }: { readonly error: EcrApiError }): JSX.Element {
  const shown = problemText(error);

  return (
    <Suspense fallback={null}>
      <ServiceStatePage icon="route" title={shown.title}>
        {shown.detail !== null && (
          <Text size="sm" c="dimmed" ta="center">
            {shown.detail}
          </Text>
        )}
        <Text size="xs">
          <Code>{error.problem.errorCode}</Code> <Code>{error.problem.correlationId}</Code>
        </Text>
        <Button component={Link} to="/">
          {t('nav.goToDocuments')}
        </Button>
      </ServiceStatePage>
    </Suspense>
  );
}

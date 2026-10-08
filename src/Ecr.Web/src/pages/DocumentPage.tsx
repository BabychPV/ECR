import { lazy, Suspense, useEffect, useMemo, useState, type JSX } from 'react';
import { Alert, Badge, Skeleton, Stack, Tabs, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useParams } from 'react-router-dom';
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
import { DocumentActionBar } from '@/features/documents/DocumentActionBar';
import { DocumentSaveState } from '@/features/documents/DocumentSaveState';
import { useDocumentLogActions } from '@/features/documents/DocumentLogActions';
import { useVersionMigrationAction } from '@/features/documents/VersionMigrationAction';
import { DocumentLockBanner } from '@/features/documents/DocumentLockBanner';
import { documentLockOf, hasLockedSheet, locksDataActions } from '@/features/documents/documentLock';
import { DocumentProgress } from '@/features/documents/DocumentProgress';
import { useDocumentPending } from '@/features/grid/autosave';
import { isEditable } from '@/features/workflow/SheetActions';
import { useCalculationsStale } from '@/features/methodologies/staleCalculations';
import { can, useSession } from '@/shared/session/useSession';
import { localized } from '@/shared/i18n/localized';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { useNarrowScreen } from '@/shared/narrowScreen';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showApiError } from '@/shared/ui/notify';
import { PageHeader } from '@/shared/ui/PageHeader';
import { useProjectCurrentPeriodDefault } from '@/features/documents/useProjectCurrentPeriodDefault';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import '@/features/documents/documentSheetStrip.css';
import { useUrlNumber, useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';
import { registerHeldEditRevealer, useSettledAction } from '@/features/grid/settleEdits';

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

const loadCalculationResultsPanel = () => import('@/features/methodologies/CalculationResultsPanel');
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

interface GridModuleState {
  readonly component: SheetTablesComponent | null;
  readonly error: unknown;
}

// Невиконаний запит переходу до комірки не переживає сторінку (ФВ-5.6).
function useClearCellNavigationOnUnmount(): void {
  useEffect(() => () => {
    void import('@/features/grid/cellNavigation').then((module) => { module.clearCellNavigation(); });
  }, []);
}

function useSheetTablesModule(): GridModuleState & { readonly reload: () => void } {
  const [state, setState] = useState<GridModuleState>({ component: null, error: null });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let alive = true;

    // ⚠ Компонент лежить у ПОЛІ об'єкта стану, а не в стані напряму: `useState`
    // трактує функцію як апдейтер, і компонент (він теж функція) інакше був би
    // ВИКЛИКАНИЙ замість того, щоб бути збереженим.
    void import('@/features/grid/SheetWorkspace').then(
      (module) => {
        if (alive) setState({ component: module.SheetWorkspace, error: null });
      },
      (error: unknown) => {
        // ⛔ Мовчазний провал тут коштував би дорожче за будь-який інший:
        // екран лишився б із заглушками назавжди, і це виглядало б рівно як
        // дефект, який ця картка й закриває.
        if (alive) setState({ component: null, error });
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
  const periodKey = urlPeriod ?? currentPeriodKey();
  const setPeriodKey = setUrlPeriod;

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
  const lastValidation = useQuery({
    queryKey: ['validation', documentId, periodKey],
    queryFn: () =>
      apiFetch<ValidationResultResponse>(
        `/api/v1/documents/${documentId}/validation?periodKey=${periodKey}`,
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
  const [fresh, setFresh] = useState<{ scope: string; result: ValidationResultResponse } | null>(
    null,
  );
  // `UI-25`: кожна завершена перевірка — сигнал інспектору відкрити Issues, якщо є що.
  const [validatedSeq, setValidatedSeq] = useState(0);

  const validate = useMutation({
    mutationFn: (_scope: string) =>
      apiFetch<ValidationResultResponse>(
        `/api/v1/documents/${documentId}/validate`,
        {
          method: 'POST',
          // ⚠ Період — у ТІЛІ. До `A7-28` сервер читав його з рядка запиту, і
          // валідація мовчки йшла по періоду 0, відповідаючи «помилок немає».
          body: JSON.stringify({ periodKey } satisfies DocumentPeriodRequest),
        },
      ),
    onSuccess: (result, requestedScope) => {
      setFresh({ scope: requestedScope, result });
      setValidatedSeq((value) => value + 1);

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
  const freshForScope = fresh?.scope === scope ? fresh.result : null;
  const shownValidation =
    freshForScope ?? (lastValidation.data?.validated === false ? null : (lastValidation.data ?? null));

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
  const active = sheets.find((s) => s.code === sheet) ?? sheets[0];
  const activeCode = active?.code;

  // AN-28 P2-1: дія заблокована утриманою (відхиленою) коміркою - показати її:
  // аркуш, прокрутка, фокус і підсвітка - тим самим шляхом, що й зауваження (`ФВ-5.6`).
  useEffect(
    () =>
      registerHeldEditRevealer((held) => {
        if (held.periodKey !== periodKey) return;

        const target = tables.data?.find((table) => table.tableInstanceId === held.tableInstanceId);
        if (target === undefined) return;

        if (target.sheetCode !== activeCode) setSheet(target.sheetCode);
        void import('@/features/grid/cellNavigation').then((module) =>
          module.requestCellNavigation({
            tableDefId: target.tableDefId,
            tableInstanceId: target.tableInstanceId,
            rowKey: held.edit.rowKey,
            columnCode: held.edit.columnCode,
          }),
        );
      }),
    [periodKey, tables.data, activeCode, setSheet],
  );

  // ⚠ Стан береться з `SheetStates` документа за КОДОМ аркуша: скалярного
  // статусу документа не існує (D-93) — аркуші за один період бувають у
  // різних станах одночасно.
  const state =
    active === undefined || summary.data === undefined
      ? ''
      : (summary.data.sheetStates[active.code] ?? 'Draft');
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
  const projectId = summary.data?.projectId ?? null;

  const projects = useQuery({
    queryKey: ['projects'],
    // AN-39/L8-10: усі сторінки - проєкт поза першими 200 теж блокує дії в архіві.
    // ⚠ За `import()`: статичний імпорт додавав файл і ~1 КБ gzip до графа маршруту (бюджет D-132).
    queryFn: () => import('@/features/projects/allProjects').then((module) => module.fetchAllProjects()),
    enabled: projectId !== null,
  });

  const calendar = useQuery({
    queryKey: ['periods', projectId],
    queryFn: () => apiFetch<PeriodCalendarDto>(`/api/v1/projects/${String(projectId ?? 0)}/periods`),
    enabled: projectId !== null,
  });

  useProjectCurrentPeriodDefault(urlPeriod, calendar.data?.periods, setPeriodKey);

  const lock = documentLockOf({
    projectStatus: projects.data?.items?.find((project) => project.id === projectId)?.status,
    periodState: calendar.data?.periods?.find((period) => period.periodKey === periodKey)?.state,
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
  const readOnly = !isEditable(state) || locksDataActions(lock) || narrow;

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
   * ⚠ Імпорт статичний і бюджет чанка (`D-132`) не чіпає: `autosave.ts` не
   * тягне ядро `RevoGrid` — воно лишається за виразом `import()` вище.
   */
  useDocumentPending(documentId, session.data?.userId);

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

  // «History» і «Compare versions» — пункти «More» + лінивий діалог (макет: блоків між шапкою й сіткою немає).
  const documentLog = useDocumentLogActions({ documentId, periodKey });

  // ✎ Лінія B: бейдж у шапці й назва пункту «More», коли числа методологій застаріли. Запит спільний із
  // панеллю чисел (один ключ), тож завершений перерахунок оновлює обох.
  const calculationsStale = useCalculationsStale(documentId, periodKey, can(session.data, 'Calculation.View'));

  const refetchBoth = (): void => {
    void summary.refetch();
    void tables.refetch();
  };

  return (
    /*
     * ⛔ Обгортка навколо ВСЬОГО екрана: заголовок — це бізнес-ключ документа,
     * тобто теж дані. Помилки обох запитів зведені разом, бо екран без
     * таблиць — це не документ, а його назва.
     *
     * ⚠ Документ БЕЗ аркушів за обраний період — окремий стан, не помилка:
     * період міг ще не відкритися. Порожні вкладки без пояснення виглядали б
     * як несправність (`A7-30` показав, що так і буває насправді).
     */
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
              просто лишало те, що вже було. */
          <Suspense fallback={<Skeleton h={52} w={190} />}>
            <PeriodPicker
              size="xs"
              miw={110}
              value={periodKey}
              onChange={(value) => setPeriodKey(value ?? periodKey)}
            />
          </Suspense>
        }
      />

      {/* ✎ UI-14 (макет `screen-document.js`, `renderActions`): одна головна
          дія, ≤ 2 другорядні, решта — у меню «More»; ліворуч — стан словами.
          Діалоги рідкісних дій — поза меню: меню розмонтовує вміст, щойно
          закривається, тобто саме тоді, коли діалог мав би відкритися. */}
      {active !== undefined && (
        <DocumentActionBar
          documentId={documentId}
          periodKey={periodKey}
          sheetDefId={active.sheetDefId}
          sheetCode={active.code}
          sheetName={active.name}
          state={state}
          lock={lock}
          readOnly={readOnly}
          language={session.data?.language ?? 'en'}
          // ⛔ Імпорт лише туди, куди можна писати (`ФВ-5.20a`): пункт над
          // поданим аркушем обіцяв би заміну чисел, яку сервер відхилить.
          canImport={can(session.data, 'Document.Import')}
          canExport={can(session.data, 'Document.Export')}
          validate={{
            loading: validateLoading || validateAction.settling,
            run: () => validateAction.run(() => validate.mutateAsync(scope), { readOnly: true }),
          }}
          calculationsStale={calculationsStale}
          documentItems={[...documentLog.menuItems, businessKeyChange.menuItem, versionMigration.menuItem, deletion.menuItem]}
          resultsStale={document.resultsStale ?? null}
          resultsStaleSince={document.resultsStaleSince ?? null}
          status={
            <>
              {/* ✎ UI-15: чип стану аркуша і заповненість одним рядком
                  (макет `renderState`/`renderProgress`). */}
              <DocumentProgress
                documentId={documentId}
                periodKey={periodKey}
                sheets={sheets.map((s) => ({ code: s.code, name: s.name, state: document.sheetStates[s.code] ?? 'Draft' }))}
                activeCode={active.code}
                // ✎ UI-25 (інспектор): «K issues» — кнопка інспектора нижче, лічильник
                // за ВИДИМИМИ таблицями (`inspectorModel.ts`); тут другого немає.
                issues={null}
                onShowIssues={() => undefined}
              />
              <DocumentSaveState readOnly={readOnly} />
              {/* ⚠ Банер «результати застарілі» (`StaleResultsBanner`) уже каже те саме з кнопкою — значок лише дублював би. */}
              {calculationsStale && document.resultsStale !== true && (
                <Badge color="statusWarning" variant="light" role="status" data-testid="document-methodology-stale">
                  {t('documents.methodologyResultsStale')}
                </Badge>
              )}
            </>
          }
        />
      )}

      {/* ⛔ `F-18`: ЧОМУ тут нічого не змінити — одразу під рядком дій, до
          будь-якої сітки: сірі комірки без пояснення читаються як збій.
          ✎ UI-26: за наявного аркуша банер малює панель дій (з контекстом). */}
      {active === undefined && <DocumentLockBanner lock={lock} periodKey={periodKey} />}

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
        <DocumentHeaderPanel
          documentId={documentId}
          // ✎ UI-16: згорнута з підсумком; розгортається сама, коли поле
          // потребує уваги (обов'язкове порожнє, недійсна дата, незбережене).
          collapsible
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
          tables={tables.data ?? []}
          messages={shownValidation?.messages ?? null}
          validatedSeq={validatedSeq}
          onSelectFinding={(finding) => {
            // ⛔ `ФВ-5.6`: спершу аркуш зауваження, потім запит переходу. Модуль
            // переходу — за `import()`: він живе в чанку сітки, не сторінки
            // (`D-132`), і сітки однаково без нього не з'являться.
            const target = tables.data?.find((table) => table.tableDefId === finding.tableDefId);
            if (target === undefined) return;

            // ⚠ Лише коли аркуш інший: зміна адреси — це навігація, і на
            // активному аркуші вона нічого не дає, крім зайвого рендеру сторінки.
            if (target.sheetCode !== active?.code) setSheet(target.sheetCode);
            void import('@/features/grid/cellNavigation').then((module) =>
              module.requestCellNavigation(finding),
            );
          }}
        />
      </Suspense>

      {documentLog.dialog}

      {/* ✎ b4b: смуга аркушів — ПІД сіткою, як у макеті (`screen-document.js`
          `.statusbar` > `.sheet-tabs`; KIT §1.5), а не вкладками над нею. Список
          стоїть після панелей і липне до низу вікна (`documentSheetStrip.css`).
          ⛔ Аркуші — лише ті, що повернув API (видимі цьому користувачу, P1). */}
      <Tabs value={active?.code ?? null} onChange={setSheet} inverted>

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
          .filter((s) => s.code !== active?.code)
          .map((s) => (
            <Tabs.Panel key={s.code} value={s.code}>
              {null}
            </Tabs.Panel>
          ))}

        {/* ⚠ `pt="md"` і `Stack` відтворюють відступи, які раніше давав
            зовнішній `Stack` сторінки між вкладками й сіткою. */}
        {active !== undefined && (
          <Tabs.Panel value={active.code} pt="md">
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
                  readOnly={readOnly}
                  tables={active.tables}
                />
              )}
            </Stack>
          </Tabs.Panel>
        )}

        <Tabs.List className="doc-sheet-strip" aria-label={t('documents.sheets')} data-doc-sheet-strip="">
          {sheets.map((s) => (
            <Tabs.Tab key={s.code} value={s.code} className="doc-sheet-tab">
              {s.name}{' '}
              <StatusBadge kind="sheet" state={document.sheetStates[s.code] ?? 'Draft'} />
            </Tabs.Tab>
          ))}
        </Tabs.List>
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
      {can(session.data, 'Calculation.View') && (
        <Suspense fallback={null}>
          <CalculationResultsPanel documentId={documentId} periodKey={periodKey} />
        </Suspense>
      )}
    </Stack>
      )}
    </AsyncBoundary>
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

/** Аркуш із його таблицями. */
interface SheetGroup {
  sheetDefId: number;
  code: string;
  name: string;
  ordinal: number;
  tables: DocumentTableDto[];
}

/**
 * Групує таблиці за аркушами.
 *
 * ⚠ Сервер віддає плоский перелік екземплярів: так його можна віддати одним
 * запитом і не вигадувати вкладену структуру, яка все одно розбирається на
 * клієнті.
 */
function groupBySheet(tables: DocumentTableDto[]): SheetGroup[] {
  const sheets = new Map<string, SheetGroup>();

  for (const table of tables) {
    const group = sheets.get(table.sheetCode) ?? {
      sheetDefId: table.sheetDefId,
      code: table.sheetCode,
      // ✎ b4b (P1 приховані аркуші): назва — лише з даних API; немає — «—», а не код.
      name: localized(table.sheetNameL10n) || '—',
      ordinal: table.sheetOrdinal,
      tables: [],
    };

    group.tables.push(table);
    sheets.set(table.sheetCode, group);
  }

  return [...sheets.values()].sort((a, b) => a.ordinal - b.ordinal);
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

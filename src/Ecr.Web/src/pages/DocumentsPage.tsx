import { lazy, Suspense, useRef, useState, type JSX } from 'react';
import {
  ActionIcon,
  Box,
  Button,
  Code,
  Group,
  NativeSelect,
  SegmentedControl,
  Skeleton,
  Table,
  Text,
  VisuallyHidden,
} from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { listDocuments, listDocumentTemplateVersions, type DocumentListPage } from '@/features/documents/api';
import { DocumentListFilterBar } from '@/features/documents/DocumentListFilterBar';
import { DocumentOwnerCell } from '@/features/documents/DocumentOwnerCell';
import { DocumentListSummaryStrip } from '@/features/documents/DocumentListSummaryStrip';
import { useDocumentListFilters } from '@/features/documents/documentListFilters';
import { EyeIcon, IssueCount } from '@/features/documents/DocumentListMarks';
import { distinctVersions, mergeVersionOptions, nextVersionSort } from '@/features/documents/documentVersion';
import { DocumentVersionCell, DocumentVersionTh } from '@/features/documents/DocumentVersionColumn';
import { parseDocumentsView } from '@/features/documents/documentsBoardModel';
import '@/features/documents/documentsList.css';
import { LateEditsMark } from '@/features/documents/LateEditsMark';
import { StaleResultsMark, StaleResultsUndeterminedMark } from '@/features/documents/StaleResultsMark';
import { DocumentYearStrip, DocumentYearStripHeader, DocumentYearStripLegend } from '@/features/documents/DocumentYearStrip';
import { calendarYears, yearsWithProjects } from '@/features/projects/projectYear';
import { CollapsibleNote } from '@/features/common/CollapsibleNote';
import type { PeriodCalendarDto } from '@/api/types';
import { useCalendarsOf } from '@/features/projects/projectCalendars';
import { statusKey } from '@/shared/ui/StatusBadge';
import { formatNumber } from '@/shared/format';
import { can, useSession } from '@/shared/session/useSession';
import { localized } from '@/shared/i18n/localized';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { useDetailPanel } from '@/shared/ui/DetailDrawer';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { FilterInline } from '@/shared/ui/FilterBar';
import { PageHeader } from '@/shared/ui/PageHeader';
import { Timestamp } from '@/shared/ui/Timestamp';
import { useDebouncedFilter } from '@/shared/ui/useDebouncedFilter';
import { useUrlNumber, useUrlParamsSetter, useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';
import { projectsQuery } from '@/features/projects/allProjects';
import { DisabledReason } from '@/features/common/DisabledReason';

/**
 * Діалог створення документа — за `import()` (`D-132`), як і решта
 * рідковідкриваних форм у цьому кодовому шарі (`features/integration/
 * lazyDataSourceForm.ts`).
 *
 * ⚠ Кнопка «Створити» бачить не кожен (право `Document.Create`), а сам діалог
 * відкриває ще рідше той, хто його бачить — статичний імпорт змушував КОЖЕН
 * перелік документів нести форму, валідацію проєкту й вибір локалізованої
 * назви, яких переважна більшість відкриттів сторінки не торкається.
 */
const loadCreateDocumentModal = () => import('@/features/documents/CreateDocumentModal');
const CreateDocumentModal = lazy(async () => ({
  default: (await loadCreateDocumentModal()).CreateDocumentModal,
}));

/**
 * Швидкий перегляд документа (`UI-29`) — за `import()`: шторку відкривають
 * рідко, а бюджет маршруту (`D-132`) не має рости заради неї.
 */
/**
 * Місце банера перерахунку (lane A) — за `import()`: хук опитування задач і модель прогресу не входять у граф маршруту
 * (бюджет D-132: DocumentsPage на межі 250 КБ gzip після смуги року й лічильника застарілих за рік).
 */
const ProjectRecalcBannerSlot = lazy(async () => ({
  default: (await import('@/features/recalc/ProjectRecalcBannerSlot')).ProjectRecalcBannerSlot,
}));

const DocumentQuickLook = lazy(async () => ({
  default: (await import('@/features/documents/DocumentQuickLook')).DocumentQuickLook,
}));

/**
 * Подання «Board» (`UI-40`) — за `import()`: більшість відкриттів переліку — таблиця, і бюджет
 * маршруту (`D-132`) не має рости заради дошки.
 */
const DocumentsBoard = lazy(async () => ({
  default: (await import('@/features/documents/DocumentsBoard')).DocumentsBoard,
}));

/**
 * ✎ Period clarity (людина 07.11, п. b): перерахунок УСЬОГО проєкту переїхав зі сторінки Periods сюди — за
 * `import()` (`D-132`): дію бачить лише `Calculation.Recalculate`, а бюджет маршруту тісний.
 */
const ProjectRecalculateAction = lazy(async () => ({
  default: (await import('@/features/documents/ProjectRecalculateAction')).ProjectRecalculateAction,
}));


/**
 * Перелік документів.
 *
 * ✎ Period clarity (людина 07.11, п. e): типово — перегляд РОКУ: «один документ на об'єкт, усі місяці
 * всередині», у рядку — смуга станів 12 місяців (`periodStates`), клік по місяцю відкриває документ на ньому.
 * Обраний місяць — колишній перегляд за період: стан аркушів і документа за ОДИН місяць (`D-93`).
 */
export function DocumentsPage(): JSX.Element {
  /*
   * ✎ g1 (макет `projects-documents-1108.html`, людина 08.11: «так макет хороший»): фільтра «Month» більше НЕМАЄ —
   * документ завжди тримає всі 12 місяців, тож перелік — завжди за РІК; місяць обирають клітинкою смуги або в самому
   * документі. Рік — в АДРЕСІ (`?year=`, `ФВ-14.29`: перелік надсилають колезі). Стара адреса з `?periodKey=`
   * (закладки й посилання до 08.11) дає рік цього місяця, а не помилку.
   *
   * ⚠ Сусідні компоненти (смуга лічильників, фільтри, шторка) і далі приймають місяць — тут він завжди `null`.
   */
  const [legacyPeriodKey] = useUrlNumber('periodKey');
  const [urlYear] = useUrlNumber('year');
  const thisYear = new Date().getFullYear();
  const year = urlYear ?? (legacyPeriodKey !== null ? Math.trunc(legacyPeriodKey / 100) : thisYear);
  const [cursor] = useUrlState('cursor');
  // `UI-40`: подання — в адресі (`?view=board`, як у макеті), щоб посилання відкривало дошку.
  const [viewParam] = useUrlState('view');
  const view = parseDocumentsView(viewParam);
  const setUrlParams = useUrlParamsSetter();
  const [creating, setCreating] = useState(false);
  // ⚠ Лишається `true` назавжди після першого відкриття — так само, як
  // `requested` у `SearchLauncher.tsx`: діалог, щойно завантажений, більше не
  // розмонтовується між закриттям і повторним відкриттям (та сама поведінка,
  // що й до лінивого чанка, коли компонент був змонтований одразу).
  const [creatingRequested, setCreatingRequested] = useState(false);
  const session = useSession();
  const [panel, setPanel] = useDetailPanel();

  /*
   * ⚠ Шторка, раз відкрита, лишається змонтованою з останнім `?panel=`, а
   * «око»-відкривач запам'ятовується явно. Причина виміряна в Chromium:
   * лінива шторка монтується ВЖЕ відкритою, і `returnFocus` Mantine (він
   * ловить відкривач лише на ПЕРЕХОДІ `opened`) після Esc кидав фокус на
   * `body`. Вміст шторки й запити однаково живуть лише відкритої.
   */
  const quickLookOpener = useRef<HTMLButtonElement | null>(null);
  const [quickLookId, setQuickLookId] = useState<string | null>(panel);
  if (panel !== null && panel !== quickLookId) setQuickLookId(panel);

  // `BE-09b`: стан і «мої» — теж в адресі; без періоду стан у запит не йде.
  const filters = useDocumentListFilters(null, year);

  // `UI-18`: пошук іде в запит після паузи в наборі; поле й адреса оновлюються одразу.
  const appliedQ = useDebouncedFilter(filters.q.trim());
  // Версії - масив; у ключ кешу іде рядок, щоб новий масив з тими ж версіями не давав новий запит.
  const versionsKey = filters.versions.join(',');

  const query = useQuery({
    queryKey: [
      'documents',
      null,
      year,
      cursor,
      filters.state,
      filters.mine,
      filters.hasLateEdits,
      filters.hasReopenedMonths,
      filters.resultsStale,
      appliedQ,
      filters.projectId,
      filters.owner,
      versionsKey,
      filters.versionSort,
    ],
    queryFn: () =>
      listDocuments({
        periodKey: null,
        year,
        cursor,
        state: filters.state,
        mine: filters.mine,
        ownerUserId: filters.owner,
        templateVersions: filters.versions,
        versionSort: filters.versionSort,
        hasLateEdits: filters.hasLateEdits,
        hasReopenedMonths: filters.hasReopenedMonths,
        resultsStale: filters.resultsStale,
        q: appliedQ,
        projectId: filters.projectId,
      }),
  });

  // Варіанти фільтра «Version»: усі версії видимих документів (за проєктом і періодом), а не лише сторінки.
  const versionOptionsQuery = useQuery({
    queryKey: ['documents', 'template-versions', filters.projectId, null],
    queryFn: () => listDocumentTemplateVersions({ projectId: filters.projectId, periodKey: null }),
  });

  // ⛔ Аудит-пас 5: колонка «Project» показувала голий числовий `projectId`
  // — та сама сутність, чий код уже видно в діалозі «New document» одним
  // кліком поруч (`CreateDocumentModal`).
  // ⛔ `X-07`: усі сторінки, а не перші 200 мовчки (`projectsQuery` = `fetchAllProjects` під єдиним ключем).
  const projects = useQuery({ ...projectsQuery });

  /*
   * ⛔ FP-3 (прохід «від першого обличчя» 2026-10-11, P2). Користувач без жодного гранта на
   * проєкт (вбудовані `SystemAdministrator`/`TemplateAdministrator`/`PeriodAdministrator` у
   * щойно розгорнутій системі) бачив «документів немає — вони з'являться, щойно період
   * відкрито», тобто підказку про ПЕРІОД там, де справжня причина — ДОСТУП. Перелік проєктів
   * порожній лише за відсутності гранта (сервер фільтрує його за грантами), тож це і є ознака.
   *
   * ⚠ Лише коли перелік проєктів УСПІШНО прочитано й порожній: на відмові чи в дорозі
   * твердження про доступ було б неправдою про дані.
   */
  const noProjects = projects.isSuccess && projects.data.items.length === 0;

  /*
   * ✎ g1/g3 (макет 08.11): календарі проєктів — для варіантів «Year» (ЛИШЕ роки, де є проєкти) і для смуги
   * (неподаний місяць бере стан періоду: відкритий, заблокований, ще не відкритий) —
   * для всіх проєктів одним запитом-обгорткою (`useCalendarsOf`, пачки по 4 — N4-08; бюджет маршруту D-132).
   */
  const calendars = useCalendarsOf((projects.data?.items ?? []).map((project) => project.id));
  const calendarOf = (projectId: number): PeriodCalendarDto | undefined =>
    calendars[(projects.data?.items ?? []).findIndex((project) => project.id === projectId)];
  const yearOptions = yearsWithProjects(
    calendars,
    year,
  );
  // Найновіший рік із проєктами — ціль кнопки порожнього стану («Show 2026»).
  const latestYear = yearsWithProjects(
    calendars,
    null,
  )[0];
  const projectsOfYear = (projects.data?.items ?? []).filter((project) =>
    calendarYears(calendarOf(project.id)).includes(year),
  );

  /*
   * ✎ Period clarity (п. b): перерахунок — дія ПРОЄКТУ: той, що обрано фільтром, або єдиний. Лише активному
   * (у чернетці документів немає) і з правом `Calculation.Recalculate` — так само, як було на сторінці Periods.
   */
  const recalcProject =
    (filters.projectId !== null
      ? projects.data?.items.find((project) => project.id === filters.projectId)
      : projects.data?.items.length === 1
        ? projects.data.items[0]
        : undefined) ?? null;
  const showRecalc =
    recalcProject !== null && recalcProject.status === 'Active' && can(session.data, 'Calculation.Recalculate');
  /*
   * ✎ UAT-p3 (людина не знайшла Recalculate): за «All projects» (2+ проєкти) дія не зникає, а лишається на місці
   * недоступною з причиною «Choose a project to recalculate» (`DisabledReason`: aria-disabled, фокус і підказка).
   */
  const recalcNeedsProject =
    filters.projectId === null &&
    (projects.data?.items.length ?? 0) > 1 &&
    can(session.data, 'Calculation.Recalculate');

  /**
   * ⛔ Директива D15 §0, правило L10. Тут стояло
   * `…?.code ?? String(projectId)`, тож відмова `GET /api/v1/projects`
   * повертала колонку рівно в той стан, який прибрав аудит-пас 5: голий
   * числовий ідентифікатор у переліку на десятки рядків. Оператор відкриває
   * не той документ — і ніщо не каже, що підпис просто не прочитався.
   *
   * ⚠ Число з екрана не ховається: без коду воно єдине, що лишається від
   * адреси документа. Змінюється ФОРМА — `<Code>` замість тексту, тобто
   * «це ідентифікатор», а не «це назва проєкту», плюс видима причина над
   * таблицею.
   */
  const projectCodeOf = (projectId: number): JSX.Element | string =>
    projects.data?.items.find((project) => project.id === projectId)?.code ?? (
      <Code>{projectId}</Code>
    );

  /**
   * ⛔ UI-walkthrough F3: посилання було зібране як `/documents/${id}` — без
   * періоду. `DocumentPage.tsx` бере `urlPeriod ?? currentPeriodKey()`, тобто
   * без параметра відкриває ПОТОЧНИЙ місяць, а не той період, який людина
   * щойно обрала тут і про який ішлося. Вимога записана в самому
   * `DocumentPage.tsx`: «⛔ Період і аркуш — в адресі (`ФВ-14.29`). Посилання
   * на документ без них відкриває інший період і інший аркуш, ніж той, про
   * який ішлося.»
   *
   * ⚠ Сьогодні наслідок НЕВИДИМИЙ: період стенда (`202609`) випадково
   * дорівнює поточному місяцю, тож підстановка дає ту саму цифру. Тому тест
   * на цю поведінку мусить задавати період ЯВНО і ВІДМІННИЙ від поточного —
   * інакше він зелений і без фіксу.
   */
  const documentHref = (documentId: number): string => `/documents/${documentId}`;

  return (
    <>
      <PageHeader
        title={t('documents.title')}
        actions={
          <Group gap="xs" align="end">
            {/* ⛔ UI-аудит, lane 3: `setPeriodKey(...)` одразу за ним
                `setCursor(null)` — два ОКРЕМІ виклики сеттера `useUrlState`
                в одному обробнику — не компонувались: `setSearchParams`,
                викликаний двічі синхронно в тому самому тіку, губив ОБИДВІ
                зміни (підтверджено ізольованим тестом на голому
                `useSearchParams`). Поле «Period» виглядало інтерактивним
                (некерований DOM встигав показати введене), але жоден запит
                ніколи не бачив `periodKey` в адресі. `useUrlParamsSetter`
                оновлює обидва параметри ОДНИМ переходом. */}
            {/* ✎ g1 (макет 08.11): рік — ЄДИНИЙ фільтр часу, і в ньому лише роки, у яких є проєкти. Зміна року
                знімає фільтр проєкту й стану (вони належали попередньому року) і курсор. */}
            <NativeSelect
              size="xs"
              label={t('documents.year')}
              data={yearOptions.map((value) => ({ value: String(value), label: String(value) }))}
              value={String(year)}
              onChange={(event) => {
                setUrlParams({
                  year: Number(event.currentTarget.value),
                  periodKey: null,
                  projectId: null,
                  state: null,
                  cursor: null,
                });
              }}
              data-documents-year=""
            />

            {/* ⛔ Створення документа не мало в інтерфейсі жодної кнопки
                (`A7-42`): система, уся суть якої — заповнення документів,
                не давала створити перший. */}
            {/* ⛔ FP-3 (прохід «від першого обличчя» 2026-10-11): без жодного доступного проєкту
                створювати документ немає де — діалог відкривався б із порожнім вибором проєкту.
                Кнопка названа причину, а не мовчить. */}
            {can(session.data, 'Document.Create') && session.data?.isSimulation !== true && (
              <DisabledReason reason={noProjects ? t('documents.noProjects') : null}>
                <Button
                  onClick={() => {
                    setCreatingRequested(true);
                    setCreating(true);
                  }}
                >
                  {t('documents.create')}
                </Button>
              </DisabledReason>
            )}

            {showRecalc && recalcProject !== null && (
              <Suspense fallback={null}>
                <ProjectRecalculateAction key={recalcProject.id} project={recalcProject} />
              </Suspense>
            )}

            {recalcNeedsProject && (
              <DisabledReason reason={t('documents.recalcPickProject')}>
                <Button variant="default" data-project-recalculate-disabled="">
                  {t('workflow.recalculate')}
                </Button>
              </DisabledReason>
            )}
          </Group>
        }
      />

      {/*
       * ⛔ Через `<AsyncBoundary>`, а не через `ErrorAlert` плюс `?? []`.
       * Стара форма показувала невдалий запит і порожній перелік ОДНОЧАСНО:
       * зверху червона смуга, під нею таблиця з заголовками і жодним рядком —
       * тобто «даних немає» там, де сервер відмовив (`ФВ-14.22`).
       */}
      {/* ⚠ Фільтри — ПОЗА межею станів: коли фільтр нічого не знайшов, саме
          ними людина й виходить із порожнього стану. */}
      {/* ⚠ Смуга — теж ПОЗА межею: її лічильники фільтрують перелік, і під
          межею кожен клік знімав би її разом із фокусом на час запиту, а
          порожній результат — ховав би кнопку, якою фільтр і знімають.
          ⚠ І в ТОМУ САМОМУ ряду, що й фільтри (рішення людини 2026-10-06). */}
      {/* ✎ UAT recalc-progress (a2): після повернення на перелік прогрес перерахунку відновлюється з сервера. */}
      {/* ✎ g2 (приймання 08.11): лише перерахунки проєктів ОБРАНОГО року (у яких є період цього року), а за
          обраним проєктом — лише його. Інакше «Finished: …» проєкту 2026 стояв над переліком 2024. */}
      <Suspense fallback={null}>
        <ProjectRecalcBannerSlot
          projectId={filters.projectId}
          onlyProjects={projectsOfYear
            .map((project) => project.id)
            .filter((id) => filters.projectId === null || id === filters.projectId)}
        />
      </Suspense>

      {/* ✎ fg6: пояснення — компактне й згортається (пам'ятається на людину). */}
      <CollapsibleNote noteId="documents-year" summary={t('documents.oneDocumentNote')}>
        {t('documents.yearOnlyNote')}
      </CollapsibleNote>

      <DocumentListFilterBar
        periodKey={null}
        year={year}
        filters={filters}
        projects={projects.data?.items ?? []}
        // Варіанти — УСІ версії видимих документів (`GET /documents/template-versions`); обрані, яких сервер уже не
        // віддає, лишаються (їх є чим зняти). Поки відповіді немає (або вона відмовила) — версії поточної сторінки.
        versionOptions={mergeVersionOptions(
          versionOptionsQuery.data?.versions ?? distinctVersions(query.data?.items ?? []),
          filters.versions,
        )}
        staleOnPage={query.data?.items.filter((d) => d.resultsStale === true).length ?? 0}
        // ✎ D-450: під фільтром за людиною кожен рядок сторінки — її документ, тож ім'я вже є без окремого запиту.
        ownerName={
          query.data?.items.find((d) => filters.owner !== null && d.ownerUserId === filters.owner)?.ownerDisplayName ??
          undefined
        }
      >
        {/* ✎ `UI-18` (макет `FilterBar`, KIT §3): «Clear filters» з'являється
            сама, щойно є активний фільтр, і скидає все ОДНИМ переходом
            (`filters.reset` — один `setSearchParams`). Без фільтрів кнопки
            немає зовсім — не вимкнена, а відсутня. */}
        {filters.active && (
          <FilterInline>
            <Button variant="subtle" onClick={filters.reset} data-clear-filters="">
              {t('documents.clearFilters')}
            </Button>
          </FilterInline>
        )}
        {/* ✎ Period clarity (п. e): за «All months» — лічильники ЗА РІК (`GET /documents/summary?year=`), тим самим
            правилом, що й фільтр стану за рік; за місяць — як і раніше. */}
        <DocumentListSummaryStrip periodKey={null} year={year} filters={filters} />
        {/* ✎ `UI-40` (макет: `E.Segmented` «Table · Board» праворуч у рядку фільтрів):
            фільтри, період і курсор — СПІЛЬНІ для обох подань, бо лежать в адресі. */}
        <FilterInline ml="auto">
          <SegmentedControl
            size="xs"
            aria-label={t('documents.view')}
            value={view}
            onChange={(value) => setUrlParams({ view: value === 'board' ? 'board' : null })}
            data={[
              { value: 'table', label: t('documents.viewTable') },
              { value: 'board', label: t('documents.viewBoard') },
            ]}
            data-documents-view={view}
          />
        </FilterInline>
      </DocumentListFilterBar>

      {/* ✎ g3 (макет 08.11): легенда клітинок смуги — під фільтрами, лише для таблиці (дошка смуги не має). */}
      {view === 'table' && <DocumentYearStripLegend />}

      {/*
       * ⛔ Три порожні стани не виглядають однаково (L10): «фільтр нічого не
       * знайшов» — власний заголовок і кнопка скидання; «документів немає» —
       * колишній текст про відкриття періоду; відмова — стан помилки межі
       * (перевіряється першою, до порожнечі). Сказати «документів немає» там,
       * де їх сховав фільтр, — неправда, з якою йдуть створювати дублікат.
       */}
      <AsyncBoundary<DocumentListPage>
        isPending={query.isPending}
        error={query.error}
        data={query.data}
        isEmpty={(page) => page.items.length === 0}
        emptyTitle={
          filters.active
            ? t('documents.noMatch')
            : noProjects
              ? t('documents.noProjects')
              : t('documents.emptyYear', { year })
        }
        emptyHint={
          filters.active
            ? t('documents.noMatchHint')
            : noProjects
              ? t('documents.noProjectsHint')
              : projectsOfYear.length > 0
                ? t('documents.emptyYearProjects', {
                    year,
                    projects: projectsOfYear
                      .map((project) => `${project.code} (${t(statusKey('project', project.status))})`)
                      .join(', '),
                  })
                : t('documents.emptyYearNoProject', { year })
        }
        emptyAction={
          filters.active ? (
            <Button variant="default" onClick={filters.reset}>
              {t('documents.resetFilters')}
            </Button>
          ) : latestYear !== undefined && latestYear !== year ? (
            // ✎ g2 (макет 08.11): з порожнього року — одним кліком до року, де є робота.
            <Button variant="default" onClick={() => setUrlParams({ year: latestYear, cursor: null })} data-show-year={latestYear}>
              {t('documents.showYear', { year: latestYear })}
            </Button>
          ) : undefined
        }
        skeleton="table"
        onRetry={() => void query.refetch()}
      >
        {(page) => {
          // Колонка «Version»: фільтр і сортування - СЕРВЕРНІ (`templateVersions`, `sort`), сторінка вже в потрібному порядку.
          const rows = page.items;
          const hasOwner = (row: (typeof rows)[number]): boolean => typeof row.ownerDisplayName === 'string';

          return (
          <>
            {/* ⛔ Відмова переліку проєктів — окрема від відмови переліку
                документів (та під власною межею вище): сама таблиця приїхала,
                не прочиталися лише підписи проєктів. Тому банер тут, а не
                замість таблиці. */}
            {projects.error !== null && (
              <ErrorAlert error={projects.error} onRetry={() => void projects.refetch()} />
            )}

            {/*
             * ✎ `UI-19` (макет `screens-work.js` → екран «/», `10-docs-list-light.png`):
             * Document (назва + ключ і проєкт другим рядком) · Sheets (смужка станів
             * аркушів + «N of M approved») · Issues · Updated (хто, коли, пізня
             * правка) · State (стан документа, `quiet`) · «око» — швидкий перегляд.
             * Окремої колонки Project більше немає: проєкт — другим рядком.
             * ⚠ Колонки Period з макета немає навмисно: перелік завжди за ОДИН
             * період (він у полі «Period» над таблицею), а періодичність проєкту
             * в рядку не приходить — підпис «Sep 2026» для квартального проєкту
             * був би неправдою.
             *
             * ⛔ Прихований аркуш (P1, безпека): усе в рядку — лише з відповіді
             * сервера. «N of M» рахує ВИДИМІ аркуші (`sheets`/`sheetStates`), а не
             * `sheetCount`, — інакше число видало б існування прихованого аркуша.
             */}
            {view === 'board' ? (
              <Suspense fallback={<Skeleton height={240} />}>
                <DocumentsBoard
                  documents={rows}
                  documentHref={documentHref}
                  projectCode={(projectId) => projects.data?.items.find((project) => project.id === projectId)?.code}
                  onQuickLook={(documentId, opener) => {
                    quickLookOpener.current = opener;
                    setPanel(String(documentId));
                  }}
                />
              </Suspense>
            ) : (
              // `UI-42`: на вузькому екрані прокручується таблиця, а не сторінка (`documentsList.css`).
              <div className="ecr-docs-table-scroll">
                {/* ✎ Live-прохід 08.11 (P3, правило людини «видимий текст, а не підказки»): власника приховано
                    доступом (R-7, `DocumentOwnerCell`) — один приглушений рядок над таблицею, а не лише підказка. */}
                {rows.some((row) => !hasOwner(row) && row.modifiedAt == null) && (
                  <Text size="xs" c="dimmed" mb="xs" data-owner-hidden-note="">
                    {t('documents.ownerHiddenNote')}
                  </Text>
                )}
                <Table
                  highlightOnHover
                  className="ecr-sticky-head"
                  data-documents-table=""
                  data-documents-scope="year"
                >
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>{t('documents.document')}</Table.Th>
                      <Table.Th data-column="owner">{t('documents.owner')}</Table.Th>
                      {/* ✎ Period clarity / g3: смуга 12 місяців — над нею перші літери місяців (макет `.ms.hd`). */}
                      <Table.Th data-column="months">
                        <DocumentYearStripHeader year={year} />
                      </Table.Th>
                      <Table.Th>{t('documents.issues')}</Table.Th>
                      <Table.Th data-column="updated">{t('documents.updated')}</Table.Th>
                      <DocumentVersionTh
                        sort={filters.versionSort}
                        onSort={() => filters.setVersionSort(nextVersionSort(filters.versionSort))}
                      />
                      <Table.Th>
                        <VisuallyHidden>{t('documents.quickLookColumn')}</VisuallyHidden>
                      </Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {rows.map((document) => {
                      const name = localized(document.nameL10n);
                      const errors = document.errorCount;

                      return (
                        <Table.Tr key={document.id} data-document-row={document.businessKey}>
                          <Table.Td data-column="document">
                            {/* ⛔ Директива «людське ім'я документа»: ім'я ПОРУЧ із
                                бізнес-ключем, а не замість нього — ключ бере участь в
                                експортах і аудиті й лишається видимим завжди. */}
                            <Box>
                              <Link to={documentHref(document.id)}>{name.length > 0 ? name : document.businessKey}</Link>
                              <Group gap="xs" wrap="nowrap">
                                {name.length > 0 && (
                                  <Text size="xs" c="dimmed" ff="monospace">
                                    {document.businessKey}
                                  </Text>
                                )}
                                {name.length > 0 && (
                                  <Text size="xs" c="dimmed" aria-hidden="true">
                                    ·
                                  </Text>
                                )}
                                {/* ⚠ Доки перелік у дорозі, місце тримає скелет, а не
                                    число: інакше колонка на мить показувала б
                                    ідентифікатори. */}
                                {projects.isPending ? (
                                  <Skeleton height={10} width={40} radius="sm" data-projects="pending" />
                                ) : (
                                  <Text size="xs" c="dimmed" data-document-project="">
                                    {projectCodeOf(document.projectId)}
                                  </Text>
                                )}
                              </Group>
                            </Box>
                          </Table.Td>
                          <Table.Td data-column="owner">
                            {/* ✎ D-450: власник (автор документа, передається D-443). `null` — людини вже немає або
                                доступ звужено (сервер ім'я не віддає) — видиме «—», а не порожнеча. */}
                            <DocumentOwnerCell ownerDisplayName={document.ownerDisplayName} modifiedAt={document.modifiedAt} />
                          </Table.Td>
                          <Table.Td data-column="months">
                            <DocumentYearStrip
                              documentId={document.id}
                              businessKey={document.businessKey}
                              year={year}
                              periodStates={document.periodStates}
                              calendar={calendarOf(document.projectId)}
                              reopenedPeriods={document.reopenedPeriods}
                            />
                          </Table.Td>
                          <Table.Td data-column="issues" data-document-errors={errors ?? 'none'}>
                            {/* ⛔ BE-09: `null` — документ НЕ ПЕРЕВІРЯЛИ (або роль не
                                бачить усіх аркушів) — «—», а не «0»: нуль під
                                неперевіреним документом — та сама неправда, що `A7-28`. */}
                            {errors === null || errors === undefined ? (
                              <Text c="dimmed">—</Text>
                            ) : errors > 0 ? (
                              <IssueCount count={errors} />
                            ) : (
                              <Text size="sm" c="dimmed">
                                {formatNumber(errors)}
                              </Text>
                            )}
                          </Table.Td>
                          <Table.Td data-column="updated">
                            <Box>
                              {typeof document.modifiedByDisplayName === 'string' && (
                                <Text size="sm">{document.modifiedByDisplayName}</Text>
                              )}
                              <Group gap="xs" wrap="wrap">
                                <Text size="xs" c="dimmed">
                                  <Timestamp value={document.modifiedAt} />
                                </Text>
                                {/* `BE-09b`: пізні правки за період (без періоду — за будь-який). */}
                                {document.hasLateEdits && <LateEditsMark />}
                                {/* RC14-D: результати методологій застаріли (лише `true`; `null` — доступ звужено). */}
                                {document.resultsStale === true && <StaleResultsMark since={document.resultsStaleSince} />}
                                {/* ✎ D-446: чому застарілість не визначена — частину документа читачеві закрито. */}
                                {document.resultsStaleUndetermined === 'PartiallyHidden' && <StaleResultsUndeterminedMark />}
                              </Group>
                            </Box>
                          </Table.Td>
                          <Table.Td data-column="version">
                            <DocumentVersionCell source={document} />
                          </Table.Td>
                          <Table.Td data-column="quick-look">
                            {/* `UI-29`: швидкий перегляд у шторці під `?panel=<id>`. */}
                            <ActionIcon
                              size="sm"
                              variant="subtle"
                              color="gray"
                              aria-label={t('documents.quickLook', { key: document.businessKey })}
                              data-quick-look={document.id}
                              onClick={(event) => {
                                quickLookOpener.current = event.currentTarget;
                                setPanel(String(document.id));
                              }}
                            >
                              <EyeIcon />
                            </ActionIcon>
                          </Table.Td>
                        </Table.Tr>
                      );
                    })}
                  </Table.Tbody>
                </Table>
              </div>
            )}

            {/* ⚠ Курсорна пагінація, а не offset: за місяць у проєкті тисячі
                документів, і сторінка 200 через OFFSET сканує все, що до неї. */}
            {page.nextCursor !== null && (
              <Button mt="md" variant="default" onClick={() => setUrlParams({ cursor: page.nextCursor })}>
                {t('documents.more')}
              </Button>
            )}
          </>
          );
        }}
      </AsyncBoundary>

      {/* ⚠ Шторка монтується лише за `?panel=`. Документ береться з уже
          завантаженої сторінки; посилання на документ поза нею шторка дочитує
          сама. */}
      {quickLookId !== null && (
        <Suspense fallback={null}>
          <DocumentQuickLook
            panelId={quickLookId}
            year={year}
            // ✎ D-466 (огляд 08.11): блоки місяця шторки — за ПОТОЧНИМ періодом проєкту (`isCurrent`, D-77).
            currentPeriodOf={(projectId) => {
              const calendar = calendarOf(projectId);
              return calendar === undefined ? undefined : (calendar.periods.find((p) => p.isCurrent)?.periodKey ?? null);
            }}
            document={query.data?.items.find((item) => String(item.id) === quickLookId)}
            listReady={query.data !== undefined}
            onClose={() => {
              // Після зняття шторки — фокус назад на «око» (KIT §5; «Аудит»: e2e -Grep "модальний").
              const opener = quickLookOpener.current;
              window.setTimeout(() => opener?.focus(), 0);
            }}
            projectCode={(projectId) => projects.data?.items.find((project) => project.id === projectId)?.code}
            documentHref={documentHref}
          />
        </Suspense>
      )}

      {creatingRequested && (
        <Suspense fallback={null}>
          <CreateDocumentModal
            opened={creating}
            onClose={() => setCreating(false)}
            preferredPeriodKey={null}
          />
        </Suspense>
      )}
    </>
  );
}

import { Suspense, lazy, useEffect, useState, type JSX } from 'react';
import { Badge, Button, Group, Modal, ScrollArea, Select, Stack, Table, Text, Textarea, VisuallyHidden } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type { PagedProjects, PeriodCalendarDto, ReopenPeriodRequest } from '@/api/types';
import { hasProjectGrant } from '@/features/documents/projectGrant';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { Hint } from '@/shared/ui/Hint';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatusBadge, statusKey } from '@/shared/ui/StatusBadge';
import { formatMonthYear } from '@/shared/format';
import { siteMomentText, zoneClock } from '@/shared/siteMoment';
import { showApiError, showDone } from '@/shared/ui/notify';
import { useUrlNumber } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';
import { projectsQuery } from '@/features/projects/allProjects';
import { FilterRow } from '@/shared/ui/FilterBar';

// Правило поясу майданчика живе в `shared/siteMoment`; ім'я лишається доступним звідси.
export { siteMomentText } from '@/shared/siteMoment';

// UI-33: плитки року й «All projects» — окремим чанком (бюджет `D-132`).
const PeriodYearTiles = lazy(async () => ({
  default: (await import('@/features/projects/PeriodsOverview')).PeriodYearTiles,
}));
const PeriodsStatStrip = lazy(async () => ({
  default: (await import('@/features/projects/PeriodsOverview')).PeriodsStatStrip,
}));
const AllProjectsPeriods = lazy(async () => ({
  default: (await import('@/features/projects/PeriodsOverview')).AllProjectsPeriods,
}));
// ✎ UAT-p3: людина шукала перерахунок саме тут — та сама дія, що й на переліку документів (окремим чанком, D-132).
const ProjectRecalculateAction = lazy(async () => ({
  default: (await import('@/features/documents/ProjectRecalculateAction')).ProjectRecalculateAction,
}));
/**
 * Поле дати — за `import()`, і не заради стилю.
 *
 * ⛔ `@mantine/dates` тягне за собою `dayjs`, і зі СТАТИЧНИМ імпортом він уже
 * ламав гейт `D-132` на `SnapshotsPage` (259.3 КБ gzip проти межі 250). Поле
 * з'являється лише в діалозі перевідкриття — тобто в дії, яку робить один
 * адміністратор кілька разів на рік; вантажити `dayjs` усім, хто просто
 * дивиться календар періодів, нема за що.
 */
const DateInput = lazy(async () => {
  const module = await import('@/shared/dates/DateInputWithStyles');

  return { default: module.DateInput };
});

/** Зсув зони від UTC у мілісекундах САМЕ в цю мить (літній час — теж мить). */
function zoneOffsetMs(instant: number, timeZoneId: string): number {
  const parts = zoneClock(timeZoneId).formatToParts(new Date(instant));

  const at = (type: Intl.DateTimeFormatPartTypes): number =>
    Number(parts.find((part) => part.type === type)?.value ?? '0');

  // ⚠ `% 24`: за `h24` північ приходить як «24», і без цього доба з'їжджала б
  // рівно на межі, заради якої вся функція й потрібна.
  const wall = Date.UTC(at('year'), at('month') - 1, at('day'), at('hour') % 24, at('minute'), at('second'));

  return wall - instant;
}

function SiteTime({
  value,
  zone,
  dateOnly = false,
  inclusiveEnd = false,
}: {
  readonly value: string | null | undefined;
  readonly zone: string;
  readonly dateOnly?: boolean;
  /**
   * Межа ВИКЛЮЧНА (`endsAt` — «після цього моменту закрито»), а показати
   * треба останній ДЕНЬ, коли ще можна: північ 17 березня — це «до 16
   * березня включно», а не «17 березня».
   */
  readonly inclusiveEnd?: boolean;
}): JSX.Element {
  if (value === null || value === undefined || value === '') return <span data-timestamp="none">—</span>;

  const text = siteMomentText(value, zone, dateOnly, inclusiveEnd);
  if (text === null) return <span data-timestamp="unparsed">{value}</span>;

  return (
    <time dateTime={value} title={value} data-timestamp="ok">
      {text}
    </time>
  );
}

/**
 * Який календарний відрізок покриває період (`X-34`): «January 2026»,
 * «Q4 2025», «2026».
 *
 * ⛔ `Sequence` — порядковий номер, а не місяць (`R-A6`): у квартальному
 * проєкті 202504 — четвертий КВАРТАЛ 2025, а не квітень. Підпис без
 * періодичності проєкту вгадував би саме місяць.
 */
export function periodCaption(year: number, sequence: number, kind: string): string {
  if (kind === 'Monthly' && sequence >= 1 && sequence <= 12) {
    // ⚠ `A2-10`: місяць — із каталогу, а не з `Intl` (у Chrome немає `kk`).
    return formatMonthYear(year, sequence);
  }

  if (kind === 'Quarterly') return t('periods.quarterOf', { quarter: sequence, year });
  if (kind === 'Yearly') return String(year);

  return t('periods.customOf', { sequence, year });
}

/**
 * ✎ Period clarity (людина 07.11, п. a): мить, з якої місяць блокується, — одна дата в рядку.
 *
 * ⚠ Перевідкритий і ще не закритий місяць блокується за строком перевідкриття (`D-68`), а не за
 * календарем: інакше рядок показував би минулу дату над відкритим місяцем.
 */
export function lockMomentOf(period: {
  readonly state: string;
  readonly endsAt: string;
  readonly reopenedUntil?: string | null;
}): string {
  const reopened = period.reopenedUntil ?? null;

  return reopened !== null && period.state !== 'Closed' ? reopened : period.endsAt;
}

/**
 * Обрана КАЛЕНДАРНА дата → момент, до якого період лишається відкритим.
 *
 * ⛔ Межа — північ НАСТУПНОЇ доби в поясі МАЙДАНЧИКА, тобто дослівно те саме,
 * що рахує сервер за порожнього `until` (`ReopenPeriodHandler.EndOfSiteDay`,
 * `local.Date.AddDays(1)` → UTC, `D-68`). Два інших очевидних варіанти —
 * неправильні:
 *  - `date.toISOString()` дає ПІВНІЧ ПОЧАТКУ обраної доби, тобто вікно
 *    коротше на добу: «відкрити до 30 вересня» закрилося б 29-го ввечері;
 *  - північ у поясі ТОГО, ХТО ДИВИТЬСЯ, розходиться з поясом майданчика рівно
 *    на різницю зсувів — для проєкту на `Asia/Atyrau` (+05:00), відкритого з
 *    Києва (+02:00/+03:00), це 2–3 години рівно там, де вирішується «встиг чи не встиг»
 *    (той самий дефект, про який попереджає підпис поясу над таблицею).
 *
 * ⚠ Два наближення, а не одне: зсув залежить від моменту, а момент — від
 * зсуву. Перше наближення міряє зсув по цей бік переходу на літній час, друге
 * — по той; для зон без переходу обидва дають те саме.
 */
export function endOfSiteDayUtc(day: Date, timeZoneId: string): string {
  const wall = Date.UTC(day.getFullYear(), day.getMonth(), day.getDate() + 1);
  const first = wall - zoneOffsetMs(wall, timeZoneId);

  return new Date(wall - zoneOffsetMs(first, timeZoneId)).toISOString();
}

/**
 * Проєкти і календар їхніх періодів.
 *
 * ⚠ Стан періоду **обчислюється з часу і зсувів**, а не зберігається полем:
 * збережений статус розійшовся б із календарем рівно тоді, коли фонова задача
 * не спрацювала. Тому екран показує те, що віддає сервер, і не рахує сам.
 *
 * ⚠ `PeriodKey = Year*100 + Sequence` (R-A6), і `Sequence` — це **порядковий
 * номер періоду**, а не місяць: у квартальному проєкті їх чотири.
 *
 * ✎ Period clarity (людина 07.11): сторінка — ЛИШЕ блокування місяців (стан, дата блокування, перевідкриття).
 * Життєвий цикл проєкту (створити, клонувати, активувати, заархівувати), маршрут, політики й пояс — у
 * налаштуваннях проєкту (`ProjectSettingsPage`), перерахунок — на переліку документів.
 */
export function PeriodsPage(): JSX.Element {
  const [projectId, setProjectId] = useUrlNumber('projectId');
  // UI-33: період, обраний плиткою року, — його рядок у календарі виділено.
  const [tileKey, setTileKey] = useState<number | null>(null);
  const queryClient = useQueryClient();
  const session = useSession();

  // Який період відкриваємо; `null` — діалог закритий.
  const [reopening, setReopening] = useState<number | null>(null);

  // Причина і строк перевідкриття. Обидва скидаються ПРИ ВІДКРИТТІ діалогу
  // (`openReopen` нижче), а не при закритті: причина попереднього відкриття,
  // що лишилася в полі, — найтихіший спосіб підписати цю дію поясненням від
  // зовсім іншого періоду (той самий вибір, що й у `ReasonModal`).
  const [reopenReason, setReopenReason] = useState('');
  const [reopenUntil, setReopenUntil] = useState<Date | null>(null);

  // ⛔ Проєкти ВИБИРАЮТЬСЯ зі списку, а не вводяться номером. Це не про
  // зручність: без переліку не видно СТАНУ проєкту, а саме він визначає, чи
  // відкриються періоди взагалі (`A7-25`).
  // ⛔ `X-07`: усі сторінки, а не перші 200 мовчки (`projectsQuery` = `fetchAllProjects` під єдиним ключем).
  const projects = useQuery({ ...projectsQuery });

  const periods = useQuery({
    queryKey: ['periods', projectId],
    queryFn: () => apiFetch<PeriodCalendarDto>(`/api/v1/projects/${projectId ?? 0}/periods`),
    enabled: projectId !== null,
  });

  const selected = (projects.data?.items ?? []).find((p) => p.id === projectId);

  /*
   * ⛔ `U-10`. Сторінка відкривалася на «Pick a project», хоча проєкт у
   * системі ОДИН — тобто вимагала вибору там, де вибору немає, і до кліку
   * лишалася порожньою.
   *
   * ⛔ Межа автовибору названа прямо, і вона вузька: обирається лише коли
   * варіант рівно ОДИН. Автовибір «першого-ліпшого» з десяти тут заборонений
   * — він мовчки показав би адміністраторові календар чужого проєкту, і
   * найдорожче те, що на екрані ніщо не сказало б, що вибір зроблено за
   * нього. Проєкти рівноправні: «поточного» серед них немає (на відміну від
   * періоду, у якого є `isCurrent` — див. `DocumentsPage.tsx`), тож іншого
   * чесного критерію, ніж «він один», не існує.
   *
   * ⚠ Вибір потрапляє в АДРЕСУ (`?projectId=`, `useUrlNumber`), а не в
   * локальний стан: поле показує його, і посилання на сторінку лишається
   * робочим. Це те саме, що зробила б людина кліком.
   *
   * ⚠ Ефект, а не обчислення під час рендера: `setProjectId` пише в адресу,
   * тобто це побічна дія. Умова `projectId === null` тримає його одноразовим
   * — знявши вибір, людина не отримає його назад тим самим тактом (окрема
   * дія «скинути» на цій сторінці не передбачена, але й підміняти намір
   * мовчки не можна).
   */
  const onlyProject = projects.data?.items.length === 1 ? projects.data.items[0] : undefined;

  useEffect(() => {
    if (projectId === null && onlyProject !== undefined) setProjectId(onlyProject.id);
  }, [projectId, onlyProject, setProjectId]);

  /** Перечитує проєкти і календар після будь-якої зміни. */
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: projectsQuery.queryKey });
    await queryClient.invalidateQueries({ queryKey: ['periods', projectId] });
  };

  /**
   * Відкриття закритого періоду (`ФВ-1.10`).
   *
   * ⛔ Відкриття періоду і відкриття документа — РІЗНІ операції з різними
   * правами (`D-67`). Право `Period.Reopen` існувало від Етапу 3, а кнопки не
   * було: закритий період не відкривався з інтерфейсу взагалі, і документ у
   * ньому теж — бо повернення аркуша відхиляється `ECR-PRD-4223`, доки
   * закритий період.
   */
  const reopenPeriod = useMutation({
    mutationFn: (target: { id: number; reason: string; until: string | null }) =>
      apiFetch(`/api/v1/periods/${target.id}/reopen`, {
        method: 'POST',
        body: JSON.stringify({
          reason: target.reason,

          /*
           * ⛔ Тут стояв жорсткий `until: null` із коментарем «Безстроково»
           * — обіцянка, якої сервер не виконує НІКОЛИ. За порожнього `until`
           * `ReopenPeriodHandler` бере `EndOfSiteDay` (`D-68`), тобто період
           * закривається сам опівночі в поясі майданчика; безстрокового
           * відкриття не існує в принципі. Людина натискала «відкрити»,
           * читала «безстроково» і дізнавалася про межу вже по факту —
           * наступного ранку, коли період знову закритий.
           *
           * ⚠ Тепер `null` лишається ЗНАЧЕННЯМ, а не замовчуванням, про яке
           * мовчать: порожнє поле строку означає рівно те, що зробить сервер,
           * і підпис під полем каже це словами.
           */
          until: target.until,
        } satisfies ReopenPeriodRequest),
      }),
    onSuccess: async () => {
      await refresh();
      setReopening(null);
      showDone(t('periods.reopened'));
    },
    onError: showApiError,
  });

  /*
   * ⚠ Пояс МАЙДАНЧИКА, а не той, у якому сидить адміністратор: строк
   * перевідкриття — момент, і сервер міряє його саме цим поясом (`D-68`).
   *
   * ⚠ `?? 'UTC'` — гілка, якої в житті немає: діалог відкривається лише з
   * рядка календаря, тобто `periods.data` на той момент уже приїхав. Названа
   * вона тому, що мовчазний запасний пояс був би найтихішою з можливих
   * неправд про строк.
   */
  const siteZone = periods.data?.timeZoneId ?? 'UTC';

  // UI-33: дата плиток і огляду — тим самим правилом поясу, що й колонки календаря.
  const siteDate = (value: string, zone: string, inclusiveEnd: boolean): string | null =>
    siteMomentText(value, zone, true, inclusiveEnd);

  // Те, що поїде в тіло запиту. `null` — порожнє поле, тобто «до кінця доби
  // майданчика» рішенням СЕРВЕРА, а не нашим.
  const reopenUntilIso = reopenUntil === null ? null : endOfSiteDayUtc(reopenUntil, siteZone);

  /*
   * ⛔ Строк, який уже минув, на сервер не їде. Домен його НЕ відхиляє —
   * `Period.Reopen` бере будь-який момент, — і наслідок гірший за відмову:
   * період переходить у `Grace` із межею в минулому, тобто найближчий прогін
   * `PeriodStateJob` закриє його назад, і на екрані це виглядатиме як
   * «кнопка не спрацювала».
   *
   * ⚠ Межа звіряється з МОМЕНТОМ, а не з календарною датою: обрана СЬОГОДНІШНЯ
   * дата дає кінець сьогоднішньої доби майданчика — він ще попереду, і
   * забороняти його нема за що.
   */
  const reopenUntilPast = reopenUntilIso !== null && Date.parse(reopenUntilIso) <= Date.now();

  const openReopen = (periodId: number): void => {
    setReopenReason('');
    setReopenUntil(null);
    setReopening(periodId);
  };

  // ⛔ Право `Period.Reopen` не каже, ЧИЇ періоди: сервер вимагає ще й гранта
  // Manage на проєкт (`ReopenPeriodHandler`). Без цієї умови кнопка обіцяла б
  // дію, яка завершиться 403.
  const reopens =
    can(session.data, 'Period.Reopen') &&
    projectId !== null &&
    hasProjectGrant(session.data, projectId, 'Manage');

  return (
    <>
      <PageHeader
        title={t('periods.title')}
        actions={
          <FilterRow>
            <Select
              size="xs"
              miw={220}
              label={t('periods.project')}
              placeholder={t('periods.pickProject')}
              /*
               * ⚠ У варіанті списку компонента бути не може — потрібен РЯДОК.
               * Тому підпис береться тим самим ключем каталогу, що й у
               * `StatusBadge` (`statusKey`), а не кодом сервера: `Archived`
               * англійською посеред казахського інтерфейсу — це той самий
               * дефект, що й `{period.state}` у таблиці нижче, лише в
               * випадному списку.
               */
              data={(projects.data?.items ?? []).map((p) => ({
                value: String(p.id),
                label: `${p.code} · ${t(statusKey('project', p.status))}`,
              }))}
              value={projectId === null ? null : String(projectId)}
              onChange={(value) => {
                setTileKey(null);
                setProjectId(value === null ? null : Number(value));
              }}
            />

            {/* ✎ Period clarity (людина 07.11, п. b): маршрут погодження, політики, клон, пояс, життєвий цикл
                проєкту — у налаштуваннях проєкту; перерахунок — на переліку документів. Тут — лише місяці. */}
            <Button
              variant="default"
              component={Link}
              to={projectId === null ? '/admin/projects' : `/admin/projects?projectId=${String(projectId)}`}
              data-project-settings-link=""
            >
              {t('projectSettings.title')}
            </Button>

            {/* ✎ UAT-p3: перерахунок обраного проєкту — те саме правило, що на переліку документів (лише Active і
                `Calculation.Recalculate`), той самий компонент і перелік пропущених. */}
            {selected !== undefined && selected.status === 'Active' && can(session.data, 'Calculation.Recalculate') && (
              <Suspense fallback={null}>
                <ProjectRecalculateAction key={selected.id} project={selected} label={t('periods.recalcProject')} />
              </Suspense>
            )}
          </FilterRow>
        }
      />

      {/* Недоступний перелік проєктів лишає порожнім сам вибір — це треба
          сказати, а не показати порожній Select. */}
      <AsyncBoundary<PagedProjects>
        isPending={projects.isPending}
        error={projects.error}
        data={projects.data}
        isEmpty={(page) => page.items.length === 0}
        emptyTitle={t('periods.noProjects')}
        emptyHint={t('periods.noProjectsHint')}
        onRetry={() => void projects.refetch()}
      >
        {() => null}
      </AsyncBoundary>

      {/* UI RC9 (макет `/admin/periods`): смуга показників — ПЕРШЕ під заголовком, а не під плитками. */}
      {(projects.data?.items.length ?? 0) > 0 && (
        <Suspense fallback={null}>
          <PeriodsStatStrip projects={projects.data?.items ?? []} />
        </Suspense>
      )}

      {selected?.status === 'Draft' && (
        <Text c="statusWarning" size="sm" mb="xs">
          {t('periods.draftHint')}
        </Text>
      )}

      {/*
       * ⚠ Доки проєкт не обрано, `data` — `undefined`: запиту ще не було, і
       * обгортка каже саме це, а не «періодів немає».
       */}
      {projectId !== null && (
      <AsyncBoundary<PeriodCalendarDto>
        isPending={periods.isPending}
        error={periods.error}
        data={periods.data}
        isEmpty={(calendar) => calendar.periods.length === 0}
        emptyTitle={t('periods.noPeriods')}
        emptyHint={t('periods.noPeriodsHint')}
        skeleton="table"
        onRetry={() => void periods.refetch()}
      >
        {(calendar) => (
        <>
        {/* ⛔ Пояс названо ПОРУЧ із межами, а не лише у формі створення
            (директива ПК-1 №06 §3). Колонки нижче показують моменти в поясі
            МАЙДАНЧИКА (`D-68`), і без підпису «01.02 00:00» читається як
            місцевий час того, хто дивиться. Для проєкту на `Asia/Atyrau`
            (+05:00), відкритого з Києва (+02:00/+03:00), це різниця в 2–3
            години рівно там, де вирішується, встиг чи не встиг. */}
        <Suspense fallback={null}>
          <PeriodYearTiles
            calendar={calendar}
            projectCode={selected?.code ?? ''}
            projectStatus={selected?.status}
            siteDate={siteDate}
            selectedKey={tileKey}
            onPick={(key) => {
              setTileKey(key);
              document.querySelector(`[data-period-row="${String(key)}"]`)?.scrollIntoView?.({ block: 'nearest' });
            }}
          />
        </Suspense>

        <Text size="sm" mb="xs" data-periods-lock-note="">
          {t('periods.lockOnlyNote')}
        </Text>
        <Text size="xs" c="dimmed" mb="xs">
          {t('periods.timeZone')}: {calendar.timeZoneId}
        </Text>

        {/* ⛔ Аудит-пас 5: без обмеження ширини контейнера `Badge`-мітки
            стану (`Table.Th периods.state`) обтинались еліпсисом, щойно
            сторінка звужувалась (Mantine `Badge .label` — `overflow:hidden;
            text-overflow:ellipsis`) — той самий дефект, що вже виправлено
            для матриці ролей у `SecurityPage.tsx`, тим самим прийомом. */}
        {/* ✎ Period clarity (людина 07.11, п. a): сторінка — ЛИШЕ блокування місяців. Рядок = місяць: підпис,
            стан (Open / Late (flagged) / Locked) і ОДНА дата «Locks on»; ключ і порядковий номер — у підказці.
            Колонки «Range» (початок — жорстке закриття, що читалось як «місяць триває до 15-го наступного») і
            «Grace until» прибрано: пізні правки позначаються як і раніше (`D-70`), їх видно станом рядка. */}
        <ScrollArea type="auto" offsetScrollbars>
        <Table striped className="ecr-sticky-head" data-periods-table="months">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('periods.columnMonth')}</Table.Th>
              <Table.Th>{t('periods.state')}</Table.Th>
              {/* ⚠ `Hint` із `focusable` (Q-337): формула — з РЕАЛЬНИХ чисел політики проєкту, доступна з клавіатури. */}
              <Table.Th>
                <Hint
                  focusable
                  label={t('periods.locksOnHint', {
                    grace: calendar.policy.graceOffsetDays,
                    hardClose: calendar.policy.hardCloseOffsetDays,
                    code: calendar.policy.code,
                  })}
                >
                  <Text span td="underline dotted" fw={600} size="sm">
                    {t('periods.locksOn')}
                  </Text>
                </Hint>
              </Table.Th>
              {/* ⚠ `X-29`: колонка дій — з назвою для читалки, а не порожня. */}
              <Table.Th>
                <VisuallyHidden>{t('common.actions')}</VisuallyHidden>
              </Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {calendar.periods.map((period) => (
              <Table.Tr
                key={period.periodKey}
                data-period-row={period.periodKey}
                data-selected={tileKey === period.periodKey ? 'true' : undefined}
                {...(tileKey === period.periodKey ? { bg: 'var(--mantine-primary-color-light)' } : {})}
              >
                <Table.Td>
                  {/* ⚠ `X-34`: що саме покриває період — за періодичністю
                      проєкту, а не з `periodKey` арифметикою (`R-A6`).
                      ✎ Period clarity: підпис — головне, ключ і номер — у підказці (фокус/наведення). */}
                  <Hint
                    focusable
                    label={t('periods.keyTooltip', { key: period.periodKey, sequence: period.sequence })}
                  >
                    <Text span fw={600} size="sm" data-period-caption="" data-period-key={period.periodKey}>
                      {periodCaption(period.year, period.sequence, calendar.periodKind)}
                    </Text>
                  </Hint>
                  {period.isCurrent && (
                    <Badge ml="xs" size="xs" variant="light">
                      {t('periods.current')}
                    </Badge>
                  )}
                  {/* ⛔ UI-аудит, lane 2: `CurrentPeriod` — підказка
                      інтерфейсу, а НЕ правило доступу (`D-77`); «Pin»
                      дозволяє призначити поточним будь-який період незалежно
                      від його стану. Наслідок без цього бейджа — «current»
                      мовчки опинявся на Closed-періоді, а той, що
                      справді Open, лишався взагалі без жодної позначки, і
                      нічого на екрані про це не сигналило. */}
                  {period.isCurrent && period.state !== 'Open' && (
                    <Badge ml="xs" size="xs" color="statusWarning" variant="outline">
                      {t('periods.currentNotOpen')}
                    </Badge>
                  )}
                </Table.Td>
                <Table.Td>
                  {/* ⛔ UI-аудит, lane 8 (рішення НЕ скасоване, лише переїхало):
                      `ScrollArea` (Аудит-пас 5) обгортає ТАБЛИЦЮ, але сам
                      `Badge` лишався здатним стискатись — table-layout: auto
                      бере ширину стовпця з того, що РЕНДЕРИТЬСЯ, а
                      `.mantine-Badge-label`'s власний `overflow:hidden`
                      дозволяє йому «поміститись» у будь-яку ширину замість
                      того, щоб змусити таблицю (і тим самим ScrollArea)
                      прокручуватись. Наслідок — «Scheduled» ставало
                      нечитабельним «S…». `min-width: fit-content` тепер несе
                      САМ `StatusBadge` — безумовно, для всіх різновидів:
                      сторінка, яка мусить пам'ятати про цей проп, — це та сама
                      розбіжність між екранами, заради усунення якої набір і
                      заведено (той самий дефект уже ловили окремо для
                      `SnapshotsPage`). */}
                  <StatusBadge kind="period" state={period.state} />
                  {/* ⚠ Відкритий понад календар період видно окремо: інакше
                      `Open` після кінця місяця виглядає як несправність
                      календаря, а не як свідоме рішення людини. */}
                  {period.reopenedUntil !== null && (
                    <Badge ml="xs" size="xs" color="statusWarning" variant="outline">
                      {/* ⚠ Тут `formatDateTime`, а не `<Timestamp>`: момент
                          підставляється ВСЕРЕДИНУ рядка каталогу, а компонент
                          — це вузол, який у параметр `t()` не вкладеш. Година
                          лишається (на відміну від меж періоду вище): це
                          КРАЙНІЙ СТРОК, і «до 30 вересня» без години не
                          відповідає на питання «чи встигну ще сьогодні». */}
                      {t('periods.reopenedUntil', {
                        until:
                          siteMomentText(period.reopenedUntil, calendar.timeZoneId) ?? period.reopenedUntil,
                      })}
                    </Badge>
                  )}
                </Table.Td>
                {/* ✎ Period clarity: ОДНА дата — коли місяць блокується. Перевідкритий місяць блокується за строком
                    перевідкриття (`D-68`), решта — за жорстким закриттям (`endsAt`, виключна межа: «Locks on» — мить,
                    з якої запис уже не приймається). Із годиною: це крайній строк, у поясі МАЙДАНЧИКА. */}
                <Table.Td data-period-locks-on={period.periodKey}>
                  <SiteTime value={lockMomentOf(period)} zone={calendar.timeZoneId} />
                </Table.Td>
                <Table.Td>
                  <Group gap="xs" justify="flex-end">
                    {period.state === 'Closed' && reopens && (
                      <Button
                        size="xs"
                        variant="subtle"
                        onClick={() => openReopen(period.id)}
                      >
                        {t('periods.reopen')}
                      </Button>
                    )}
                  </Group>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
        </ScrollArea>
        </>
        )}
      </AsyncBoundary>
      )}

      {/* UI-33: «All projects · current period» — без вибору проєкту це головний
          вміст сторінки, а не порожній «Pick a project»; рядок обирає проєкт. */}
      {(projects.data?.items.length ?? 0) > 0 && (
        <Suspense fallback={null}>
          <AllProjectsPeriods
            projects={projects.data?.items ?? []}
            selectedId={projectId}
            onPick={(id) => {
              setTileKey(null);
              setProjectId(id);
            }}
            siteDate={siteDate}
          />
        </Suspense>
      )}

      {/*
        ⛔ Не `ReasonModal`, і це не дублювання заради дублювання: перевідкриття
        збирає ДВА значення — причину і строк, — а `ReasonModal` приймає рівно
        одне поле і живе в `shared/ui`, тобто розширювати його заради одного
        екрана означало б платити всіма його споживачами. Поведінка причини
        лишається та сама: поле обов'язкове, кнопка вимкнена, доки воно порожнє.
      */}
      <Modal
        opened={reopening !== null}
        onClose={() => setReopening(null)}
        title={t('periods.reopen')}
      >
        <Stack gap="xs">
          <Textarea
            label={t('workflow.reason')}
            description={t('periods.reopenHint')}
            value={reopenReason}
            onChange={(event) => setReopenReason(event.currentTarget.value)}
            minRows={3}
            autosize
            data-autofocus
          />

          {/*
            ⚠ `Suspense` із порожнім запасним вузлом: поле їде окремим чанком, і
            «завантаження…» на ті мілісекунди читалося б як несправність. Тиху
            втрату строку це не відкриває — порожнє поле й означає `null`, тобто
            рівно те саме, що напис під ним обіцяє.

            ⛔ НЕ `minDate`: він мовчки відкидав би набрану вручну минулу дату,
            і людина бачила б порожнє поле без жодного слова про те, чому. Межу
            тут стереже видима причина нижче, а не зникле значення.
          */}
          <Suspense fallback={null}>
            <DateInput
              label={t('periods.reopenUntil')}
              description={t('periods.reopenUntilHint')}
              // Формат заданий кодом — однозначний і не залежить від локалі ОС.
              clearable
              value={reopenUntil}
              onChange={setReopenUntil}
              /*
               * ⚠ Причина — власний рядок каталогу, а не позичений підпис
               * стану. Доти тут стояв `periods.reopenedUntil` («open until
               * {until}»), бо свого рядка не було; він читається як «період
               * відкрито до…», хоча період ще НЕ відкривали — тобто описує
               * наслідок замість причини відмови.
               *
               * ⛔ Сам запобіжник не дублює сервер: `Period.Reopen` минулий
               * строк приймає, і період пішов би в `Grace` із межею в минулому,
               * а наступний прогін `PeriodStateJob` закрив би його — мовчки.
               */
              error={reopenUntilPast ? t('periods.reopenUntilPast') : undefined}
            />
          </Suspense>

          <Group justify="flex-end" mt="md">
            <Button variant="default" onClick={() => setReopening(null)}>
              {t('common.cancel')}
            </Button>
            <Button
              disabled={reopenReason.trim().length === 0 || reopenUntilPast}
              loading={reopenPeriod.isPending}
              onClick={() => {
                if (reopening !== null) {
                  reopenPeriod.mutate({
                    id: reopening,
                    reason: reopenReason.trim(),
                    until: reopenUntilIso,
                  });
                }
              }}
            >
              {t('periods.reopen')}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}

/*
 * ✎ Тут стояла `stateColor(state)`. Її змістовне рішення — «`Grace` виділено
 * окремим кольором, а не зведено до „відкритого“: правка в пільговому строку
 * позначається як пізня (D-70) і виглядає в аудиті інакше» — живе далі в
 * `statusTable.period` (`shared/ui/StatusBadge.tsx`), де `Grace` — `warning`.
 *
 * ⛔ А от чого в наборі НЕМАЄ навмисно — це `default: 'blue'`, під який тут
 * потрапляв `Scheduled`: «ще не відкрито» фарбувалося тим самим кольором, що
 * й невідомий стан сервера, тобто «нема чого робити» і «клієнт відстав від
 * сервера» були на екрані нерозрізненні. У наборі `Scheduled` — `muted`,
 * невідоме — `warning`.
 */

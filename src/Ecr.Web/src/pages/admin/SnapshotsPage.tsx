import { lazy, Suspense, useEffect, useRef, useState, type JSX } from 'react';
import {
  Anchor,
  Badge,
  Button,
  Group,
  Modal,
  NumberInput,
  ScrollArea,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiEnqueue, apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import type {
  BuildSnapshotRequest,
  JobStatus,
  PagedProjects,
  ReportDefinition,
  ReportSnapshotSummary,
} from '@/api/types';
import { outcomeOf, pollInterval } from '@/features/workflow/jobFollow';
import { humanizeJobId } from '@/features/workflow/jobLabel';
import { ReportDefinitionsModal } from '@/features/reports/ReportDefinitionsModal';
import { snapshotExportUrl } from '@/features/reports/api';
import { SnapshotFormatBadge } from '@/features/reports/SnapshotFormatBadge';
import {
  NoParameters,
  defaultDraft,
  missingRequired,
  readReportParameters,
  toParametersBody,
  type ParameterDraft,
  type ParameterValue,
  type ReportParameterDeclaration,
} from '@/features/reports/parameters';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { showApiError, showDone } from '@/shared/ui/notify';
import { useUrlNumber } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';

// ⚠ За `import()`: бюджет маршруту тісний, а рядки зрізу відкривають рідко.
const SnapshotRowsModal = lazy(() => import('@/features/reports/SnapshotRowsModal'));

/**
 * Поле дати — теж за `import()`, і не заради стилю.
 *
 * ⛔ `@mantine/dates` тягне за собою `dayjs`, і зі СТАТИЧНИМ імпортом цей
 * маршрут важив 259.3 КБ gzip — тобто ламав гейт `D-132` (межа 250; за
 * `import()` вийшло 245.4).
 * Поле з'являється лише в діалозі побудови й лише для звіту, що оголосив
 * параметр типу `Date`; вантажити його всім, хто просто дивиться перелік
 * зрізів, нема за що — рівно той аргумент, яким винесений Monaco.
 */
const DateInput = lazy(async () => {
  const module = await import('@mantine/dates');

  return { default: module.DateInput };
});

/**
 * Зрізи регламентної звітності.
 *
 * ⛔ Це **не** екран звітів. Звітність лишається в SSRS (`D-52`), і маршрутів
 * `/reports/*` в інтерфейсі немає навмисно. Наша межа — `rpt.*`: незмінний
 * зріз, який SSRS читає. Тут його будують і бачать, на яких даних він
 * побудований.
 *
 * ✎ `D-52a` (2026-09-19): SSRS лишається для PDF держформ, але рядки зрізу
 * тепер видно й тут — дія «View rows» (`SnapshotRowsModal`).
 *
 * ⛔ Побудова не мала в клієнті жодного споживача (`A7-39`): зріз можна було
 * створити лише запитом повз інтерфейс, тобто звітність для регулятора
 * залежала від того, чи є поруч людина з `curl`.
 *
 * ⚠ Зріз **незмінний**: повторна побудова створює новий, а не переписує
 * старий. Інакше звіт, роздрукований учора, і той самий звіт сьогодні давали б
 * різні числа без жодного сліду (`ФВ-9.17`).
 */
export function SnapshotsPage(): JSX.Element {
  const queryClient = useQueryClient();
  const session = useSession();

  const [projectId, setProjectId] = useUrlNumber('projectId');
  const [periodKey, setPeriodKey] = useUrlNumber('periodKey');

  const [building, setBuilding] = useState(false);
  const [managing, setManaging] = useState(false);
  const [code, setCode] = useState<string | null>(null);
  const [buildPeriod, setBuildPeriod] = useState(currentPeriodKey());

  const projects = useQuery({
    queryKey: ['projects'],
    queryFn: () => apiFetch<PagedProjects>('/api/v1/projects?limit=200'),
  });

  // ⛔ Аудит-пас 5: рядок списку показував голий `snapshot.projectId`
  // (число з бази) замість коду проєкту — той самий довідник уже
  // завантажено для селектора вище, лишалося лише звести id → код.
  const projectCodeOf = (projectId: number): string =>
    projects.data?.items.find((project) => project.id === projectId)?.code ?? String(projectId);

  // ⛔ Перелік описів звітів, а не поле для набору коду руками (`W7`). До
  // цього єдиним способом вказати звіт було ВГАДАТИ його код: описів у базі
  // не створювало ніщо, тож будь-який набраний код відмовляв `ECR-RPT-0404`, і
  // відрізнити «помилився в коді» від «звіту не існує взагалі» було нічим.
  const reportDefs = useQuery({
    queryKey: ['report-defs'],
    queryFn: () => apiFetch<ReportDefinition[]>('/api/v1/reports'),
  });

  // ⚠ У виборі — лише те, за чим зріз СПРАВДІ побудується: чинний опис із
  // опублікованою версією. Показати решту означало б пропонувати варіанти,
  // кожен другий з яких відмовляє без пояснення (побудова бере лише
  // `Published`).
  //
  // ⛔ `?? []` — лише для «ще їде»: ВІДМОВУ переліку показує `referenceError`
  // нижче, а не «опублікованих звітів немає» (це була б неправда про дані).
  const buildable = (reportDefs.data ?? []).filter(
    (definition) =>
      definition.isActive && definition.versions.some((v) => v.status === 'Published'),
  );

  // ⛔ `R6`: параметри звіту (`@Name`). Оголошення читаються з `rulesJson`
  // ОПУБЛІКОВАНОЇ версії — саме її бере побудова
  // (`IReportDefinitionStore.FindCurrentVersionAsync`), і чернетка опису тут ні
  // до чого.
  const selectedDefinition = code === null ? undefined : buildable.find((d) => d.code === code);
  const publishedVersion = selectedDefinition?.versions.find((v) => v.status === 'Published');

  // ⚠ Доки звіт не обрано, питання «які в нього параметри» не стоїть — це не
  // «прочитати не вдалося». Кнопку побудови й так тримає `code === null`.
  const parameters =
    code === null ? NoParameters : readReportParameters(publishedVersion?.rulesJson);

  // ⛔ Чернетка тримається РАЗОМ із кодом звіту, а не окремим станом, який
  // скидає ефект: ефект виконується ПІСЛЯ рендера, тобто існував би кадр, у
  // якому поля вже від нового звіту, а значення ще від старого — і саме такий
  // кадр поїхав би в запит, натисни людина побудову досить швидко.
  const [draft, setDraft] = useState<{ code: string | null; values: ParameterDraft }>({
    code: null,
    values: {},
  });

  const values =
    draft.code === code
      ? draft.values
      : defaultDraft(parameters.kind === 'declared' ? parameters.items : []);

  const setValue = (name: string, value: ParameterValue): void =>
    setDraft({ code, values: { ...values, [name]: value } });

  /*
   * ⛔ Деградація в бік ЗАБОРОНИ (`D15` §0, `L10`). Два стани блокують
   * побудову, і причини в них РІЗНІ:
   *   - оголошення прочитати не вдалося — побудова наосліп або впаде `422`,
   *     або пройде без параметра й дасть зріз, який виглядає нормальним;
   *   - обов'язковий параметр без значення — сервер однаково відмовить `422`,
   *     але дізнатися про це з екрана ДО кліку дешевше, ніж із невдалої задачі.
   *
   * ⚠ «Параметрів немає» — ТРЕТІЙ стан, і він нічого не блокує.
   */
  const blockedReason =
    parameters.kind === 'unreadable'
      ? 'snapshots.parametersUnknown'
      : missingRequired(parameters.items, values).length > 0
        ? 'snapshots.parametersBlocked'
        : null;

  // Довідники сторінки (проєкти у фільтрі, описи звітів у двох вікнах): їхня
  // відмова — банер над переліком; сам перелік зрізів від них не залежить.
  const referenceError = projects.error ?? reportDefs.error;

  const retryReferences = (): void => {
    if (projects.error !== null) void projects.refetch();
    if (reportDefs.error !== null) void reportDefs.refetch();
  };

  const snapshots = useQuery({
    queryKey: ['snapshots', projectId, periodKey],
    queryFn: () =>
      apiFetch<ReportSnapshotSummary[]>(
        '/api/v1/reports/snapshots' +
          (projectId === null ? '' : `?projectId=${projectId}`) +
          (periodKey === null ? '' : `${projectId === null ? '?' : '&'}periodKey=${periodKey}`),
      ),
  });

  // ⛔ UI-аудит, lane 6: побудова повертає `202` з `jobId` — задача фонова,
  // і в момент відповіді сервера рядок зрізу ще не існує. Стара версія
  // інвалідувала `['snapshots']` ОДРАЗУ на цій відповіді (не на завершенні
  // задачі), тож перезапит незмінно приходив ще ДО того, як задача встигала
  // хоч щось записати — сторінка лишалася на «зрізів іще нема» назавжди,
  // без жодного опитування, аж доки хтось не перезавантажить сторінку
  // руками. Прийом і модуль (`jobFollow.ts`, `pollInterval`/`outcomeOf`) —
  // ті самі, що вже working у `ExportButton.tsx`/`PeriodsPage.tsx`.
  const [jobId, setJobId] = useState<string | null>(null);

  const build = useMutation({
    mutationFn: () => {
      // ⚠ Поле `parameters` з'являється в тілі ЛИШЕ коли параметри оголошені:
      // для звіту без них запит лишається побайтно таким, яким був до `R6`.
      const declared = parameters.kind === 'declared' ? parameters.items : [];
      const body = toParametersBody(declared, values);

      return apiEnqueue(`/api/v1/reports/${encodeURIComponent(code ?? '')}/build`, {
        projectId: projectId ?? 0,
        periodKey: buildPeriod,
        ...(body === undefined ? {} : { parameters: body }),
      } satisfies BuildSnapshotRequest);
    },
    onSuccess: (job) => {
      setJobId(job.jobId);
      setBuilding(false);
      // ⛔ Аудит-пас 8, lane6, п.8: людський вигляд у тості, сам `jobId` — не.
      showDone(t('snapshots.queued', { job: humanizeJobId(job.jobId) }));
    },
    onError: showApiError,
  });

  const job = useQuery({
    queryKey: ['job', jobId],
    queryFn: () => apiFetch<JobStatus>(`/api/v1/jobs/${encodeURIComponent(jobId ?? '')}`),
    enabled: jobId !== null,
    refetchInterval: (query) => pollInterval(query.state.data?.state),
    retry: false,
  });

  // BE-17: перевірка незмінності. Підсумок тримається ПО ЗРІЗУ, а не один на
  // сторінку: звіряють зазвичай кілька зрізів одного періоду поспіль, і
  // відповідь, що зникає з натисканням наступної кнопки, довелося б записувати
  // на папірці.
  const [verified, setVerified] = useState<Record<number, SnapshotVerifyResponse>>({});

  // D-52a: рядки зрізу в застосунку — другий споживач `rpt.*` поруч із SSRS.
  const [viewing, setViewing] = useState<number | null>(null);

  const verify = useMutation({
    mutationFn: (snapshotId: number) =>
      apiFetch<SnapshotVerifyResponse>(`/api/v1/reports/snapshots/${snapshotId}/verify`, {
        method: 'POST',
      }),
    onSuccess: (result, snapshotId) =>
      setVerified((previous) => ({ ...previous, [snapshotId]: result })),
    onError: showApiError,
  });

  const outcome = jobId === null ? null : outcomeOf(job.data?.state, job.isError);

  // ⚠ Інвалідація й тост — ОДИН раз на задачу, не на кожен рендер: `ref`,
  // не стан, той самий захист, що й `ExportButton.tsx`.
  const reported = useRef<string | null>(null);

  useEffect(() => {
    if (jobId === null || outcome === null || outcome === 'running') return;
    if (reported.current === jobId) return;

    reported.current = jobId;

    if (outcome === 'succeeded') {
      void queryClient.invalidateQueries({ queryKey: ['snapshots'] });
      showDone(t('snapshots.built'));
    } else if (outcome === 'failed') {
      notifications.show({
        color: 'statusError',
        message: job.data?.error ?? t('snapshots.buildFailed'),
      });
    }

    // ⛔ `unknown` (стан прочитати не вдалося, брак `System.ViewHealth`) —
    // навмисно без тосту, той самий прецедент, що й `ExportButton.tsx`:
    // причина — брак права на читання задачі, а не збій побудови.
  }, [jobId, outcome, job.data?.error, queryClient]);

  return (
    <>
      <PageHeader
        title={t('snapshots.title')}
        actions={
          <Group gap="xs" align="end">
            <Select
              size="xs"
              miw={200}
              label={t('documents.project')}
              placeholder={t('periods.pickProject')}
              data={(projects.data?.items ?? []).map((project) => ({
                value: String(project.id),
                label: project.code,
              }))}
              value={projectId === null ? null : String(projectId)}
              onChange={(value) => setProjectId(value === null ? null : Number(value))}
            />

            <NumberInput
              size="xs"
              miw={110}
              label={t('documents.period')}
              value={periodKey ?? ''}
              onChange={(value) => setPeriodKey(typeof value === 'number' ? value : null)}
            />

            {/* ⛔ Вікно описів на відмові показало б «описів немає» — і запросило б
                завести дублікат. Вимкнено, доки перелік не приїде. */}
            {can(session.data, 'Report.EditDefinition') && (
              <Button
                size="xs"
                variant="default"
                disabled={reportDefs.error !== null}
                onClick={() => setManaging(true)}
              >
                {t('reportDefs.manage')}
              </Button>
            )}

            {can(session.data, 'Report.BuildSnapshot') && (
              <Button size="xs" disabled={projectId === null} onClick={() => setBuilding(true)}>
                {t('snapshots.build')}
              </Button>
            )}
          </Group>
        }
      />

      <ErrorAlert error={referenceError} onRetry={retryReferences} />

      <AsyncBoundary<ReportSnapshotSummary[]>
        isPending={snapshots.isPending}
        error={snapshots.error}
        data={snapshots.data}
        isEmpty={(all) => all.length === 0}
        emptyTitle={t('snapshots.empty')}
        emptyHint={t('snapshots.emptyHint')}
        skeleton="table"
        onRetry={() => void snapshots.refetch()}
      >
        {(all) => (
          // ⛔ Аудит-пас 5: без обмеження ширини контейнера `Badge`-мітка
          // статусу (`snapshots.status`) обтиналась еліпсисом, щойно сторінка
          // звужувалась (Mantine `Badge .label` — `overflow:hidden;
          // text-overflow:ellipsis`) — той самий дефект, що вже виправлено
          // для матриці ролей у `SecurityPage.tsx`, тим самим прийомом.
          <ScrollArea type="auto" offsetScrollbars>
          <Table striped className="ecr-sticky-head">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('snapshots.builtAt')}</Table.Th>
                <Table.Th>{t('documents.project')}</Table.Th>
                <Table.Th>{t('documents.period')}</Table.Th>
                <Table.Th>{t('snapshots.rows')}</Table.Th>
                <Table.Th>{t('snapshots.status')}</Table.Th>
                <Table.Th>{t('snapshots.hash')}</Table.Th>
                <Table.Th>{t('snapshots.verify')}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {all.map((snapshot) => (
                <Table.Tr key={snapshot.id}>
                  <Table.Td>
                    {/* ⚠ Момент побудови — з годиною: два зрізи одного дня
                        розрізняються саме нею. Точне значення лишається в
                        `dateTime`/`title` (`Timestamp`), бо зріз незмінний і
                        момент його побудови — частина доказу, а не підпис. */}
                    <Timestamp value={snapshot.builtAt} />
                    {snapshot.isCurrent && (
                      <Badge ml="xs" size="xs" variant="light">
                        {t('snapshots.current')}
                      </Badge>
                    )}
                  </Table.Td>
                  <Table.Td>{projectCodeOf(snapshot.projectId)}</Table.Td>
                  <Table.Td>{snapshot.periodKey ?? '—'}</Table.Td>
                  <Table.Td>
                    <Group gap="xs" wrap="nowrap">
                      {snapshot.rowCount}
                      <Button
                        size="compact-xs"
                        variant="default"
                        onClick={() => setViewing(snapshot.id)}
                      >
                        {t('snapshots.viewRows')}
                      </Button>

                      {/*
                        ⛔ Посилання, а не `fetch` із кнопки: вивантаження
                        автентифікується тією самою cookie, що й сторінка, тож
                        браузер завантажує книгу сам (`snapshotExportUrl` — це
                        URL-білдер, не запит). Кнопка, яка тягла б файл у
                        пам'ять і віддавала його `Blob`-посиланням, додала б
                        крок, який нічого не вирішує.

                        ⚠ Право `Report.Export` — окреме від перегляду рядків:
                        книга виходить за межі системи, і той, хто може
                        подивитися зріз на екрані, не обов'язково може винести
                        його назовні.

                        ⚠ Межа Excel названа ПОРУЧ із дією, а не у довідці:
                        числа в книзі мають 15 значущих цифр, і той, хто звіряє
                        до останнього знаку, мусить дізнатися про це ДО
                        вивантаження, а не після. Для звірки без утрат лишається
                        перегляд рядків поруч.
                      */}
                      {can(session.data, 'Report.Export') && (
                        <Anchor
                          size="xs"
                          href={snapshotExportUrl(snapshot.id)}
                          download
                          title={t('snapshots.exportHint')}
                        >
                          {t('snapshots.export')}
                        </Anchor>
                      )}
                    </Group>
                  </Table.Td>
                  <Table.Td>
                    {/* ⚠ Статус зрізу успадковується від стану даних: зріз
                        `Draft` існує, але регулятор його не бачить —
                        вʼюха `rpt.v_*` віддає лише `Approved` і `Submitted`
                        (`ФВ-10.11`). */}
                    {/* ⛔ Бейдж набору, а не сирий код сервера: підпис — із
                        каталогу (`status.snapshot.*`), а `miw="fit-content"`
                        (UI-аудит-пас 8, lane6, п.9) тепер живе в самому
                        `StatusBadge`, а не на сторінці. */}
                    {/* ⚠ Формат чисел (2026-09-21): поданий зріз не
                        перебудовується, тож старий показує менше знаків —
                        позначка каже, що це формат, а не дефект. */}
                    <Group gap="xs" wrap="nowrap">
                      <StatusBadge kind="snapshot" state={snapshot.status} />
                      <SnapshotFormatBadge format={snapshot.hashFormat} />
                    </Group>
                  </Table.Td>
                  <Table.Td>
                    {/* ⛔ Контрольна сума показується цілком, а не обрізаною:
                        саме нею два зрізи одного періоду відрізняються один
                        від одного, і «перші вісім символів збіглися» — не
                        відповідь на питання «це той самий зріз». */}
                    <Text size="xs" style={{ wordBreak: 'break-all' }}>
                      {snapshot.contentHash ?? '—'}
                    </Text>
                  </Table.Td>
                  <Table.Td>
                    <VerifyCell
                      result={verified[snapshot.id]}
                      loading={verify.isPending && verify.variables === snapshot.id}
                      onVerify={() => verify.mutate(snapshot.id)}
                    />
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
          </ScrollArea>
        )}
      </AsyncBoundary>

      <Modal opened={building} onClose={() => setBuilding(false)} title={t('snapshots.build')}>
        <Select
          label={t('snapshots.code')}
          description={t('snapshots.codeHint')}
          placeholder={t('snapshots.pickReport')}
          nothingFoundMessage={reportDefs.isSuccess ? t('snapshots.noPublished') : null}
          data={buildable.map((definition) => ({
            value: definition.code,
            label: `${localized(definition.nameL10n) || definition.code} (${definition.code})`,
          }))}
          value={code}
          onChange={setCode}
          data-autofocus
        />

        {/* ⛔ «Опублікованих немає» — твердження про ДАНІ; на відмові (і поки
            перелік їде) його казати не можна. */}
        <ErrorAlert error={reportDefs.error} onRetry={retryReferences} />

        {reportDefs.isSuccess && buildable.length === 0 && (
          <Text size="xs" c="dimmed" mt="xs">
            {t('snapshots.noPublished')}
          </Text>
        )}

        <NumberInput
          mt="sm"
          label={t('documents.period')}
          value={buildPeriod}
          onChange={(value) => setBuildPeriod(typeof value === 'number' ? value : buildPeriod)}
        />

        {/* ⛔ Поля параметрів — лише коли їх СПРАВДІ оголошено. «Прочитати не
            вдалося» не малює порожньої секції: порожня секція читається як
            «параметрів немає», тобто як протилежне твердження. */}
        {parameters.kind === 'declared' && parameters.items.length > 0 && (
          <Stack gap="xs" mt="sm">
            <Text size="sm" fw={600}>
              {t('snapshots.parameters')}
            </Text>

            {parameters.items.map((declaration) => (
              <ParameterField
                key={declaration.code}
                declaration={declaration}
                value={values[declaration.code] ?? null}
                onChange={(value) => setValue(declaration.code, value)}
              />
            ))}
          </Stack>
        )}

        {/* ⛔ `AsyncBoundary` тут навмисно НЕ використано: її `<Title order={4}>`
            всередині модалки рве `heading-order` і валить гейт `a11y`. Причина
            блокування — текстом, і вона названа, а не «побудова недоступна». */}
        {blockedReason !== null && (
          <Text size="xs" c="statusError" mt="sm" role="alert">
            {t(blockedReason)}
          </Text>
        )}

        <Text size="xs" c="dimmed" mt="sm">
          {t('snapshots.buildHint')}
        </Text>

        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setBuilding(false)}>
            {t('common.cancel')}
          </Button>
          <Button
            disabled={code === null || blockedReason !== null}
            loading={build.isPending}
            onClick={() => build.mutate()}
          >
            {t('snapshots.build')}
          </Button>
        </Group>
      </Modal>

      <ReportDefinitionsModal
        opened={managing}
        onClose={() => setManaging(false)}
        definitions={reportDefs.data ?? []}
      />

      {viewing !== null && (
        <Suspense fallback={null}>
          <SnapshotRowsModal snapshotId={viewing} onClose={() => setViewing(null)} />
        </Suspense>
      )}
    </>
  );
}

type SnapshotVerifyResponse = components['schemas']['SnapshotVerifyResponse'];

/**
 * Поле одного параметра звіту (`R6`).
 *
 * ⛔ Вид поля диктує ОГОЛОШЕННЯ, а не здогад: сервер приведення не робить
 * (рядок `"5"` у параметр `Number` — відмова), тож єдине текстове поле на всі
 * типи гарантувало б `422` для кожного числа й кожної дати.
 *
 * ⛔ Дата — `DateInput`, а не `<TextInput type="date">`: нативне поле бере
 * формат з ОС, а не з локалі продукту (`D15-09`), і той самий запис читався б
 * як третє вересня в одного користувача і як дев'яте березня в іншого.
 */
function ParameterField(props: {
  declaration: ReportParameterDeclaration;
  value: ParameterValue;
  onChange: (value: ParameterValue) => void;
}): JSX.Element {
  const { declaration, value } = props;

  // ⚠ Обов'язковість названа СЛОВОМ, а не самою зірочкою: зірочка поруч із
  // іменем параметра, яке придумав методолог, читається як частина імені.
  const description = declaration.required ? t('snapshots.parameterRequired') : undefined;

  if (declaration.type === 'Boolean') {
    return (
      <Switch
        label={declaration.code}
        description={description}
        checked={value === true}
        onChange={(event) => props.onChange(event.currentTarget.checked)}
      />
    );
  }

  if (declaration.type === 'Number') {
    return (
      <NumberInput
        label={declaration.code}
        description={description}
        withAsterisk={declaration.required}
        value={typeof value === 'number' ? value : ''}
        onChange={(next) => props.onChange(typeof next === 'number' ? next : '')}
      />
    );
  }

  if (declaration.type === 'Date') {
    return (
      // ⚠ `fallback={null}`: заглушка на місці одного поля форми блимала б
      // рівно ті мілісекунди, за які їде чанк, і читалася б як збій. Побудову
      // це не відкриває — обов'язкове поле лишається порожнім, доки поле не
      // змонтоване, а порожнє обов'язкове тримає кнопку заблокованою.
      <Suspense fallback={null}>
        <DateInput
          label={declaration.code}
          description={description}
          withAsterisk={declaration.required}
          // Формат заданий кодом — однозначний і не залежить від локалі браузера.
          valueFormat="YYYY-MM-DD"
          clearable
          value={value instanceof Date ? value : null}
          onChange={(next) => props.onChange(next)}
        />
      </Suspense>
    );
  }

  return (
    <TextInput
      label={declaration.code}
      description={description}
      withAsterisk={declaration.required}
      value={typeof value === 'string' ? value : ''}
      onChange={(event) => props.onChange(event.currentTarget.value)}
    />
  );
}

/**
 * Дія «Перевірити» й її підсумок у рядку зрізу (BE-17).
 *
 * ⛔ При розбіжності показуються ОБИДВІ суми цілком: «не збігається» без
 * чисел — це твердження, яке нема чим ні підтвердити, ні передати далі.
 */
function VerifyCell(props: {
  result: SnapshotVerifyResponse | undefined;
  loading: boolean;
  onVerify: () => void;
}): JSX.Element {
  const { result } = props;

  return (
    <>
      <Group gap="xs" wrap="nowrap">
        <Button size="compact-xs" variant="default" loading={props.loading} onClick={props.onVerify}>
          {t('snapshots.verify')}
        </Button>
        {result !== undefined && (
          <Badge
            variant="light"
            miw="fit-content"
            color={result.matches ? 'statusSuccess' : 'statusError'}
          >
            {t(result.matches ? 'snapshots.verifyMatch' : 'snapshots.verifyMismatch')}
          </Badge>
        )}
      </Group>
      {/* ⚠ Примітка, а не тривога: вміст НЕ змінено, інший лише формат суми
          (зріз побудовано до BE-17). Прибрати разом зі старим форматом. */}
      {result !== undefined && result.matches && result.matchedFormat === 'legacy' && (
        <Text size="xs" mt="xs" c="dimmed">
          {t('snapshots.verifyLegacy')}
        </Text>
      )}
      {result !== undefined && !result.matches && (
        <Text size="xs" mt="xs" style={{ wordBreak: 'break-all' }}>
          {t('snapshots.verifyStored', { hash: result.stored || '—' })}
          <br />
          {t('snapshots.verifyActual', { hash: result.actual })}
        </Text>
      )}
    </>
  );
}

/**
 * Поточний період як `Year*100 + Sequence` (R-A6).
 *
 * ⚠ Лише початкове значення поля: справжній поточний період задає календар
 * проєкту, і в квартальному номер місяця йому не дорівнює.
 */
function currentPeriodKey(): number {
  const now = new Date();

  return now.getUTCFullYear() * 100 + (now.getUTCMonth() + 1);
}

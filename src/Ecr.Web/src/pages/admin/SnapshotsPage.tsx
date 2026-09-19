import { useEffect, useRef, useState, type JSX } from 'react';
import { Badge, Button, Group, Modal, NumberInput, ScrollArea, Select, Table, Text } from '@mantine/core';
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
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { Timestamp } from '@/shared/ui/Timestamp';
import { showApiError, showDone } from '@/shared/ui/notify';
import { useUrlNumber } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';

/**
 * Зрізи регламентної звітності.
 *
 * ⛔ Це **не** екран звітів. Звітність лишається в SSRS (`D-52`), і маршрутів
 * `/reports/*` в інтерфейсі немає навмисно. Наша межа — `rpt.*`: незмінний
 * зріз, який SSRS читає. Тут його будують і бачать, на яких даних він
 * побудований.
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
  const buildable = (reportDefs.data ?? []).filter(
    (definition) =>
      definition.isActive && definition.versions.some((v) => v.status === 'Published'),
  );

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
    mutationFn: () =>
      apiEnqueue(`/api/v1/reports/${encodeURIComponent(code ?? '')}/build`, {
        projectId: projectId ?? 0,
        periodKey: buildPeriod,
      } satisfies BuildSnapshotRequest),
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

            {can(session.data, 'Report.EditDefinition') && (
              <Button size="xs" variant="default" onClick={() => setManaging(true)}>
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
                  <Table.Td>{snapshot.rowCount}</Table.Td>
                  <Table.Td>
                    {/* ⚠ Статус зрізу успадковується від стану даних: зріз
                        `Draft` існує, але регулятор його не бачить —
                        вʼюха `rpt.v_*` віддає лише `Approved` і `Submitted`
                        (`ФВ-10.11`). */}
                    {/* ⛔ UI-аудит-пас 8, lane6, п.9: той самий дефект, що вже
                        виправлено для бейджа стану `PeriodsPage.tsx`
                        (`miw="fit-content"` вище в тому файлі) — без нього
                        `table-layout: auto` дає стовпцю ширину з того, що
                        РЕНДЕРИТЬСЯ, а `.mantine-Badge-label`'s власний
                        `overflow:hidden; text-overflow:ellipsis` дозволяє
                        бейджу «поміститись» у будь-яку ширину замість того,
                        щоб змусити таблицю прокручуватись. Наслідок —
                        «DRAFT» ставало нечитабельним «D…» на типовій ширині
                        вікна. */}
                    <Badge variant="light" miw="fit-content">
                      {snapshot.status}
                    </Badge>
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
          nothingFoundMessage={t('snapshots.noPublished')}
          data={buildable.map((definition) => ({
            value: definition.code,
            label: `${localized(definition.nameL10n) || definition.code} (${definition.code})`,
          }))}
          value={code}
          onChange={setCode}
          data-autofocus
        />

        {buildable.length === 0 && (
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

        <Text size="xs" c="dimmed" mt="sm">
          {t('snapshots.buildHint')}
        </Text>

        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setBuilding(false)}>
            {t('common.cancel')}
          </Button>
          <Button
            disabled={code === null}
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
    </>
  );
}

type SnapshotVerifyResponse = components['schemas']['SnapshotVerifyResponse'];

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

import { useCallback, useEffect, useRef, useState, type JSX } from 'react';
import { Alert, Anchor, Button, Card, Checkbox, Group, Progress, Stack, Table, Text } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useParams } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type { JobStatus } from '@/api/types';
import { isCalculationResultsQuery } from '@/features/methodologies/calculationResultsKey';
import { PollMs, badgeStateOf } from '@/features/workflow/jobFollow';
import { humanizeJobId } from '@/features/workflow/jobLabel';
import { useFocusAfterBusy } from '@/shared/a11y/focus';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { ReasonModal } from '@/shared/ui/ReasonModal';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { recalculateImpacted, registryImpact, type RegistryImpactItem, type RegistryImpactResponse } from './api';

/** Право, якого вимагає `POST …/recalculate-impacted` (`RecalculateImpactedHandler.Permission`). */
export const RecalculatePermission = 'Calculation.Recalculate';

/** Адреса сторінки впливу довідника. */
export function registryImpactPath(code: string): string {
  return `/admin/registries/${encodeURIComponent(code)}/impact`;
}

/** Код методології з позначки `methodology:<код>`; інша позначка — як є. */
export function viaLabel(via: string): string {
  return via.startsWith('methodology:') ? via.slice('methodology:'.length) : via;
}

/**
 * Чи опитувати задачу далі.
 *
 * ⛔ Батько-розклад (P4) уже `Succeeded`, коли дочірні ще рахуються (`effectiveState = FannedOut`):
 * спільне правило `pollInterval` дивиться лише на `state` і зупинилося б на «виконано» замість
 * «ще не пораховано» — той самий дефект, який `JobsPage` уже пройшов.
 */
export function impactPollInterval(status: JobStatus | undefined): number | false {
  if (status === undefined) return PollMs;

  return status.state === 'Queued' || status.state === 'Running' || status.effectiveState === 'FannedOut'
    ? PollMs
    : false;
}

/**
 * Документ у ТОМУ періоді, який зачеплено.
 *
 * ⚠ Без `periodKey` сторінка документа відкрила б поточний період, а не той, чиї результати застаріли.
 */
export function documentHref(item: Pick<RegistryImpactItem, 'documentId' | 'periodKey'>): string {
  return `/documents/${String(item.documentId)}?periodKey=${String(item.periodKey)}`;
}

/** Ключ запиту переліку зачеплених — окремий від даних довідника. */
function impactKey(code: string): readonly unknown[] {
  return ['registry-impact', code];
}

/**
 * Стан поставленої задачі перерахунку: стан, прогрес, повідомлення й підсумок розкладу.
 *
 * ⛔ «Стан прочитати не вдалося» — не «виконується»: без права бачити задачу (`GET /jobs/{id}` —
 * автор або `System.ViewHealth`) показуємо ідентифікатор і причину, а не вічний прогрес.
 */
/** Довше за перехід Mantine-модалки (≈200 мс): після нього повернення фокуса вже відбулося. */
const FocusAfterModalMs = 300;

function ImpactJob({
  jobId,
  canOpenJobs,
  onSettled,
}: {
  readonly jobId: string;
  readonly canOpenJobs: boolean;
  readonly onSettled: () => void;
}): JSX.Element {
  const job = useQuery({
    queryKey: ['job', jobId],
    queryFn: () => apiFetch<JobStatus>(`/api/v1/jobs/${encodeURIComponent(jobId)}`),
    refetchInterval: (query) => impactPollInterval(query.state.data),
    retry: false,
  });

  const status = job.data;
  const fan = status?.fanOut;
  const settled = status !== undefined && impactPollInterval(status) === false;

  // ⛔ Перерахунок завершився (разом із дочірніми) — документи вже свіжі, і сервер їх більше не
  // повертає. Без повторного читання перелік показував би вже перераховані документи як зачеплені.
  useEffect(() => {
    if (settled) onSettled();
  }, [settled, onSettled]);

  // ⛔ Після «Перерахувати» діалог причини закривається, а кнопка сторінки на час запиту `loading`
  // (= `disabled`) — фокус падав на `<body>`, і про поставлену задачу читач не дізнавався. Тепер фокус — на
  // заголовок картки задачі: він озвучується, і наступний `Tab` веде до посилання на задачу.
  const heading = useRef<HTMLParagraphElement>(null);

  // ⚠ Із затримкою: діалог причини (`ReasonModal`) повертає фокус на кнопку сторінки з власним таймером
  // переходу, і при швидкій відповіді сервера цей таймер спрацьовував ПІСЛЯ фокуса на заголовку й
  // забирав його назад (виявлено при зведенні з `dev/integration`, де діалог уже за `import()`).
  useEffect(() => {
    const timer = window.setTimeout(() => heading.current?.focus(), FocusAfterModalMs);
    return () => window.clearTimeout(timer);
  }, [jobId]);

  return (
    <Card withBorder data-impact-job={jobId}>
      <Stack gap="xs">
        <Group justify="space-between">
          <Text ref={heading} tabIndex={-1} fw={600} data-impact-job-heading="">
            {t('registries.impact.jobQueued', { jobId: humanizeJobId(jobId) })}
          </Text>
          {status !== undefined && <StatusBadge kind="job" state={badgeStateOf(status)} />}
        </Group>

        {job.isError && (
          <Text size="sm" c="dimmed" data-impact-job-state="unknown">
            {t('registries.impact.jobUnreadable')}
          </Text>
        )}

        {status !== undefined && (
          <>
            <Progress
              value={status.percent}
              animated={status.state === 'Running'}
              aria-label={t('registries.impact.progress')}
            />
            {status.message !== null && <Text size="sm">{status.message}</Text>}
            {fan !== null && fan !== undefined && (
              <Text size="sm" data-impact-fanout={status.effectiveState ?? ''}>
                {t('jobs.fanOutProgress', { total: fan.total, done: fan.succeeded, failed: fan.failed })}
              </Text>
            )}
          </>
        )}

        {/* ⚠ Екран черги відкривається лише з `System.ViewHealth`: посилання без права — шлях у 403. */}
        {canOpenJobs && (
          <Anchor component={Link} to={`/admin/jobs?id=${encodeURIComponent(jobId)}`} size="sm">
            {t('registries.impact.openJob')}
          </Anchor>
        )}
      </Stack>
    </Card>
  );
}

/**
 * Вплив правки довідника (RT-25, FEATURE-REGISTRY-TABLES §5.10): документи ВІДКРИТИХ періодів, чиї
 * результати пораховано методологією, що читає довідник, і постановка їх перерахунку.
 *
 * ⛔ Перерахунок ніколи не ставиться сам (`R-14`): одна правка складу — десятки збережень, а бюджет
 * прогону обмежений (`D-63`). Тому дія — кнопка людини з обов'язковою причиною.
 *
 * ⛔ Закритих періодів тут немає і бути не може (`D-39`): сервер їх не повертає, а пропонувати
 * їх означало б обіцяти перерахунок, якого не буде.
 *
 * ⚠ Нічого не обрано — перераховуються всі доступні (`documentIds: null`); обрано — лише вони.
 */
export function RegistryImpactPage(): JSX.Element {
  const { code = '' } = useParams<{ code: string }>();
  const session = useSession();
  const client = useQueryClient();
  const [selected, setSelected] = useState<ReadonlySet<number>>(new Set());
  const [asking, setAsking] = useState(false);
  const [jobId, setJobId] = useState<string | null>(null);

  const impact = useQuery({
    queryKey: impactKey(code),
    queryFn: () => registryImpact(code),
    enabled: code !== '',
  });

  const refreshAfterRecalculation = useCallback((): void => {
    void client.invalidateQueries({ queryKey: impactKey(code) });
    void client.invalidateQueries({ predicate: isCalculationResultsQuery });
  }, [client, code]);

  // ⛔ L9-35: вибір живе довше за перелік — після перерахунку чи чужої правки обраний документ зникає
  // з відповіді сервера, а його id лишався у `selected` і йшов у наступний запит (422). Тому і підпис
  // кнопки, і тіло запиту рахують лише ті обрані, що є в поточному переліку.
  const effective = [...selected]
    .filter((id) => impact.data?.items.some((item) => item.documentId === id) === true)
    .sort((a, b) => a - b);

  const recalculate = useMutation({
    mutationFn: (reason: string) =>
      recalculateImpacted(code, { documentIds: effective.length === 0 ? null : effective, reason }),
    onSuccess: (accepted) => {
      setJobId(accepted.jobId);
      // ⚠ Поставлені документи перераховуються й зникнуть із переліку: вибір не переживає постановку.
      setSelected(new Set());
      // ⚠ Перелік зачеплених і банер «довідник змінено» в панелі результатів читають кеш: без
      // інвалідації вони показують стан ДО постановки перерахунку (RT-25). Ще раз — коли задача
      // завершиться (`ImpactJob.onSettled`): лише тоді перераховані документи зникають із переліку.
      refreshAfterRecalculation();
    },
  });

  // ⚠ Відмова постановки: фокус назад на кнопку (поруч із `ErrorAlert`), а не на `<body>`.
  const recalculateFocus = useFocusAfterBusy(recalculate.isPending);

  const canRecalculate = can(session.data, RecalculatePermission);
  const canOpenJobs = can(session.data, 'System.ViewHealth');
  const hasItems = (impact.data?.items.length ?? 0) > 0;

  function toggle(documentId: number, on: boolean): void {
    setSelected((current) => {
      const next = new Set(current);
      if (on) next.add(documentId);
      else next.delete(documentId);
      return next;
    });
  }

  return (
    <>
      <PageHeader
        title={t('registries.impact.title')}
        meta={code}
        actions={
          canRecalculate && (
            <Button
              ref={recalculateFocus.ref}
              size="xs"
              disabled={!hasItems}
              loading={recalculate.isPending}
              onClick={() => setAsking(true)}
              data-impact-recalculate=""
            >
              {effective.length === 0
                ? t('registries.impact.recalculateAll')
                : t('registries.impact.recalculateSelected', { count: effective.length })}
            </Button>
          )
        }
      />

      <Stack gap="md">
        <Text size="sm" c="dimmed">
          {t('registries.impact.hint')}
        </Text>

        {/* ⛔ `L10`: відмова постановки видима з кодом і текстом сервера, а не тостом, що зникає. */}
        <ErrorAlert error={recalculate.error} />

        {jobId !== null && (
          <ImpactJob jobId={jobId} canOpenJobs={canOpenJobs} onSettled={refreshAfterRecalculation} />
        )}

        <AsyncBoundary<RegistryImpactResponse>
          isPending={impact.isPending}
          error={impact.error}
          data={impact.data}
          isEmpty={(page) => page.items.length === 0}
          emptyTitle={t('registries.impact.empty')}
          emptyHint={t('registries.impact.emptyHint')}
          skeleton="table"
          onRetry={() => void impact.refetch()}
        >
          {(page) => (
            <Stack gap="xs">
              <Text size="sm" data-impact-count="">
                {t('registries.impact.count', { shown: page.items.length, total: page.total })}
              </Text>

              {/* ⚠ Вибірка вперлась у стелю сервера: справжніх документів більше, ніж `total`. */}
              {page.truncated && (
                <Alert color="statusWarning" data-impact-truncated="">
                  {t('registries.impact.truncated')}
                </Alert>
              )}

              <Table striped withTableBorder aria-label={t('registries.impact.title')}>
                <Table.Thead>
                  <Table.Tr>
                    {canRecalculate && <Table.Th aria-label={t('registries.impact.select')} />}
                    <Table.Th>{t('registries.impact.document')}</Table.Th>
                    <Table.Th>{t('registries.impact.period')}</Table.Th>
                    <Table.Th>{t('periods.state')}</Table.Th>
                    <Table.Th>{t('registries.impact.via')}</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {page.items.map((item: RegistryImpactItem) => (
                    <Table.Tr key={`${String(item.documentId)}:${String(item.periodKey)}`} data-impact-row={item.documentId}>
                      {canRecalculate && (
                        <Table.Td>
                          <Checkbox
                            aria-label={t('registries.impact.selectDocument', { document: item.businessKey })}
                            checked={selected.has(item.documentId)}
                            onChange={(event) => toggle(item.documentId, event.currentTarget.checked)}
                          />
                        </Table.Td>
                      )}
                      <Table.Td>
                        <Anchor
                          component={Link}
                          to={documentHref(item)}
                        >
                          {item.businessKey}
                        </Anchor>
                      </Table.Td>
                      <Table.Td>{item.periodKey}</Table.Td>
                      <Table.Td>
                        <StatusBadge kind="period" state={item.periodState} />
                      </Table.Td>
                      <Table.Td>{item.via.map(viaLabel).join(', ')}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </Stack>
          )}
        </AsyncBoundary>
      </Stack>

      <ReasonModal
        opened={asking}
        title={t('registries.impact.recalculateTitle')}
        label={t('workflow.reason')}
        description={t('registries.impact.recalculateHint')}
        confirmLabel={t('registries.impact.recalculateConfirm')}
        isPending={recalculate.isPending}
        onConfirm={(reason) => {
          setAsking(false);
          recalculateFocus.arm();
          recalculate.mutate(reason);
        }}
        onClose={() => setAsking(false)}
      />
    </>
  );
}

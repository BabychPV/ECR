import { useState, type JSX } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import { formatDateTime } from '@/shared/format';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { Banner } from '@/shared/ui/Banner';
import { showApiError, showDone } from '@/shared/ui/notify';
import { flertSyncStatus, syncFlertNow, type FlertSyncRun } from './api';

/** Як часто перечитувати стан, поки синк у черзі чи виконується (D-476 F3). */
export const FlertSyncPollMs = 5_000;

/** Скільки після «Синхронізувати зараз» чекати, що прогін з'явиться в журналі, перш ніж перестати опитувати. */
export const FlertSyncWaitMs = 120_000;

/** Людська назва стану прогону. Ключі — літерали: сторож каталогу шукає їх у коді. */
function statusText(status: string): string {
  switch (status) {
    case 'Running':
      return t('flert.sync.status.running');
    case 'Succeeded':
      return t('flert.sync.status.succeeded');
    case 'Degraded':
      return t('flert.sync.status.degraded');
    case 'Failed':
      return t('flert.sync.status.failed');
    default:
      return status;
  }
}

/** Чому знімок не застосовано — людською мовою; невідома причина — без підпису (лічильники й стан лишаються). */
function reasonText(reason: string | null | undefined): string | null {
  switch (reason) {
    case 'disabled':
      return t('flert.sync.reason.disabled');
    case 'emptyResult':
      return t('flert.sync.reason.emptyResult');
    case 'deactivationLimit':
      return t('flert.sync.reason.deactivationLimit');
    case 'mappingErrors':
      return t('flert.sync.reason.mappingErrors');
    case 'truncated':
      return t('flert.sync.reason.truncated');
    case 'registryIncompatible':
      return t('flert.sync.reason.registryIncompatible');
    case 'writerRejected':
      return t('flert.sync.reason.writerRejected');
    default:
      return null;
  }
}

function tone(run: FlertSyncRun | null | undefined): 'info' | 'success' | 'warning' | 'danger' {
  switch (run?.status) {
    case 'Succeeded':
      return run.reason === 'disabled' ? 'info' : 'success';
    case 'Degraded':
      return 'warning';
    case 'Failed':
      return 'danger';
    default:
      return 'info';
  }
}

/**
 * Стан синку складу газу FLERT на сторінці довідника-дзеркала (`STREAM`, `STREAM_CASE`, `GAS_COMPOSITION`, D-476 F3):
 * останній прогін (час, стан, лічильники, причина) і «Синхронізувати зараз».
 *
 * ⚠ Видно лише з правом `Integration.View`/`Integration.Manage` і лише на довідниках, які сервер назвав дзеркалом
 * (`registryCodes`); кнопка — лише з `Integration.Manage`. Поки прогін виконується або щойно поставлений — стан
 * перечитується раз на `FlertSyncPollMs`.
 */
export function FlertSyncPanel({ registryCode }: { readonly registryCode: string }): JSX.Element | null {
  const session = useSession();
  const client = useQueryClient();
  const canView = can(session.data, 'Integration.View') || can(session.data, 'Integration.Manage');
  const canSync = can(session.data, 'Integration.Manage');
  const [queuedAt, setQueuedAt] = useState<number | null>(null);

  const status = useQuery({
    queryKey: queryKeys.registries.flertSync(),
    queryFn: flertSyncStatus,
    enabled: canView,
    refetchInterval: (query) => {
      const last = query.state.data?.lastRun;
      if (last?.status === 'Running') return FlertSyncPollMs;
      // Щойно поставлений синк ще не в журналі: чекаємо прогону, новішого за постановку, але не довше FlertSyncWaitMs.
      const waiting =
        queuedAt !== null &&
        Date.now() - queuedAt < FlertSyncWaitMs &&
        (last === null || last === undefined || Date.parse(last.startedAt) < queuedAt - 1_000);
      return waiting ? FlertSyncPollMs : false;
    },
  });

  const sync = useMutation({
    mutationFn: syncFlertNow,
    onSuccess: async () => {
      setQueuedAt(Date.now());
      showDone(t('flert.sync.queued'));
      await client.invalidateQueries({ queryKey: queryKeys.registries.flertSync() });
    },
    onError: showApiError,
  });

  const data = status.data;
  if (!canView || data === undefined) return null;
  if (!data.registryCodes.some((code) => code.toUpperCase() === registryCode.toUpperCase())) return null;

  const run = data.lastRun;
  const lines: string[] = [];
  if (run === null || run === undefined) {
    lines.push(t('flert.sync.never'));
  } else {
    lines.push(t('flert.sync.lastRun', { time: formatDateTime(run.startedAt), status: statusText(run.status) }));
    lines.push(
      t('flert.sync.counts', {
        rows: run.sourceRows,
        added: run.added,
        updated: run.updated,
        deactivated: run.deactivated,
        reactivated: run.reactivated,
      }),
    );
    const reason = reasonText(run.reason);
    if (reason !== null) lines.push(reason);
  }
  if (!data.enabled) lines.push(t('flert.sync.disabled'));

  return (
    <Banner
      testId="flert-sync"
      tone={tone(run)}
      title={t('flert.sync.title')}
      text={
        <span>
          {lines.map((line) => (
            <span key={line} style={{ display: 'block' }}>
              {line}
            </span>
          ))}
        </span>
      }
      actions={
        canSync
          ? [{ label: sync.isPending ? t('flert.sync.queuing') : t('flert.sync.now'), onClick: () => sync.mutate() }]
          : undefined
      }
    />
  );
}

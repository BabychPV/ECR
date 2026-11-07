import { useState, type JSX } from 'react';
import { Badge, Button, Group, Modal, Select, Stack, Text, Textarea } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import type { PeriodCalendarDto } from '@/api/types';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { formatPeriodKey } from '@/shared/format';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showApiError, showDone } from '@/shared/ui/notify';
import { projectsQuery } from './allProjects';

// ⚠ Прямо зі схеми: `api/types.ts` — спільний файл.
type CurrentPeriodSettingDto = components['schemas']['CurrentPeriodSettingDto'];
type CurrentPeriodSettingRequest = components['schemas']['CurrentPeriodSettingRequest'];

/** Ключ кешу налаштування «поточний період» проєкту. */
export const currentPeriodSettingKey = (projectId: number): readonly unknown[] => [
  'projects',
  projectId,
  'current-period-setting',
];

/**
 * ✎ Period clarity (людина 07.11, п. c): поточний період — у налаштуваннях проєкту, з видимим РЕЖИМОМ:
 * «Automatic by date» або «Pinned to <місяць>» (з причиною), дії «Pin…» і «Back to automatic».
 *
 * ⛔ `CurrentPeriod` — підказка інтерфейсу, а НЕ правило доступу (`D-77`): закріплення не відкриває і не закриває
 * місяць. Причина закріплення обов'язкова — через місяць «чому в нас досі січень» має мати відповідь.
 *
 * ⚠ `canConfigure` — право `Period.Configure` і грант Manage на проєкт (F-19), як і на старому місці (Periods):
 * без них дії не показуються, а сервер однаково відмовив би.
 */
export function CurrentPeriodSetting({
  projectId,
  calendar,
  canConfigure,
}: {
  readonly projectId: number;
  readonly calendar: PeriodCalendarDto | undefined;
  readonly canConfigure: boolean;
}): JSX.Element {
  const queryClient = useQueryClient();
  const [pinning, setPinning] = useState(false);
  const [pinPeriodId, setPinPeriodId] = useState<string | null>(null);
  const [reason, setReason] = useState('');

  const setting = useQuery({
    queryKey: currentPeriodSettingKey(projectId),
    queryFn: () => apiFetch<CurrentPeriodSettingDto>(`/api/v1/projects/${String(projectId)}/settings/current-period`),
  });

  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: currentPeriodSettingKey(projectId) });
    await queryClient.invalidateQueries({ queryKey: ['periods', projectId] });
    await queryClient.invalidateQueries({ queryKey: projectsQuery.queryKey });
  };

  const pin = useMutation({
    mutationFn: (target: { periodId: number; reason: string }) =>
      apiFetch<CurrentPeriodSettingDto>(`/api/v1/projects/${String(projectId)}/settings/current-period`, {
        method: 'PUT',
        body: JSON.stringify({
          mode: 'Pinned',
          pinnedPeriodId: target.periodId,
          reason: target.reason,
        } satisfies CurrentPeriodSettingRequest),
      }),
    onSuccess: async () => {
      await refresh();
      setPinning(false);
      showDone(t('periods.pinned'));
    },
    onError: showApiError,
  });

  const backToAuto = useMutation({
    mutationFn: () =>
      apiFetch<CurrentPeriodSettingDto>(`/api/v1/projects/${String(projectId)}/settings/current-period/pin`, {
        method: 'DELETE',
      }),
    onSuccess: async () => {
      await refresh();
      showDone(t('projectSettings.backedToAuto'));
    },
    onError: showApiError,
  });

  // ФВ-14.26: спінер — із затримкою, не на кожен швидкий запит.
  const pinLoading = usePendingLoading(pin.isPending);
  const backLoading = usePendingLoading(backToAuto.isPending);

  const label = (key: number | null): string =>
    key === null ? t('projectSettings.noCurrent') : formatPeriodKey(key, calendar?.periodKind) || String(key);

  const openPin = (): void => {
    // ⚠ Скидання на відкритті: причина попереднього закріплення не має підписати нове.
    setPinPeriodId(null);
    setReason('');
    setPinning(true);
  };

  if (setting.error !== null) {
    return <ErrorAlert error={setting.error} onRetry={() => void setting.refetch()} />;
  }

  const data = setting.data;
  const pinned = data?.mode === 'Pinned';

  return (
    <Stack gap="xs" data-current-period-mode={data?.mode ?? ''}>
      {data !== undefined && (
        <>
          <Group gap="xs">
            <Text size="sm">{t('projectSettings.currentIs', { period: label(data.currentPeriodKey) })}</Text>
            <Badge
              variant={pinned ? 'outline' : 'light'}
              {...(pinned ? { color: 'statusWarning' } : {})}
              data-current-period-badge=""
            >
              {pinned
                ? t('projectSettings.modePinned', { period: label(data.currentPeriodKey) })
                : t('projectSettings.modeAuto')}
            </Badge>
          </Group>
          {pinned && data.pinnedReason !== null && (
            <Text size="xs" c="dimmed">
              {t('projectSettings.pinnedReason', { reason: data.pinnedReason })}
            </Text>
          )}
          {pinned && (
            <Text size="xs" c="dimmed">
              {t('projectSettings.automaticWouldBe', { period: label(data.automaticPeriodKey) })}
            </Text>
          )}
        </>
      )}

      {canConfigure && data !== undefined && (
        <Group gap="xs">
          <Button variant="default" onClick={openPin}>
            {t('projectSettings.pin')}
          </Button>
          {pinned && (
            <Button
              variant="default"
              loading={backLoading}
              disabled={backToAuto.isPending}
              onClick={() => backToAuto.mutate()}
            >
              {t('projectSettings.backToAuto')}
            </Button>
          )}
        </Group>
      )}

      <Modal opened={pinning} onClose={() => setPinning(false)} title={t('periods.pin')}>
        <Stack gap="xs">
          <Select
            required
            label={t('projectSettings.pinPeriod')}
            data={(calendar?.periods ?? []).map((period) => ({
              value: String(period.id),
              label: label(period.periodKey),
            }))}
            value={pinPeriodId}
            onChange={setPinPeriodId}
            data-autofocus
          />
          <Textarea
            required
            label={t('workflow.reason')}
            description={t('periods.pinHint')}
            value={reason}
            onChange={(event) => setReason(event.currentTarget.value)}
            minRows={2}
            autosize
          />
          <Group justify="flex-end" mt="md">
            <Button variant="default" onClick={() => setPinning(false)}>
              {t('common.cancel')}
            </Button>
            <Button
              disabled={pinPeriodId === null || reason.trim().length === 0 || pin.isPending}
              loading={pinLoading}
              onClick={() => {
                if (pinPeriodId !== null) pin.mutate({ periodId: Number(pinPeriodId), reason: reason.trim() });
              }}
            >
              {t('periods.pin')}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}

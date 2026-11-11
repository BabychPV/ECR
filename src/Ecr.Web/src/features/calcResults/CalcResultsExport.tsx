import { useEffect, useRef, useState, type JSX } from 'react';
import { Anchor, Button, Group, Modal, SegmentedControl, Stack, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { JobStatus } from '@/api/types';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { outcomeOf, pollInterval } from '@/features/workflow/jobFollow';
import { language, t } from '@/shared/i18n';
import { notificationCloseButtonProps, showApiError } from '@/shared/ui/notify';
import { errorCodeText } from '@/shared/ui/problemText';
import { enqueueCalculationYearExport, exportDownloadHref, type ExportLayout } from './api';

interface CalcResultsExportProps {
  readonly opened: boolean;
  readonly onClose: () => void;
  readonly documentId: number;
  readonly documentKey: string;
  readonly year: number;
  /** Виходи, що зараз проходять фільтри. */
  readonly filteredRowIds: readonly string[];
  /** Чи фільтри щось відсікають (інакше «As filtered» = «Whole document»). */
  readonly filtered: boolean;
}

/**
 * Діалог «Export to Excel» огляду року: рядки (як відфільтровано / увесь документ) × розкладка (місяці колонками /
 * довгий перелік). Будує книгу фоном та сама задача, що й експорт документа; готовий файл — тостом із посиланням
 * (і в «My tasks»), як у `ExportButton`.
 */
export function CalcResultsExport({
  opened,
  onClose,
  documentId,
  documentKey,
  year,
  filteredRowIds,
  filtered,
}: CalcResultsExportProps): JSX.Element {
  const [scope, setScope] = useState<'filtered' | 'all'>('filtered');
  const [layout, setLayout] = useState<ExportLayout>('pivot');
  const [jobId, setJobId] = useState<string | null>(null);

  const start = useMutation({
    mutationFn: () =>
      enqueueCalculationYearExport(documentId, {
        year,
        layout,
        rowIds: scope === 'filtered' && filtered ? [...filteredRowIds] : null,
        language: language(),
      }),
    onSuccess: (job) => {
      setJobId(job.jobId);
      onClose();
      notifications.show({ id: `cr-export-${job.jobId}`, message: t('calcResults.exportBuilding'), loading: true });
    },
    onError: showApiError,
  });

  // ФВ-14.26: спінер — лише коли постановка справді затяглася.
  const startLoading = usePendingLoading(start.isPending);

  const job = useQuery({
    queryKey: ['job', jobId],
    queryFn: () => apiFetch<JobStatus>(`/api/v1/jobs/${encodeURIComponent(jobId ?? '')}`),
    enabled: jobId !== null,
    refetchInterval: (query) => pollInterval(query.state.data?.state),
    retry: false,
  });
  const outcome = jobId === null ? null : outcomeOf(job.data?.state, job.isError);

  // Повідомлення — один раз на задачу, а не на кожен рендер (той самий захист, що в `ExportButton`).
  const reported = useRef<string | null>(null);
  useEffect(() => {
    if (jobId === null || outcome === null || outcome === 'running' || reported.current === jobId) return;
    reported.current = jobId;
    notifications.hide(`cr-export-${jobId}`);
    const exportKey = job.data?.message ?? '';
    if (outcome === 'succeeded' && exportKey.length > 0) {
      notifications.show({
        id: `cr-export-ready-${jobId}`,
        color: 'statusSuccess',
        autoClose: false,
        closeButtonProps: notificationCloseButtonProps,
        message: (
          <Anchor size="sm" href={exportDownloadHref(documentId, exportKey)} download data-testid="cr-export-download">
            {t('calcResults.exportReady')}
          </Anchor>
        ),
      });
    }
    if (outcome === 'failed') {
      notifications.show({
        color: 'statusError',
        message: errorCodeText(job.data?.errorCode, t('calcResults.exportFailed')),
        closeButtonProps: notificationCloseButtonProps,
      });
    }
  }, [jobId, outcome, job.data?.message, job.data?.errorCode, documentId]);

  return (
    <Modal opened={opened} onClose={onClose} title={t('calcResults.exportTitle')} size="md" data-testid="cr-export-dialog">
      <Stack gap="md">
        <Text size="sm" c="dimmed">
          {t('calcResults.exportIntro', { document: documentKey, year })}
        </Text>
        <Stack gap="xs">
          <Text size="sm" fw={500} id="cr-exp-scope-label">
            {t('calcResults.exportRows')}
          </Text>
          <SegmentedControl
            aria-labelledby="cr-exp-scope-label"
            value={scope}
            onChange={(value) => setScope(value === 'all' ? 'all' : 'filtered')}
            data={[
              { value: 'filtered', label: t('calcResults.exportFiltered') },
              { value: 'all', label: t('calcResults.exportAll') },
            ]}
          />
        </Stack>
        <Stack gap="xs">
          <Text size="sm" fw={500} id="cr-exp-layout-label">
            {t('calcResults.exportLayout')}
          </Text>
          <SegmentedControl
            aria-labelledby="cr-exp-layout-label"
            value={layout}
            onChange={(value) => setLayout(value === 'long' ? 'long' : 'pivot')}
            data={[
              { value: 'pivot', label: t('calcResults.exportPivot') },
              { value: 'long', label: t('calcResults.exportLong') },
            ]}
          />
          <Text size="xs" c="dimmed">
            {t('calcResults.exportLongHint')}
          </Text>
        </Stack>
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            {t('calcResults.cancel')}
          </Button>
          <Button onClick={() => start.mutate()} loading={startLoading} disabled={start.isPending} data-testid="cr-export-go">
            {t('calcResults.exportGo')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

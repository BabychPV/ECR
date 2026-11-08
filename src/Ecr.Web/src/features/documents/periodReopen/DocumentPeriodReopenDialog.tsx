import '@mantine/dates/styles.css';
import { useState, type JSX } from 'react';
import { Button, Group, Modal, Stack, Text, Textarea } from '@mantine/core';
import { DateTimePicker } from '@mantine/dates';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { calendarLocale } from '@/shared/dates/calendarLocale';
import { formatPeriodKey } from '@/shared/format';
import { formatDateTime } from '@/shared/format/datetime';
import { language, t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showDone } from '@/shared/ui/notify';
import { postDocumentPeriodReopen } from './api';

/** Найбільша довжина причини — як `DocumentPeriodReopen.MaxReasonLength` на сервері. */
export const ReopenReasonMaxLength = 400;

export interface DocumentPeriodReopenDialogProps {
  readonly documentId: number;
  readonly periodKey: number;
  /** Строк за замовчуванням із сервера — кінець доби майданчика (UTC, ISO). */
  readonly defaultOpenUntil: string;
  readonly onClose: () => void;
}

/**
 * Діалог «Reopen January 2026 for this document…» (приймальна 08.11 (i1)): причина обов'язкова, «Open until» — за
 * замовчуванням кінець доби майданчика; текст прямо каже, що відкривається лише цей документ, а решта лишається
 * закритою.
 *
 * ⚠ Окремим чанком (`D-132`) разом із `@mantine/dates` і його стилями — сторінка документа їх не несе.
 * ⚠ Час — у поясі браузера (поле показує й надсилає момент; сервер зберігає UTC).
 */
export function DocumentPeriodReopenDialog({
  documentId,
  periodKey,
  defaultOpenUntil,
  onClose,
}: DocumentPeriodReopenDialogProps): JSX.Element {
  const queryClient = useQueryClient();
  const [reason, setReason] = useState('');
  const [until, setUntil] = useState<Date | null>(() => new Date(defaultOpenUntil));
  const period = formatPeriodKey(periodKey);

  const mutation = useMutation({
    mutationFn: () =>
      postDocumentPeriodReopen(documentId, {
        periodKey,
        reason: reason.trim(),
        openUntil: until === null ? null : until.toISOString(),
      }),
    onSuccess: async (view) => {
      // Стан винятку, документ (його ключі — під тим самим префіксом) і зрізи: сітка стає редагованою.
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.document.ofDocument(documentId) }),
        queryClient.invalidateQueries({ queryKey: queryKeys.slices.all() }),
      ]);
      showDone(t('document.reopen.done', { period, until: formatDateTime(view.openUntil) }));
      onClose();
    },
  });
  const loading = usePendingLoading(mutation.isPending);

  const reasonMissing = reason.trim().length === 0;
  const untilInvalid = until === null || until.getTime() <= Date.now();

  return (
    <Modal opened onClose={onClose} title={t('document.reopen.dialogTitle', { period })}>
      <Stack gap="sm">
        <Text size="sm" data-testid="document-period-reopen-scope">
          {t('document.reopen.dialogScope', { period })}
        </Text>

        <Textarea
          label={t('document.reopen.reason')}
          required
          value={reason}
          onChange={(event) => setReason(event.currentTarget.value)}
          maxLength={ReopenReasonMaxLength}
          minRows={3}
          autosize
          data-autofocus
          data-testid="document-period-reopen-reason"
        />

        <DateTimePicker
          label={t('document.reopen.until')}
          description={t('document.reopen.untilHint')}
          required
          value={until}
          onChange={setUntil}
          minDate={new Date()}
          valueFormat="YYYY-MM-DD HH:mm"
          locale={calendarLocale(language())}
          error={untilInvalid ? t('document.reopen.untilInvalid') : undefined}
          data-testid="document-period-reopen-until"
        />

        {mutation.error !== null && <ErrorAlert error={mutation.error} />}

        <Group justify="flex-end" mt="xs">
          <Button variant="default" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button
            disabled={reasonMissing || untilInvalid}
            loading={loading}
            onClick={() => {
              if (!mutation.isPending) mutation.mutate();
            }}
            data-testid="document-period-reopen-confirm"
          >
            {t('document.reopen.confirm')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

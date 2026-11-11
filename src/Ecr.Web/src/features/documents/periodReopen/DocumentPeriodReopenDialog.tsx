import '@mantine/dates/styles.css';
import { useState, type JSX } from 'react';
import { Button, Group, Modal, Stack, Text, Textarea } from '@mantine/core';
import { DateTimePicker } from '@mantine/dates';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { calendarLocale } from '@/shared/dates/calendarLocale';
import { formatPeriodKey } from '@/shared/format';
import { language, t } from '@/shared/i18n';
import { untilTextInZone } from '@/shared/siteMoment';
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
  /** Пояс майданчика (IANA) для строку в тості; без нього — пояс браузера (✎ AN-178). */
  readonly timeZoneId?: string | null | undefined;
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
  timeZoneId,
  onClose,
}: DocumentPeriodReopenDialogProps): JSX.Element {
  const queryClient = useQueryClient();
  const [reason, setReason] = useState('');
  const [until, setUntil] = useState<Date | null>(() => new Date(defaultOpenUntil));
  const period = formatPeriodKey(periodKey);

  const mutation = useMutation({
    // The dialog shows the refusal itself (ErrorAlert), so the global toast must stay silent.
    meta: { handled: true },
    mutationFn: () =>
      postDocumentPeriodReopen(documentId, {
        periodKey,
        reason: reason.trim(),
        openUntil: until === null ? null : until.toISOString(),
      }),
    onSuccess: async (view) => {
      // Стан винятку, документ (його ключі — під тим самим префіксом) і зрізи: сітка стає редагованою.
      // ✎ debt-1109 i4-P3: і вкладка Periods (календар і перелік винятків `projectDocumentReopensKey` — обидва під
      // префіксом `['periods']`): без цього повернення на Periods показувало місяць просто «Locked» до перезавантаження.
      // Проєкту документа діалог не знає, тож префікс без нього — перечитуються лише змонтовані календарі.
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.document.ofDocument(documentId) }),
        queryClient.invalidateQueries({ queryKey: queryKeys.slices.all() }),
        queryClient.invalidateQueries({ queryKey: ['periods'] }),
      ]);
      // ✎ AN-178: строк — у поясі майданчика з правилом кінця доби (`untilTextInZone`), як банер і смуга місяців;
      // доти — `formatDateTime` у поясі браузера: той самий строк у тості й банері читався по-різному.
      showDone(t('document.reopen.done', { period, until: untilTextInZone(view.openUntil, timeZoneId) }));
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

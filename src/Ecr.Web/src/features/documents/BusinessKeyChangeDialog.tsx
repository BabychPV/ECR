import type { JSX } from 'react';
import { Button, Group, Modal, Stack, Textarea, TextInput } from '@mantine/core';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { t } from '@/shared/i18n';
import { BusinessKeyMaxLength } from './api';

/** Стан форми тримає `useBusinessKeyChangeAction`; діалог лише малює його. */
export interface BusinessKeyChangeDialogProps {
  readonly opened: boolean;
  readonly onClose: () => void;
  readonly newKey: string;
  readonly onNewKeyChange: (value: string) => void;
  readonly reason: string;
  readonly onReasonChange: (value: string) => void;
  /** `422 rekeyKeyInvalid` — під полем ключа. */
  readonly keyError: string | null;
  /** `422 rekeyReasonRequired` — під полем причини. */
  readonly reasonError: string | null;
  /** Решта відмов — банером під полями, форма лишається. */
  readonly bannerError: unknown;
  readonly canSubmit: boolean;
  /** Мутація в польоті (`useMutation().isPending`). */
  readonly isPending: boolean;
  readonly onConfirm: () => void;
}

/**
 * Діалог «Змінити номер справи» (ФВ-3.9) — окремим чанком (`D-132`): зміна ключа — рідкісна дія з
 * небезпечним правом, а `Textarea` з автовисотою й поле ключа сторінка документа несла б у чанку
 * маршруту для кожного, хто його відкрив. Логіка (права, мутація, розбір відмов) — у
 * `BusinessKeyChangeAction.tsx`.
 */
export function BusinessKeyChangeDialog({
  opened,
  onClose,
  newKey,
  onNewKeyChange,
  reason,
  onReasonChange,
  keyError,
  reasonError,
  bannerError,
  canSubmit,
  isPending,
  onConfirm,
}: BusinessKeyChangeDialogProps): JSX.Element {
  return (
    <Modal opened={opened} onClose={onClose} title={t('documents.changeKeyTitle')}>
      <Stack gap="sm">
        <TextInput
          label={t('documents.newBusinessKey')}
          description={t('documents.newBusinessKeyHint', { max: BusinessKeyMaxLength })}
          value={newKey}
          onChange={(event) => onNewKeyChange(event.currentTarget.value)}
          error={keyError}
          data-autofocus
        />

        <Textarea
          label={t('workflow.reason')}
          value={reason}
          onChange={(event) => onReasonChange(event.currentTarget.value)}
          error={reasonError}
          minRows={3}
          autosize
        />

        {bannerError !== null && <ErrorAlert error={bannerError} />}

        <Group justify="flex-end" mt="xs">
          <Button variant="default" onClick={onClose} data-testid="business-key-cancel">
            {t('common.cancel')}
          </Button>
          <Button disabled={!canSubmit} loading={isPending} onClick={onConfirm} data-testid="business-key-confirm">
            {t('documents.changeKey')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

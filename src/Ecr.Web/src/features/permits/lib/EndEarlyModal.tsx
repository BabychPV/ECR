import { useEffect, useState, type JSX } from 'react';
import { Button, Group, Modal, Textarea } from '@mantine/core';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { t } from '@/shared/i18n';
import { useRestoreFocus } from '@/shared/ui/useRestoreFocus';

export interface EndEarlyModalProps {
  readonly opened: boolean;
  readonly title: string;
  readonly label: string;
  readonly description: string;
  readonly confirmLabel: string;
  readonly minLength: number;
  readonly isPending: boolean;
  /** Відмова сервера, яку людина виправляє тут же (діалог НЕ закривається). `null` - відмови немає. */
  readonly error: string | null;
  /** Людина змінила причину: стара відмова втратила силу. */
  readonly onEdit: () => void;
  readonly returnFocusTo: () => HTMLElement | null;
  readonly onConfirm: (reason: string) => void;
  readonly onClose: () => void;
}

/**
 * PUI-5: «End early» з помилкою ВСЕРЕДИНІ діалогу.
 *
 * `ReasonModal` (shared/ui) відмову показати не вміє: вона йшла тостом або діалог закривався і причину губило.
 * Тут та сама поведінка (поріг `minLength` після `trim()`, очищення при відкритті, повернення фокуса), плюс
 * `error` на полі причини - Mantine ставить `aria-invalid` і зв'язує текст із полем.
 */
export function EndEarlyModal({
  opened,
  title,
  label,
  description,
  confirmLabel,
  minLength,
  isPending,
  error,
  onEdit,
  returnFocusTo,
  onConfirm,
  onClose,
}: EndEarlyModalProps): JSX.Element {
  const [reason, setReason] = useState('');
  useRestoreFocus(opened, returnFocusTo);
  const confirmLoading = usePendingLoading(isPending);

  useEffect(() => {
    if (opened) setReason('');
  }, [opened]);

  const enough = reason.trim().length >= Math.max(1, minLength);

  return (
    <Modal opened={opened} onClose={onClose} title={title} returnFocus={false}>
      <Textarea
        label={label}
        description={description}
        value={reason}
        onChange={(event) => {
          setReason(event.currentTarget.value);
          if (error !== null) onEdit();
        }}
        error={error}
        minRows={3}
        autosize
        data-autofocus
      />

      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <Button disabled={!enough} loading={confirmLoading} onClick={() => onConfirm(reason.trim())}>
          {confirmLabel}
        </Button>
      </Group>
    </Modal>
  );
}

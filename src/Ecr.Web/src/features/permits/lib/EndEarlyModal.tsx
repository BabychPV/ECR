import { useEffect, useState, type JSX } from 'react';
import { Button, Group, Modal, Text, Textarea, VisuallyHidden } from '@mantine/core';
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
  const [note, setNote] = useState('');
  useRestoreFocus(opened, returnFocusTo);
  const confirmLoading = usePendingLoading(isPending);

  useEffect(() => {
    if (opened) {
      setReason('');
      setNote('');
    }
  }, [opened]);

  const required = Math.max(1, minLength);
  const enough = reason.trim().length >= required;

  return (
    <Modal opened={opened} onClose={onClose} title={title} returnFocus={false}>
      <Textarea
        label={label}
        description={
          <>
            {description}
            {/* KI-04: порогу `minLength` людина не бачила - кнопка просто не вмикалась. Підказка лежить в ОПИСІ поля (Mantine
                зв'язує його з полем через `aria-describedby`) і зникає, щойно порогу досягнуто. */}
            {enough ? null : (
              <Text component="span" display="block" inherit data-testid="end-reason-hint">
                {t('permits.endEarly.minLengthHint', { min: String(required) })}
              </Text>
            )}
          </>
        }
        value={reason}
        onChange={(event) => {
          const value = event.currentTarget.value;
          setReason(value);
          // Регіон мовчить, доки немає ПЕРЕХОДУ: початковий текст підказки вже читається з опису поля (без подвійного читання).
          const nextEnough = value.trim().length >= required;
          if (nextEnough !== enough) {
            setNote(nextEnough ? t('permits.endEarly.minLengthReached') : t('permits.endEarly.minLengthHint', { min: String(required) }));
          }
          if (error !== null) onEdit();
        }}
        error={error}
        minRows={3}
        autosize
        data-autofocus
      />
      {/* Підказка в описі поля читається при фокусі (`aria-describedby`), але її ЗНИКНЕННЯ читалка не озвучить. Тому окремий
          постійний живий регіон: ПОРОЖНІЙ, доки не було переходу, а далі його текст змінюється лише на переході «замало» ⇄
          «досить» (не на кожну літеру), тож читалка оголошує перехід один раз і не дублює опис. */}
      <VisuallyHidden role="status" aria-live="polite" aria-atomic data-testid="end-reason-status">
        {note}
      </VisuallyHidden>

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

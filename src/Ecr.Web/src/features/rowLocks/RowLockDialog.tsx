import { useState, type JSX } from 'react';
import { Button, Group, List, Modal, Stack, Text, Textarea } from '@mantine/core';
// ⚠ Поле дати — лише в цьому лінивому модулі (`D-132`): `@mantine/dates` і його стилі не їдуть із сіткою.
import { DateOnlyInput } from '@/shared/dates/DateInputWithStyles';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { todayDateOnly } from '@/shared/format/dateOnly';
import { t } from '@/shared/i18n';
import { lockFormError, lockFormErrorText, lockRequest, MaxNoteLength, type LockForm, type LockTable } from './rowLockModel';
import { useCreateRowLocks } from './useRowLocks';

/** Рядок, який блокуємо: ключ для запиту й назва для людини (підпис або «рядок N», без технічного ключа). */
export interface LockRowItem {
  readonly rowKey: string;
  readonly label: string;
}

export interface RowLockDialogProps {
  readonly documentId: number;
  readonly periodKey: number;
  readonly table: LockTable;
  /** Виділені в сітці рядки (діапазон або рядок фокусної комірки). */
  readonly rows: readonly LockRowItem[];
  readonly onClose: () => void;
}

/** Скільки назв рядків показати поіменно; решта — «і ще N». */
const ListedRows = 8;

/**
 * ✎ B139 (B75): «Заблокувати рядки…» — рядки (виділені в сітці), дати з–по (кінцева включно, часовий пояс
 * проєкту рахує сервер) і необов'язкова примітка. Діалог за `KIT.md` §6: назва дії в заголовку, наслідок словами,
 * дієслово на кнопці, «Скасувати» поруч.
 *
 * ⚠ «Заблокувати» вимкнена, доки форма не пройде перевірку `lockFormError`; решту (минула дата, рядок уже
 * заблоковано, немає права) каже сервер — `showApiError` текстом каталогу, діалог лишається відкритим.
 *
 * ⛔ Лінивий модуль (`D-132`): сітка вантажить його лише з першим відкриттям.
 */
export default function RowLockDialog({ documentId, periodKey, table, rows, onClose }: RowLockDialogProps): JSX.Element {
  const [fromDate, setFromDate] = useState(() => todayDateOnly());
  const [toDate, setToDate] = useState('');
  const [note, setNote] = useState('');

  const form: LockForm = { rowKeys: rows.map((row) => row.rowKey), fromDate, toDate, note };
  const error = lockFormError(form);
  const create = useCreateRowLocks(documentId, periodKey, onClose);

  // ФВ-14.26: спінер не миттєво (короткий запит не блимає).

  const createLoading = usePendingLoading(create.isPending);


  const submit = (): void => {
    if (error !== null || create.isPending) return;
    create.mutate(lockRequest(form, table, periodKey));
  };

  const close = (): void => {
    if (!create.isPending) onClose();
  };

  return (
    <Modal opened onClose={close} title={t('rowLock.dialogTitle', { count: rows.length })} data-row-lock-dialog>
      <Stack gap="sm">
        <Text size="sm">{t('rowLock.dialogBody')}</Text>

        <div>
          <Text size="sm" fw={500} id="row-lock-rows-label">
            {t('rowLock.rowsLabel', { count: rows.length })}
          </Text>
          <List size="sm" aria-labelledby="row-lock-rows-label" data-row-lock-rows>
            {rows.slice(0, ListedRows).map((row) => (
              <List.Item key={row.rowKey}>{row.label}</List.Item>
            ))}
          </List>
          {rows.length > ListedRows && (
            <Text size="sm" c="dimmed">
              {t('rowLock.rowsMore', { count: rows.length - ListedRows })}
            </Text>
          )}
        </div>

        <Group grow align="flex-start">
          <DateOnlyInput label={t('rowLock.fromLabel')} value={fromDate} onChange={setFromDate} clearable={false} autoFocus />
          <DateOnlyInput
            label={t('rowLock.toLabel')}
            description={t('rowLock.toHint')}
            value={toDate}
            onChange={setToDate}
          />
        </Group>

        <Textarea
          label={t('rowLock.noteLabel')}
          autosize
          minRows={2}
          maxRows={5}
          value={note}
          maxLength={MaxNoteLength + 50}
          onChange={(event) => setNote(event.currentTarget.value)}
          data-row-lock-note
        />

        {error !== null && toDate !== '' && (
          <Text size="sm" c="statusError" role="alert" data-row-lock-error>
            {lockFormErrorText(error)}
          </Text>
        )}

        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={close} disabled={create.isPending}>
            {t('grid.confirmCancel')}
          </Button>
          <Button onClick={submit} disabled={error !== null} loading={createLoading} data-row-lock-save>
            {t('rowLock.lockVerb')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

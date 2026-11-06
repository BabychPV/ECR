import { useState, type JSX } from 'react';
import { Button, Group, Modal, NumberInput, Stack, TextInput } from '@mantine/core';
import { serverMessage } from '@/features/registries/rc816/serverMessage';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showErrorText } from '@/shared/ui/notify';
import { changedValues, type EditField } from './airCardModel';
import { useSaveAirRow } from './useAirLimits';

/** Рядок, який править форма: таблиця (довідник), id, версія і початкові значення текстом. */
export interface AirEditTarget {
  readonly title: string;
  readonly registryCode: string;
  readonly id: number;
  readonly version: string | null;
  readonly fields: readonly EditField[];
  readonly initial: Readonly<Record<string, string>>;
}

/**
 * P1 07.11: форма правки одного рядка картки «Sources & parameters» (джерело або речовина) — усі поля, що правляться
 * (вбудовані й додані колонки), типізовано: число — `NumberInput`, текст — `TextInput`, дата — `TextInput type="date"`.
 * Пише загальним пакетним записом довідника з версією рядка; відмова рядка (`entryChanged` — рядок змінив хтось інший) —
 * текст сервера в банері, картка перечитується (`useSaveAirRow`).
 */
export function AirRowEditor({
  target,
  permitId,
  onClose,
}: {
  readonly target: AirEditTarget;
  readonly permitId: number;
  readonly onClose: () => void;
}): JSX.Element {
  const [draft, setDraft] = useState<Readonly<Record<string, string>>>(target.initial);
  const [rowErrors, setRowErrors] = useState<readonly string[]>([]);
  const save = useSaveAirRow(permitId);
  const set = (code: string, value: string): void => setDraft((current) => ({ ...current, [code]: value }));

  const submit = (): void => {
    const values = changedValues(target.fields, target.initial, draft);
    if (Object.keys(values).length === 0) {
      onClose();
      return;
    }

    setRowErrors([]);
    save.mutate(
      { registryCode: target.registryCode, id: target.id, version: target.version, values },
      {
        onSuccess: (result) => {
          if (result.applied) {
            onClose();
            return;
          }

          const found = result.rows.flatMap((row) => row.errors);
          const errors = found.map((error) => serverMessage(error.messageKey, error.params));
          // Someone else changed the row: this form holds the old version, so saving again would be refused again. Close it
          // with the server's text; the card has been re-read, and opening the row again takes the fresh version.
          if (found.some((error) => error.messageKey.endsWith('.entryChanged'))) {
            showErrorText(errors.join(' '));
            onClose();
            return;
          }

          setRowErrors(errors.length > 0 ? errors : [t('permits.air.edit.notSaved')]);
        },
      },
    );
  };

  return (
    <Modal opened onClose={onClose} title={target.title} size="lg">
      <form
        onSubmit={(event) => {
          event.preventDefault();
          submit();
        }}
      >
        <Stack gap="sm">
          {rowErrors.length > 0 && (
            <Banner
              tone="danger"
              title={t('permits.air.edit.notSaved')}
              text={
                <>
                  {rowErrors.map((message, index) => (
                    <span key={index} className="pa-edit-error">
                      {message}
                    </span>
                  ))}
                </>
              }
            />
          )}
          {save.error !== null && <ErrorAlert error={save.error} />}
          <div className="pa-edit-grid">
            {target.fields.map((field) =>
              field.kind === 'number' ? (
                <NumberInput
                  key={field.code}
                  label={field.label}
                  value={draft[field.code] ?? ''}
                  onChange={(value) => set(field.code, typeof value === 'number' ? String(value) : value)}
                  hideControls
                  allowDecimal
                  decimalSeparator="."
                  data-field={field.code}
                />
              ) : (
                <TextInput
                  key={field.code}
                  label={field.label}
                  type={field.kind === 'date' ? 'date' : 'text'}
                  value={draft[field.code] ?? ''}
                  onChange={(event) => set(field.code, event.currentTarget.value)}
                  data-field={field.code}
                />
              ),
            )}
          </div>
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose}>
              {t('common.cancel')}
            </Button>
            <Button type="submit" loading={save.isPending}>
              {t('common.save')}
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}

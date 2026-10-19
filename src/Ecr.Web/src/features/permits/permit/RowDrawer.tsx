import { useState, type JSX } from 'react';
import { Button, Drawer, Group, Stack, Text } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { serverMessage } from '@/features/registries/rc816/serverMessage';
import { saveBatch, type RegistryBatchItem, type RegistryBatchResult } from '@/features/registries/rows/api';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { showApiError, showDone, showUndo } from '@/shared/ui/notify';
import { useRestoreFocus } from '@/shared/ui/useRestoreFocus';
import type { Permit } from '../api/types';
import { requiredColumns, validateRow } from '../lib/rowForm';
import {
  deleteRowItem,
  fieldProblem,
  initialForm,
  labelOfId,
  newRowItem,
  toRowValues,
  updateRowItem,
  type FieldProblem,
  type FormValues,
  type LoadedRow,
  type PermitField,
} from './model';
import { permitKeys, usePickerData } from './usePermitData';
import { RowFields } from './RowFields';
import './permit.css';

export interface RowDrawerProps {
  readonly permit: Permit;
  readonly registryCode: string;
  readonly tableName: string;
  readonly fields: readonly PermitField[];
  /** Усі рядки таблиці (для перевірки дубля ключа). */
  readonly rows: readonly LoadedRow[];
  /** Рядок, що редагується; `null` — новий. */
  readonly loaded: LoadedRow | null;
  readonly readOnly: boolean;
  /** «Save and add next» просить форму нового рядка; `onClose` — закрити шторку. */
  readonly onAddNext: () => void;
  readonly onClose: () => void;
}

function problemText(problem: FieldProblem): string {
  switch (problem) {
    case 'notNumber':
      return t('permits.page.row.notNumber');
    case 'notInteger':
      return t('permits.page.row.notInteger');
    case 'notDate':
      return t('permits.page.row.notDate');
  }
}

/** Помилки полів із відповіді пакета: `field` → текст сервера; без поля — для всієї форми. */
export function resultErrors(result: RegistryBatchResult): { fields: Record<string, string>; form: string[] } {
  const fields: Record<string, string> = {};
  const form: string[] = [];
  for (const row of result.rows) {
    for (const error of row.errors) {
      const text = serverMessage(error.messageKey, error.params);
      if (error.field === null || error.field === '') form.push(text);
      else fields[error.field] = text;
    }
  }
  return { fields, form };
}

/**
 * Шторка рядка таблиці дозволу (мокап `ctx.panel('*')`): новий рядок і редагування, перевірка обов'язкових і дубля
 * ключа до відправки (`rowForm`), список із трьох областей (`pickerScope`), «Save and add next», видалення з «Undo».
 * Закінчений дозвіл відкриває рядок лише для читання.
 */
export function RowDrawer({
  permit,
  registryCode,
  tableName,
  fields,
  rows,
  loaded,
  readOnly,
  onAddNext,
  onClose,
}: RowDrawerProps): JSX.Element {
  const queryClient = useQueryClient();
  // Шторка живе, лише поки відкрита (`?panel=`): після закриття фокус — на кнопку, що її відкрила (PA-U-03).
  useRestoreFocus(true);
  const isNew = loaded === null;
  // Знімок значень на момент відкриття: порівняння «що змінено» не має повзти разом із фоновим перечитанням рядків.
  const [original] = useState<FormValues>(() => initialForm(fields, loaded ?? undefined));
  const [form, setForm] = useState<FormValues>(original);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formErrors, setFormErrors] = useState<string[]>([]);
  const picker = usePickerData(permit, fields, true);

  function refresh(): void {
    void queryClient.invalidateQueries({ queryKey: permitKeys.rows(registryCode, permit.id) });
    void queryClient.invalidateQueries({ queryKey: permitKeys.item(permit.id) });
  }

  const write = useMutation<RegistryBatchResult, Error, { items: readonly RegistryBatchItem[]; addNext: boolean }>({
    mutationKey: ['permits', 'item', permit.id, 'row', registryCode],
    mutationFn: ({ items }) => saveBatch(registryCode, items, false),
    onSuccess: (result, { addNext }) => {
      if (!result.applied) {
        const found = resultErrors(result);
        setErrors(found.fields);
        setFormErrors(found.form.length > 0 ? found.form : [t('permits.page.row.notSaved')]);
        return;
      }
      refresh();
      showDone(isNew ? t('permits.page.row.added') : t('permits.page.row.saved'));
      if (addNext) onAddNext();
      else onClose();
    },
    onError: showApiError,
  });
  const saving = usePendingLoading(write.isPending);

  const remove = useMutation<RegistryBatchResult, Error, LoadedRow>({
    mutationKey: ['permits', 'item', permit.id, 'row-delete', registryCode],
    mutationFn: (row) => saveBatch(registryCode, [deleteRowItem('delete', row)], false),
    onSuccess: (result, row) => {
      if (!result.applied) {
        setFormErrors(resultErrors(result).form.concat([t('permits.page.row.notDeleted')]));
        return;
      }
      refresh();
      onClose();
      // Сервер «назад» не вміє: скасування заводить рядок наново з тими самими значеннями (новий id).
      void showUndo(t('permits.page.row.deleted'), () => {
        void saveBatch(registryCode, [newRowItem('undo', permit.id, fields, initialForm(fields, row))], false)
          .then(refresh)
          .catch(showApiError);
      });
    },
    onError: showApiError,
  });

  function changeField(code: string, value: string): void {
    setForm((current) => ({ ...current, [code]: value }));
    setErrors((current) => {
      if (current[code] === undefined) return current;
      return Object.fromEntries(Object.entries(current).filter(([key]) => key !== code));
    });
  }

  function submit(addNext: boolean): void {
    if (write.isPending || readOnly) return;
    const found: Record<string, string> = {};
    const messages: string[] = [];

    for (const field of fields) {
      const problem = fieldProblem(field, form[field.column.code] ?? '');
      if (problem !== null) found[field.column.code] = problemText(problem);
    }

    const labelOf = (column: PermitField['column'], id: string): string | null =>
      labelOfId(fields.find((field) => field.column.code === column.code), picker.data, id);
    const verdict = validateRow(
      fields.map((field) => field.column),
      toRowValues(fields, form, labelOf),
      rows.map((row) => row.row),
      loaded?.row,
    );

    if (!verdict.ok) {
      if (verdict.kind === 'required') {
        // Нечитабельне значення («12abc») для `validateRow` порожнє; його помилка конкретніша за «Required».
        const unread = verdict.missing.filter((code) => found[code] === undefined);
        for (const code of unread) found[code] = t('permits.page.row.required');
        if (unread.length > 0) messages.push(t('permits.page.row.fillRequired'));
      } else {
        const names = fields.filter((field) => verdict.keyColumns.includes(field.column.code)).map((field) => field.column.label);
        messages.push(t('permits.page.row.duplicate', { columns: names.join(' + ') }));
      }
    }

    setErrors(found);
    setFormErrors(messages);
    if (Object.keys(found).length > 0 || messages.length > 0) return;

    const item =
      loaded === null
        ? newRowItem('save', permit.id, fields, form)
        : updateRowItem('save', loaded, fields, form, original);
    // Нічого не змінено: не відправляти порожній пакет, просто закрити.
    if (item === null) {
      onClose();
      return;
    }
    write.mutate({ items: [item], addNext });
  }

  const title = isNew ? t('permits.page.row.newTitle') : String(loaded.row[fields[0]?.column.code ?? ''] ?? loaded.row.id);
  const subtitle = `${tableName} · ${permit.number}${readOnly ? ` · ${t('permits.page.row.readOnly')}` : ''}`;

  return (
    <Drawer
      opened
      onClose={onClose}
      position="right"
      size="lg"
      title={title}
      closeButtonProps={{ 'aria-label': t('permits.page.row.close') }}
    >
      <Stack gap="md">
        <Text size="sm" c="dimmed">
          {subtitle}
        </Text>
        {formErrors.length > 0 && <Banner tone="danger" text={formErrors.join(' ')} />}
        {picker.failed !== null && <Banner tone="warning" text={t('permits.page.row.pickersFailed')} />}
        <RowFields
          fields={fields}
          form={form}
          errors={errors}
          onChange={changeField}
          readOnly={readOnly}
          picker={picker.data}
          pickerPending={picker.pending}
          isNew={isNew}
        />
        {requiredColumns(fields.map((field) => field.column)).length > 0 && !readOnly && (
          <Text size="xs" c="dimmed">
            {t('permits.page.row.requiredNote')}
          </Text>
        )}
        <Group justify="space-between" gap="xs">
          {loaded !== null && !readOnly ? (
            <Button variant="default" color="statusError" disabled={remove.isPending} onClick={() => remove.mutate(loaded)}>
              {t('permits.page.row.delete')}
            </Button>
          ) : (
            <span />
          )}
          <Group gap="xs">
            <Button variant="default" onClick={onClose}>
              {t('permits.page.row.close')}
            </Button>
            {!readOnly && isNew && (
              <Button variant="default" disabled={write.isPending} onClick={() => submit(true)}>
                {t('permits.page.row.saveNext')}
              </Button>
            )}
            {!readOnly && (
              <Button loading={saving} onClick={() => submit(false)}>
                {t('permits.page.row.save')}
              </Button>
            )}
          </Group>
        </Group>
      </Stack>
    </Drawer>
  );
}

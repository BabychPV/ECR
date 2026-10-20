import { useState, type JSX } from 'react';
import { Button, Group, Modal, Stack, Text, TextInput } from '@mantine/core';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { language, t } from '@/shared/i18n';
import { showApiError, showDone } from '@/shared/ui/notify';
import type { PermitListValue } from '../lists/listsApi';
import { valueLabels } from '../lists/listsModel';
import { useChangeListItems } from '../lists/useLists';

export interface AddValueDialogProps {
  /** Ключ списку (`shared:…` або `type:…`). */
  readonly listKey: string;
  /** Спільний довідник чи список лише цього типу: від цього залежить пояснення, кому значення стане доступним. */
  readonly scope: 'shared' | 'type';
  /** Код довідника для пояснення спільного списку. */
  readonly listName: string;
  /** Нове значення вже в списку, а пікери перечитано: форма ставить його в поле. */
  readonly onAdded: (item: PermitListValue) => void;
  readonly onClose: () => void;
}

/** Нове значення серед відповіді: найновіший запис із введеним англійським підписом (якщо підпис не збігся — останній). */
export function addedItem(items: readonly PermitListValue[], text: string): PermitListValue | undefined {
  const wanted = text.trim().toLowerCase();
  const matching = items.filter((item) => (item.displayL10n.en ?? '').trim().toLowerCase() === wanted);
  const pool = matching.length > 0 ? matching : items;

  return pool.reduce<PermitListValue | undefined>((best, item) => (best === undefined || item.id > best.id ? item : best), undefined);
}

/**
 * «Not in the list? Add a value…» (мокап `screens-permits.js`): діалог із одним полем. Значення пишеться в СПІЛЬНИЙ
 * довідник (його побачать усі типи) або в список лише цього типу — за областю стовпця; права перевіряє сервер
 * (`PUT /permit-lists/{key}/items`), а кнопка в формі показується лише з правом (`editable` переліку).
 */
export function AddValueDialog({ listKey, scope, listName, onAdded, onClose }: AddValueDialogProps): JSX.Element {
  const change = useChangeListItems(listKey);
  const saving = usePendingLoading(change.isPending);
  const [text, setText] = useState('');

  function submit(): void {
    if (change.isPending || text.trim() === '') return;
    change.mutate([{ action: 'Add', id: null, displayL10n: valueLabels(text, undefined, language()) }], {
      onSuccess: (detail) => {
        const item = addedItem(detail.items, text);
        showDone(t('permits.page.addValue.added'));
        if (item !== undefined) onAdded(item);
        onClose();
      },
      onError: showApiError,
    });
  }

  return (
    <Modal
      opened
      onClose={onClose}
      title={t('permits.page.addValue.title')}
      closeButtonProps={{ 'aria-label': t('permits.page.addValue.close') }}
      centered
    >
      <form
        onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          submit();
        }}
      >
        <Stack gap="md">
          <Text size="sm" c="dimmed">
            {scope === 'shared' ? t('permits.page.addValue.sharedNote', { name: listName }) : t('permits.page.addValue.typeNote')}
          </Text>
          <TextInput
            label={t('permits.page.addValue.label')}
            value={text}
            maxLength={256}
            required
            data-autofocus
            onChange={(event) => setText(event.currentTarget.value)}
          />
          <Group justify="flex-end" gap="xs">
            <Button variant="default" onClick={onClose}>
              {t('permits.page.addValue.cancel')}
            </Button>
            <Button type="submit" loading={saving} disabled={text.trim() === ''}>
              {t('permits.page.addValue.submit')}
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}

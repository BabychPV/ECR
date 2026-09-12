import { useState, type JSX } from 'react';
import { Button, Group, Modal, NumberInput, Switch, TextInput } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { PresentationRevisionResponse, TemplateColumnDto } from '@/api/types';
import { LocalizedInput, type LocalizedValue } from '@/shared/ui/LocalizedInput';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/** Одна зміна презентаційного шару, як її приймає сервер. */
interface PresentationChange {
  entityType: string;
  entityId: number;
  field: string;
  value: string | null;
}

/**
 * Правка презентаційного шару **опублікованої** версії (`ФВ-7.2`).
 *
 * ⛔ Це єдина зміна, дозволена після публікації: підписи, порядок, формат,
 * видимість. Структура заморожена, і спроба змінити тип чи точність
 * відхиляється `ECR-TMPL-0409` із переліком полів — саме тому тут показані
 * лише презентаційні поля, а тип даних видно, але не редаговано.
 *
 * ⚠ Заголовок редагується **всіма мовами одразу**: у базі це один JSON, і
 * запис лише англійського значення стер би російське й казахське. Тому
 * структура віддає `headerL10n` цілком, а не готовий рядок.
 *
 * ⛔ Надсилаються **лише змінені** поля. Патч із усіма полями поспіль
 * записував би в аудит зміни, яких не було, і кожне відкриття діалогу
 * виглядало б в історії як правка.
 */
export function PresentationEditor({
  templateVersionId,
  column,
  onClose,
}: {
  templateVersionId: number;
  column: TemplateColumnDto | null;
  onClose: () => void;
}): JSX.Element {
  const queryClient = useQueryClient();

  const [header, setHeader] = useState<LocalizedValue>({});
  const [format, setFormat] = useState('');
  const [ordinal, setOrdinal] = useState(0);
  const [hidden, setHidden] = useState(false);
  const [loadedFor, setLoadedFor] = useState<number | null>(null);

  // ⚠ Стан наповнюється при зміні колонки, а не в `useEffect`: ефект тут дав
  // би зайвий рендер із порожніми полями, і діалог блимав би порожнім щоразу.
  if (column !== null && loadedFor !== column.id) {
    setLoadedFor(column.id);
    setHeader({ ...(column.headerL10n.values ?? {}) });
    setFormat(column.displayFormat ?? '');
    setOrdinal(column.ordinal);
    setHidden(column.isHidden);
  }

  const patch = useMutation({
    mutationFn: (changes: PresentationChange[]) =>
      apiFetch<PresentationRevisionResponse>(
        `/api/v1/template-versions/${templateVersionId}/presentation`,
        { method: 'PATCH', body: JSON.stringify(changes) },
      ),
    onSuccess: async (result) => {
      // ⚠ Нова ревізія — це новий ключ кешу `v{id}:r{rev}`. Перечитуємо
      // структуру: без цього екран показував би стару, а сервер віддавав нову.
      await queryClient.invalidateQueries({
        queryKey: queryKeys.templates.version(templateVersionId),
      });

      onClose();
      showDone(t('version.patched', { revision: result.presentationRevision }));
    },
    onError: showApiError,
  });

  /** Те, що справді змінилося. */
  const changes = (): PresentationChange[] => {
    if (column === null) return [];

    const result: PresentationChange[] = [];
    const before = column.headerL10n.values ?? {};

    const entity = { entityType: 'ColumnDef', entityId: column.id };

    if (JSON.stringify(header) !== JSON.stringify(before)) {
      result.push({ ...entity, field: 'HeaderL10n', value: JSON.stringify(header) });
    }

    if (format !== (column.displayFormat ?? '')) {
      result.push({ ...entity, field: 'DisplayFormat', value: format.length === 0 ? null : format });
    }

    if (ordinal !== column.ordinal) {
      result.push({ ...entity, field: 'Ordinal', value: String(ordinal) });
    }

    if (hidden !== column.isHidden) {
      result.push({ ...entity, field: 'IsHidden', value: hidden ? '1' : '0' });
    }

    return result;
  };

  const pending = changes();

  return (
    <Modal
      opened={column !== null}
      onClose={onClose}
      title={column === null ? '' : `${t('version.presentation')} · ${column.code}`}
    >
      <LocalizedInput
        label={t('version.column')}
        description={t('version.headerHint')}
        value={header}
        onChange={setHeader}
      />

      <TextInput
        mt="sm"
        label={t('version.displayFormat')}
        description={t('version.displayFormatHint')}
        value={format}
        onChange={(event) => setFormat(event.currentTarget.value)}
      />

      <NumberInput
        mt="sm"
        label={t('version.ordinal')}
        description={t('version.ordinalHint')}
        value={ordinal}
        onChange={(value) => setOrdinal(typeof value === 'number' ? value : ordinal)}
      />

      <Switch
        mt="sm"
        label={t('version.hidden')}
        description={t('version.hiddenHint')}
        checked={hidden}
        onChange={(event) => setHidden(event.currentTarget.checked)}
      />

      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={onClose}>
          {t('common.cancel')}
        </Button>

        {/* ⛔ Вимкнена, поки нічого не змінилося: порожній патч сервер
            відхиляє `ECR-TMPL-0422` («змінювати нічого»), і кнопка, яка
            гарантовано дасть відмову, — це та сама неправдива обіцянка. */}
        <Button
          disabled={pending.length === 0}
          loading={patch.isPending}
          onClick={() => patch.mutate(pending)}
        >
          {t('common.save')}
        </Button>
      </Group>
    </Modal>
  );
}

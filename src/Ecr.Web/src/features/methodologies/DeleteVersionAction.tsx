import { useState, type JSX } from 'react';
import { Button } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { MethodologyDraftVersionDto } from '@/api/types';
import { queryKeys } from '@/api/queryKeys';
import { deleteMethodologyVersion } from '@/features/methodologies/api';
import { mayDeleteVersion } from '@/features/methodologies/versionDeletion';
import { t } from '@/shared/i18n';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showDone } from '@/shared/ui/notify';

export interface DeleteVersionActionArgs {
  readonly methodologyId: number;

  /** Чи має сесія право `Calculation.EditFormula`. */
  readonly allowed: boolean;
}

export interface DeleteVersionAction {
  /** Кнопка для рядка версії; `null`, якщо версію видаляти не можна. */
  readonly triggerFor: (version: MethodologyDraftVersionDto) => JSX.Element | null;

  /** Підтвердження — один на екран, а не на рядок. */
  readonly dialog: JSX.Element;

  /** Банер відмови; `null`, якщо відмови не було. */
  readonly refusal: JSX.Element | null;
}

/**
 * «Видалити чернетку» версії методології (`BE-25`).
 *
 * ⚠ Той самий розподіл, що й `useDeleteDocumentAction`: кнопка — у рядку,
 * відмова — банером на сторінці. У `ConfirmModal` банеру місця немає: його
 * `text` загорнутий у `<p>`, а `Alert` — це `<div>`.
 */
export function useDeleteVersionAction({
  methodologyId,
  allowed,
}: DeleteVersionActionArgs): DeleteVersionAction {
  const queryClient = useQueryClient();
  const [target, setTarget] = useState<MethodologyDraftVersionDto | null>(null);
  const [opened, setOpened] = useState(false);

  const versionsKey = queryKeys.methodologies.versionsOf(methodologyId);

  const remove = useMutation({
    mutationFn: (version: MethodologyDraftVersionDto) =>
      deleteMethodologyVersion(methodologyId, version.id),
    onSuccess: async (_, version) => {
      setOpened(false);
      await queryClient.invalidateQueries({ queryKey: versionsKey });
      showDone(t('methodologies.versionDeleted', { version: version.versionNumber }));
    },
    onError: async () => {
      setOpened(false);

      // ⚠ Відмова могла статися тому, що стан версії змінився за спиною
      // (опублікували з іншої вкладки): перечитаний перелік сховає кнопку.
      await queryClient.invalidateQueries({ queryKey: versionsKey });
    },
  });

  const triggerFor = (version: MethodologyDraftVersionDto): JSX.Element | null =>
    mayDeleteVersion(version, allowed) ? (
      <Button
        size="compact-xs"
        variant="subtle"
        color="statusError"
        onClick={() => {
          remove.reset();
          setTarget(version);
          setOpened(true);
        }}
      >
        {t('methodologies.deleteVersion')}
      </Button>
    ) : null;

  const versionNumber = target?.versionNumber ?? '';

  const dialog = (
    <ConfirmModal
      opened={opened}
      title={t('methodologies.deleteVersionTitle', { version: versionNumber })}
      text={t('methodologies.deleteVersionText')}
      verb={t('methodologies.deleteVersion')}
      danger
      isPending={remove.isPending}
      onConfirm={() => {
        if (target !== null) remove.mutate(target);
      }}
      onClose={() => setOpened(false)}
    />
  );

  // ⚠ Відмова — стандартним `ErrorAlert`, як скрізь. Причину (`versionNotDraft`
  // / `versionUsedInCalculations`) сервер кладе в `detail`, зібраний із
  // каталогу за `messageKey`; заголовок коду `ECR-CALC-0409` нейтральний
  // («Conflicting methodology state», `b0f4cacd`), тож власний банер, що
  // ховав колишнє «second pair of eyes», більше не потрібен.
  //
  // ⚠ Без `onRetry`: «повторити» означало б видалення без підтвердження.
  const refusal = remove.error === null ? null : <ErrorAlert error={remove.error} />;

  return { triggerFor, dialog, refusal };
}

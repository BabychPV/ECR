import type { JSX } from 'react';
import type { PermitType } from '@/features/permits/api/types';
import { t } from '@/shared/i18n';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { pickName } from './names';

/**
 * Підтвердження «Delete permit type» (Permits, PM-08c): видаляється лише НІКОЛИ не опублікований
 * тип. Опублікований сервер відхиляє `409 ECR-PRM-4094`, тож діалог не пропонує приреченого
 * підтвердження: кнопка вимкнена, причина написана.
 */
export interface DeleteTypeConfirmProps {
  readonly opened: boolean;
  readonly type: PermitType;
  readonly isPending?: boolean | undefined;
  readonly onConfirm: () => void;
  readonly onClose: () => void;
}

export function DeleteTypeConfirm({
  opened,
  type,
  isPending,
  onConfirm,
  onClose,
}: DeleteTypeConfirmProps): JSX.Element {
  const published = type.status === 'Published';

  return (
    <ConfirmModal
      opened={opened}
      title={t('permits.editor.dialogs.deleteType.title', { name: pickName(type.nameL10n) })}
      verb={t('permits.editor.dialogs.deleteType.verb')}
      consequences={
        published
          ? [t('permits.editor.dialogs.deleteType.published')]
          : [
              t('permits.editor.dialogs.deleteType.removed', { tables: type.tables.length }),
              { text: t('permits.editor.dialogs.deleteType.unused'), note: true },
            ]
      }
      confirmDisabled={published}
      isPending={isPending}
      onConfirm={onConfirm}
      onClose={onClose}
    />
  );
}

import type { JSX } from 'react';
import type { PermitType } from '@/features/permits/api/types';
import { t } from '@/shared/i18n';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { pickName } from './names';

/**
 * Підтвердження «Open a new version» (мокап `new-version`; Permits, PM-08c): з опублікованого
 * типу відкривається чернетка наступної версії.
 *
 * ⚠ Не небезпечна дія (`danger={false}`): опублікована версія не змінюється, а дозволи далі
 * працюють з нею, доки чернетку не опублікують.
 */
export interface NewVersionConfirmProps {
  readonly opened: boolean;
  readonly type: PermitType;
  readonly isPending?: boolean | undefined;
  readonly onConfirm: () => void;
  readonly onClose: () => void;
}

export function NewVersionConfirm({
  opened,
  type,
  isPending,
  onConfirm,
  onClose,
}: NewVersionConfirmProps): JSX.Element {
  const published = type.version;

  return (
    <ConfirmModal
      opened={opened}
      title={t('permits.editor.dialogs.newVersion.title', { name: pickName(type.nameL10n) })}
      verb={t('permits.editor.dialogs.newVersion.verb')}
      danger={false}
      consequences={[
        t('permits.editor.dialogs.newVersion.draft', { next: published + 1, published }),
        { text: t('permits.editor.dialogs.newVersion.keep', { published }), note: true },
      ]}
      isPending={isPending}
      onConfirm={onConfirm}
      onClose={onClose}
    />
  );
}

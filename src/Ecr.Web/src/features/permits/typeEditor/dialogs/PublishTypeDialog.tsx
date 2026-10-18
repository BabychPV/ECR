import type { JSX } from 'react';
import { Button, Group, Modal, Stack, Text } from '@mantine/core';
import type { PermitType } from '@/features/permits/api/types';
import { t } from '@/shared/i18n';
import { ReasonModal } from '@/shared/ui/ReasonModal';
import { pickName } from './names';

/** Мінімальна довжина причини публікації: дзеркало сервера (`PermitTypeDefinitionValidator.MinReasonLength`). */
export const MinPublishReasonLength = 10;

/**
 * Діалог «Publish “<name>” vN?» (мокап `publish-type`; Permits, PM-08c).
 *
 * Два стани: тип без таблиць — діалог-відмова «Add a table before publishing» з дією
 * «Add table…» (`onAddTable`); інакше — причина зміни від 10 символів (`ReasonModal`).
 *
 * ⚠ `permitCount` необов'язковий: `PermitTypeDto` кількості дозволів не несе (вона є лише в рядку
 * переліку). Не передано — речення про наявні дозволи не показується взагалі, а не «0».
 * ⛔ Версія в заголовку — майбутня (`draftVersion ?? version`): публікується чернетка, не вже
 * опубліковане.
 */
export interface PublishTypeDialogProps {
  readonly opened: boolean;
  readonly type: PermitType;
  readonly permitCount?: number | undefined;
  readonly isPending?: boolean | undefined;
  readonly onSubmit: (reason: string) => void;

  /** Лише для відмови «без таблиць»: відкрити майстер «Add a table». */
  readonly onAddTable?: (() => void) | undefined;
  readonly onClose: () => void;
}

export function PublishTypeDialog({
  opened,
  type,
  permitCount,
  isPending,
  onSubmit,
  onAddTable,
  onClose,
}: PublishTypeDialogProps): JSX.Element {
  const name = pickName(type.nameL10n);

  if (type.tables.length === 0) {
    return (
      <Modal opened={opened} onClose={onClose} title={t('permits.editor.dialogs.publish.blockedTitle')}>
        <Stack gap="md">
          <Text size="sm">{t('permits.editor.dialogs.publish.blockedText')}</Text>
          <Group justify="flex-end" gap="xs">
            <Button variant="default" onClick={onClose} data-autofocus>
              {t('common.cancel')}
            </Button>
            {onAddTable === undefined ? null : (
              <Button variant="filled" onClick={onAddTable}>
                {t('permits.editor.dialogs.publish.addTable')}
              </Button>
            )}
          </Group>
        </Stack>
      </Modal>
    );
  }

  const version = type.draftVersion ?? type.version;
  const description =
    permitCount === undefined
      ? undefined
      : permitCount > 0
        ? t('permits.editor.dialogs.publish.usedBy', { count: permitCount })
        : t('permits.editor.dialogs.publish.unused');

  return (
    <ReasonModal
      opened={opened}
      title={t('permits.editor.dialogs.publish.title', { name, version })}
      label={t('permits.editor.dialogs.publish.reason')}
      description={description}
      confirmLabel={t('permits.editor.dialogs.publish.verb')}
      minLength={MinPublishReasonLength}
      isPending={isPending}
      onConfirm={onSubmit}
      onClose={onClose}
    />
  );
}

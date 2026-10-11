import type { JSX } from 'react';
import { Button, Group } from '@mantine/core';
import { DisabledReason } from '@/features/common/DisabledReason';
import { t } from '@/shared/i18n';
import { returnBlock, validateBlock, type ReturnBlock, type ValidateBlock } from './ownerActions';
import type { OwnerAssignmentTarget } from './types';
import { useOwnerAssignmentActions } from './useOwnerAssignmentActions';

/** Причини - літерали ключів (а не шаблон рядка), щоб сторожі ключів їх бачили. */
function blockedText(block: ValidateBlock | ReturnBlock | null): string | null {
  switch (block) {
    case null:
      return null;
    case 'alreadyValidated':
      return t('editAssign.blocked.alreadyValidated');
    case 'noRecipients':
      return t('editAssign.blocked.noRecipients');
    case 'notAllFilled':
      return t('editAssign.blocked.notAllFilled');
    case 'nothingToReturn':
      return t('editAssign.blocked.nothingToReturn');
  }
}

/**
 * Кнопки «Валідувати» і «Повернути…» для однієї передачі + їхні діалоги.
 *
 * Недоступна кнопка лишається в потоці фокуса з причиною (`DisabledReason`),
 * а не зникає: власник бачить, ЧОМУ ще не можна (не всі виконавці
 * заповнили).
 */
export function AssignmentOwnerActions({
  documentId,
  target,
}: {
  documentId: number;
  target: OwnerAssignmentTarget;
}): JSX.Element {
  const actions = useOwnerAssignmentActions(documentId);
  const validateReason = validateBlock(target);
  const returnReason = returnBlock(target);

  return (
    <>
      <Group gap="xs" wrap="nowrap">
        <DisabledReason reason={blockedText(validateReason)}>
          <Button size="xs" onClick={(event) => actions.validate(target, event.currentTarget)}>
            {t('editAssign.validate')}
          </Button>
        </DisabledReason>
        <DisabledReason reason={blockedText(returnReason)}>
          <Button size="xs" variant="default" onClick={(event) => actions.returnBack(target, event.currentTarget)}>
            {t('editAssign.return')}
          </Button>
        </DisabledReason>
      </Group>
      {actions.dialogs}
    </>
  );
}

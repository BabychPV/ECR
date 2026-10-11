import type { AssignmentRecipient, OwnerAssignmentTarget } from './types';

/*
 * Правила доступності дій власника - дзеркало серверних (§4 дизайну), але
 * лише для того, щоб не показувати кнопку, яка гарантовано дасть 422.
 * Рішення лишається за сервером: відмова все одно прийде текстом помилки.
 */

const activeRecipients = (target: OwnerAssignmentTarget): AssignmentRecipient[] =>
  target.recipients.filter((recipient) => recipient.isActive);

/** Виконавці, яких «Повернути» зачепить за замовчуванням (усі `Filled`). */
export function returnableRecipients(target: OwnerAssignmentTarget): AssignmentRecipient[] {
  return activeRecipients(target).filter((recipient) => recipient.status === 'Filled');
}

export type ValidateBlock = 'alreadyValidated' | 'noRecipients' | 'notAllFilled';

/** Чому «Валідувати» недоступне; `null` - доступне. */
export function validateBlock(target: OwnerAssignmentTarget): ValidateBlock | null {
  if (target.validation === 'Validated') return 'alreadyValidated';

  const active = activeRecipients(target);
  if (active.length === 0) return 'noRecipients';
  if (active.some((recipient) => recipient.status !== 'Filled')) return 'notAllFilled';

  return null;
}

export type ReturnBlock = 'nothingToReturn';

/** Чому «Повернути» недоступне; `null` - доступне. Повернути можна й `Validated`. */
export function returnBlock(target: OwnerAssignmentTarget): ReturnBlock | null {
  return returnableRecipients(target).length === 0 ? 'nothingToReturn' : null;
}

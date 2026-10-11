/**
 * Форма передачі місяця третій стороні, потрібна діям власника
 * (`DESIGN-OWNER-EDIT-GRANTS`, §6 API: `GET …/edit-assignments`).
 *
 * ⚠ Типи описані тут, а не взяті зі `schema.d.ts`: контракт EA-3 з'явиться в
 * `dev/integration` пізніше, а клієнтська лінія не чекає сервера. Коли схема
 * з'явиться, цей файл замінюється аліасами на `components['schemas'][…]`;
 * споживачі (`ownerActions`, `api`) від цього не змінюються.
 */
export type RecipientStatus = 'Assigned' | 'Filled' | 'Returned';

export type SliceValidation = 'NotValidated' | 'Validated';

export interface AssignmentRecipient {
  readonly userId: number;
  readonly displayName: string;
  readonly status: RecipientStatus;
  readonly isActive: boolean;
  readonly returnReason?: string | null | undefined;
}

/** Передача, над якою власник виконує «Валідувати» / «Повернути». */
export interface OwnerAssignmentTarget {
  readonly id: number;

  /** Уже перекладена назва зрізу («January»), для заголовків діалогів. */
  readonly sliceLabel: string;
  readonly validation: SliceValidation;
  readonly version: number;
  readonly recipients: readonly AssignmentRecipient[];
}

/** `POST …/{id}/validate`. */
export interface ValidateAssignmentRequest {
  readonly baseVersion: number;
}

/** `POST …/{id}/return`; без `userIds` повертаються всі `Filled`. */
export interface ReturnAssignmentRequest {
  readonly userIds?: readonly number[];
  readonly reason: string;
  readonly baseVersion: number;
}

/** Межі коментаря повернення (`ReturnReason`, §5 дизайну). */
export const ReturnReasonMaxLength = 500;

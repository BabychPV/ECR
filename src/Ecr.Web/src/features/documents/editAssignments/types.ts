/**
 * Контракт «передачі місяця» (EA-5a) — `DESIGN-OWNER-EDIT-GRANTS-2026-10-11.md` §6.
 *
 * ⚠ Типи рукописні, бо серверний контракт (EA-3/EA-4) ще не в `schema.d.ts`. Форма полів — з
 * дизайну; коли «Аналіз» запушить `openapi.snapshot.json`, цей файл замінюється аліасами на
 * `components['schemas'][…]` без змін у компонентах.
 */

/** Зріз передачі; усі поля необов'язкові, жодного = «весь документ». */
export interface AssignmentSlice {
  readonly sheetCode?: string | null | undefined;
  readonly tableCode?: string | null | undefined;
  readonly monthNumber?: number | null | undefined;
  readonly periodKey?: number | null | undefined;
}

/** Похідний статус зрізу (§3.1). */
export type AssignmentStatus = 'Open' | 'Assigned' | 'InProgress' | 'Filled' | 'Validated' | 'Returned';

export type RecipientStatus = 'Assigned' | 'Filled' | 'Returned';

/** Чи бачить виконавець документ; `Unknown` — видимість через AD-групу система поіменно не знає. */
export type RecipientVisibility = 'Visible' | 'NotVisible' | 'Unknown';

export interface UserRef {
  readonly userId: number;
  readonly displayName: string;
}

export interface AssignmentRecipient {
  readonly userId: number;
  readonly displayName: string;
  readonly status: RecipientStatus;
  readonly filledAt?: string | null | undefined;
  readonly returnedAt?: string | null | undefined;
  readonly returnReason?: string | null | undefined;
  readonly isActive: boolean;
  readonly visibility: RecipientVisibility;
}

export interface EditAssignment {
  readonly id: number;
  readonly slice: AssignmentSlice;
  readonly isExclusive: boolean;
  readonly status: AssignmentStatus | (string & {});
  readonly validatedAt?: string | null | undefined;
  readonly validatedBy?: UserRef | null | undefined;
  readonly delegatedBy?: UserRef | null | undefined;
  readonly note?: string | null | undefined;
  readonly recipients: readonly AssignmentRecipient[];
  readonly appliedSet?: { readonly id: number; readonly name: string } | null | undefined;
  readonly version: string;
}

export interface EditAssignmentsList {
  readonly owner: UserRef;
  readonly canManage: boolean;
  readonly assignments: readonly EditAssignment[];
}

export interface UpsertAssignmentRequest {
  readonly slice: AssignmentSlice;
  readonly recipientUserIds: readonly number[];
  readonly isExclusive: boolean;
  readonly note?: string | undefined;
  readonly appliedSetId?: number | undefined;
  readonly baseVersion?: string | undefined;
}

export interface Candidate {
  readonly userId: number;
  readonly displayName: string;
  readonly visibility: RecipientVisibility;
}

export interface EditorSetMember {
  readonly userId: number;
  readonly displayName: string;
  readonly visibility?: RecipientVisibility | undefined;
}

export interface EditorSet {
  readonly id: number;
  readonly name: string;
  readonly members: readonly EditorSetMember[];
}

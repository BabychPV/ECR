import type { components } from '@/api/schema';

/**
 * Контракт «передачі місяця» (EA-5): ТІЛЬКИ псевдоніми згенерованої схеми (`D-137`) - форма полів приходить із
 * сервера (`DocumentEditAssignmentsController`), а не вгадується. Тест `api.contract.test.ts` звіряє кожен виклик
 * `api.ts` зі схемою (метод, шлях, тіло).
 */
type Schemas = components['schemas'];

/** Зріз передачі: усі чотири селектори присутні, `null` = «не обмежено» (жодного = весь документ). */
export type AssignmentSlice = Schemas['EditAssignmentSliceDto'];

export type EditAssignment = Schemas['EditAssignmentDto'];
export type EditAssignmentsList = Schemas['EditAssignmentListDto'];
export type AssignmentRecipient = Schemas['EditAssignmentRecipientDto'];
export type Candidate = Schemas['EditAssignmentCandidateDto'];
export type EditorSet = Schemas['EditorSetDto'];
export type EditorSetMember = Schemas['EditorSetMemberDto'];
export type UpsertAssignmentRequest = Schemas['UpsertEditAssignmentRequest'];
export type ReturnAssignmentRequest = Schemas['ReturnEditAssignmentRequest'];
export type ValidateAssignmentRequest = Schemas['ValidateEditAssignmentRequest'];
export type CopyAssignmentRequest = Schemas['CopyEditAssignmentRequest'];
export type ApplyEditorSetRequest = Schemas['ApplyEditorSetRequest'];
export type SaveEditorSetRequest = Schemas['SaveEditorSetRequest'];
export type EditAssignmentBatch = Schemas['EditAssignmentBatchDto'];

/** Рядок «моїх» передач (шухляда «My tasks», смуга виконавця). */
export type MyEditAssignment = Schemas['MyEditAssignmentDto'];
export type MyEditAssignmentsView = Schemas['MyEditAssignmentsDto'];

/**
 * Значення `status` на сервері - рядок; клієнт звужує його до відомих (невідоме малюється нейтрально).
 * Похідний статус зрізу: `Open`/`Assigned`/`InProgress`/`Filled`/`Validated`/`Returned`.
 */
export type AssignmentStatus = 'Open' | 'Assigned' | 'InProgress' | 'Filled' | 'Validated' | 'Returned';

export type RecipientStatus = 'Assigned' | 'Filled' | 'Returned';

/** Межа тексту повернення/примітки (дизайн §5: `reasonTooLong` > 500). */
export const ReturnReasonMaxLength = 500;

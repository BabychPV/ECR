import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';

/*
 * «Передача місяця третій стороні» (EA-5c): секції «Assigned to me» і «Awaiting my validation» у «My tasks».
 *
 * ⚠ Типи записані тут (навмисно БЕЗ суфікса `Dto`, щоб не вдавати схему - сторож D-137), а не беруться зі `schema.d.ts`: серверна частина (EA-4) ще не злита, контракт
 * `GET /api/v1/me/edit-assignments` у знімок не потрапив. Коли потрапить - замінити на `Schemas[...]`
 * (форму узгоджено з DESIGN-OWNER-EDIT-GRANTS §6: лише рядки викликача, без імен інших виконавців).
 */

/** Стан передачі для виконавця (`Assigned/Returned` - потрібна дія; `Filled` - чекає валідації власника). */
export type EditAssignmentStatus = 'Assigned' | 'Returned' | 'Filled';

export interface MyEditAssignment {
  readonly assignmentId: number;
  readonly documentId: number;
  readonly documentKey: string;

  /** Передане: місяць/зріз, уже локалізований сервером (напр. «January»). */
  readonly slice: string;
  readonly status: EditAssignmentStatus;

  /** Примітка делегатора. */
  readonly note?: string | null;

  /** Коментар повернення - лише для `Returned`. */
  readonly returnReason?: string | null;

  /** Лише для `Filled` (рядок власника): коли виконавець заповнив. */
  readonly filledAt?: string | null;
}

export interface MyEditAssignmentsView {
  /** Передані МЕНІ на заповнення (`role=assignee`): `Assigned`/`Returned`. */
  readonly assignedToMe: readonly MyEditAssignment[];

  /** Заповнені виконавцями, чекають МОЄЇ валідації (`role=owner`): `Filled`. */
  readonly awaitingMyValidation: readonly MyEditAssignment[];
}

/** Ключ запиту (DESIGN §7); інвалідується будь-якою мутацією передач. */
export const myEditAssignmentsKey = ['edit-assignments', 'mine'] as const;

/** Опитування, поки шухляда закрита, не працює: лише при фокусі/навігації. Відкрита шухляда - раз на хвилину. */
const OpenedPollMs = 60_000;

export function myEditAssignmentsUrl(): string {
  return '/api/v1/me/edit-assignments';
}

/**
 * Адреса документа передачі.
 *
 * ⛔ Літерал, а не `generatePath` (`features` не імпортують `app`, як `myTaskDocumentHref`).
 * Для «Validate…» - шторка передач документа (`?panel=edit-assignments`, DESIGN §8).
 */
export function editAssignmentHref(documentId: number, validate: boolean): string {
  const base = `/documents/${String(documentId)}`;

  return validate ? `${base}?panel=edit-assignments` : base;
}

export function pendingEditAssignmentCount(data: MyEditAssignmentsView | undefined): number {
  return (data?.assignedToMe.length ?? 0) + (data?.awaitingMyValidation.length ?? 0);
}

export function useMyEditAssignments(opened: boolean): UseQueryResult<MyEditAssignmentsView> {
  return useQuery({
    queryKey: myEditAssignmentsKey,
    queryFn: async (): Promise<MyEditAssignmentsView> => {
      const dto = await apiFetch<Partial<MyEditAssignmentsView>>(myEditAssignmentsUrl());

      return { assignedToMe: dto.assignedToMe ?? [], awaitingMyValidation: dto.awaitingMyValidation ?? [] };
    },
    refetchOnWindowFocus: true,
    refetchInterval: opened ? OpenedPollMs : false,
    staleTime: opened ? 0 : 30_000,
  });
}

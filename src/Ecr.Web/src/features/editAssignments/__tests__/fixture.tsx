import { vi } from 'vitest';
import { makeSlice } from '../model';
import type { AssignmentRecipient, AssignmentSlice, EditAssignment, EditAssignmentsList, MyEditAssignment } from '../types';

/**
 * Мок сервера для тестів шторки «Доступ до редагування» (EA-5a).
 *
 * ⛔ Контракт — з `DESIGN-OWNER-EDIT-GRANTS-2026-10-11.md` §6; реального сервера ще немає, тож відповіді
 * тут — наші припущення про форму. Через тест іде той самий `apiFetch`, що й у продукті.
 */
export interface Sent {
  readonly method: string;
  readonly url: string;
  readonly body: Record<string, unknown> | undefined;
}

export const DocumentId = 42;

export function assignment(
  overrides: Partial<Omit<EditAssignment, 'slice'>> & { readonly id: number; readonly slice: Partial<AssignmentSlice> },
): EditAssignment {
  return {
    isExclusive: true,
    status: 'Assigned',
    recipients: [],
    version: `v${String(overrides.id)}`,
    appliedSetId: null,
    appliedSetName: null,
    delegatedByDisplayName: 'Owner Olena',
    delegatedByUserId: 1,
    modifiedAt: '2026-10-01T00:00:00Z',
    note: null,
    validatedAt: null,
    validatedByDisplayName: null,
    ...overrides,
    slice: makeSlice(overrides.slice),
  };
}

function recipient(
  userId: number,
  displayName: string,
  status: 'Assigned' | 'Filled' | 'Returned',
  returnReason: string | null = null,
): AssignmentRecipient {
  return { userId, displayName, status, isActive: true, filledAt: null, returnCount: 0, returnReason, returnedAt: null };
}

export const January = assignment({
  id: 1,
  slice: { monthNumber: 1 },
  status: 'Filled',
  recipients: [recipient(7, 'Ivanov', 'Filled'), recipient(8, 'Petrenko', 'Filled')],
});

export const February = assignment({
  id: 2,
  slice: { monthNumber: 2 },
  status: 'Returned',
  recipients: [recipient(7, 'Ivanov', 'Returned', 'Check row 4')],
});

export const December = assignment({
  id: 3,
  slice: { monthNumber: 12 },
  status: 'Assigned',
  recipients: [recipient(7, 'Ivanov', 'Assigned')],
});

export interface ServerOptions {
  readonly list?: EditAssignmentsList;
  readonly listStatus?: number;
  readonly putStatus?: number;
  readonly grant?: string;
  readonly simulation?: boolean;
}

export function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

export function mockServer(sent: Sent[], options: ServerOptions = {}): void {
  const list: EditAssignmentsList = options.list ?? {
    ownerDisplayName: 'Owner Olena',
    canManage: true,
    assignments: [January, February],
  };

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = String(init?.method ?? 'GET');
      const body = init?.body === undefined ? undefined : (JSON.parse(String(init.body)) as Record<string, unknown>);

      if (url.includes('/api/v1/me/editor-sets')) {
        if (method === 'POST') {
          sent.push({ method, url, body });
          return jsonResponse({ id: 55, name: String(body?.name), members: [], version: 'sv55' });
        }
        return jsonResponse([
          { id: 3, name: 'Dept 5', members: [{ userId: 9, displayName: 'Sydorenko', isActive: true }], version: 'sv3' },
        ]);
      }

      if (url.includes('/api/v1/me')) {
        return jsonResponse({
          denies: [], grants: { 'Project:7': options.grant ?? 'Write' }, isSimulation: options.simulation ?? false, language: 'en',
          mustChangePassword: false, permissions: ['Document.View'], simulatedForUserId: null, userId: 1, userName: 'owner',
        });
      }

      if (url.includes('/edit-assignments/candidates')) {
        return jsonResponse([
          { userId: 11, displayName: 'Koval' },
          { userId: 12, displayName: 'Kovalenko' },
        ]);
      }

      if (url.includes('/edit-assignments')) {
        if (method === 'GET') {
          return options.listStatus === undefined || options.listStatus === 200
            ? jsonResponse(list)
            : jsonResponse({ title: 'Forbidden', status: options.listStatus, errorCode: 'ECR-ACCS-0403' }, options.listStatus);
        }
        sent.push({ method, url, body });
        if (method === 'POST' && url.endsWith('/edit-assignments') && options.putStatus !== undefined && options.putStatus !== 200) {
          return jsonResponse(
            {
              title: 'Validation failed',
              status: 422,
              detail: 'You cannot hand over sheets you cannot edit yourself.',
              errorCode: 'ECR-REQ-0422',
              messageKey: 'err.ECR-REQ-0422.editAssignmentDelegationExceedsOwnRights',
              correlationId: 'c-1',
            },
            422,
          );
        }
        return method === 'DELETE' ? new Response(null, { status: 204 }) : jsonResponse({ ...January, id: 99 });
      }

      throw new Error(`неочікуваний запит у тесті: ${method} ${url}`);
    }),
  );
}

/** Рядок `GET /me/edit-assignments` у формі сервера (`MyEditAssignmentDto`). */
export function myAssignment(
  overrides: Partial<Omit<MyEditAssignment, 'slice'>> & { readonly assignmentId: number; readonly documentId: number; readonly month?: number },
): MyEditAssignment {
  const { month, ...rest } = overrides;

  return {
    documentKey: `DOC-${String(overrides.documentId)}`,
    delegatedByDisplayName: 'Owner Olena',
    filledCount: 0,
    recipientCount: 1,
    note: null,
    returnReason: null,
    status: 'Assigned',
    statusChangedAt: null,
    version: 'v1',
    ...rest,
    slice: makeSlice({ monthNumber: month ?? 1 }),
  };
}

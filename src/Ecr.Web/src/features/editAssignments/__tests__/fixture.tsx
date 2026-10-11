import { vi } from 'vitest';
import type { EditAssignment, EditAssignmentsList } from '../types';

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

export function assignment(overrides: Partial<EditAssignment> & Pick<EditAssignment, 'id' | 'slice'>): EditAssignment {
  return {
    isExclusive: true,
    status: 'Assigned',
    recipients: [],
    version: `v${String(overrides.id)}`,
    ...overrides,
  };
}

export const January = assignment({
  id: 1,
  slice: { monthNumber: 1 },
  status: 'Filled',
  recipients: [
    { userId: 7, displayName: 'Ivanov', status: 'Filled', isActive: true, visibility: 'Visible' },
    { userId: 8, displayName: 'Petrenko', status: 'Filled', isActive: true, visibility: 'Unknown' },
  ],
});

export const February = assignment({
  id: 2,
  slice: { monthNumber: 2 },
  status: 'Returned',
  recipients: [
    { userId: 7, displayName: 'Ivanov', status: 'Returned', returnReason: 'Check row 4', isActive: true, visibility: 'Visible' },
  ],
});

export const December = assignment({
  id: 3,
  slice: { monthNumber: 12 },
  status: 'Assigned',
  recipients: [{ userId: 7, displayName: 'Ivanov', status: 'Assigned', isActive: true, visibility: 'Visible' }],
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
    owner: { userId: 1, displayName: 'Owner Olena' },
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
          return jsonResponse({ id: 55, name: String(body?.name), members: [] });
        }
        return jsonResponse([
          { id: 3, name: 'Dept 5', members: [{ userId: 9, displayName: 'Sydorenko', visibility: 'NotVisible' }] },
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
          { userId: 11, displayName: 'Koval', visibility: 'NotVisible' },
          { userId: 12, displayName: 'Kovalenko', visibility: 'Visible' },
        ]);
      }

      if (url.includes('/edit-assignments')) {
        if (method === 'GET') {
          return options.listStatus === undefined || options.listStatus === 200
            ? jsonResponse(list)
            : jsonResponse({ title: 'Forbidden', status: options.listStatus, errorCode: 'ECR-ACCS-0403' }, options.listStatus);
        }
        sent.push({ method, url, body });
        if (method === 'PUT' && options.putStatus !== undefined && options.putStatus !== 200) {
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

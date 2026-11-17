import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  approvalRouteKey,
  fetchApproverRoles,
  fetchProjectTemplateRoutes,
  fetchScopedApprovalRoute,
  previewScopedApprovalRoute,
  replaceScopedApprovalRoute,
  type ApprovalRouteScope,
} from '../approvalRoutesApi';

/**
 * ✎ B155: МЕТОД, АДРЕСА й ТІЛО кожного виклику маршрутів областей. Правила (права, перезапуск поданих, OR-правило
 * «роль кроку або загальний Approver») ухвалює сервер і перевіряють `TemplateApprovalRouteHttpTests` /
 * `TemplateApprovalRouteAccessTests` — повторювати їх тут на замокненому `fetch` означало б перевіряти заглушку.
 */

function mockServer(): { calls: { url: string; method: string; body: unknown }[] } {
  const calls: { url: string; method: string; body: unknown }[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, init?: RequestInit) => {
      calls.push({
        url,
        method: init?.method ?? 'GET',
        body: typeof init?.body === 'string' ? (JSON.parse(init.body) as unknown) : undefined,
      });

      return Promise.resolve(
        new Response(JSON.stringify({}), { status: 200, headers: { 'Content-Type': 'application/json' } }),
      );
    }),
  );

  return { calls };
}

const scopes: [ApprovalRouteScope, string][] = [
  [{ kind: 'template', templateId: 7 }, '/api/v1/templates/7/approval-route'],
  [{ kind: 'projectTemplate', projectId: 3, templateId: 7 }, '/api/v1/projects/3/templates/7/approval-route'],
  [{ kind: 'default' }, '/api/v1/approval-route/default'],
];

describe('approvalRoutesApi (B155)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it.each(scopes)('читає, замінює й рахує перезапуск за адресою області %#', async (scope, url) => {
    const server = mockServer();

    await fetchScopedApprovalRoute(scope);
    await replaceScopedApprovalRoute(scope, [11, 12]);
    await previewScopedApprovalRoute(scope, [11]);
    await previewScopedApprovalRoute(scope);

    expect(server.calls.map((c) => `${c.method} ${c.url}`)).toEqual([
      `GET ${url}`,
      `PUT ${url}`,
      `POST ${url}/preview`,
      `POST ${url}/preview`,
    ]);
    expect(server.calls[1]?.body).toEqual({ roleIds: [11, 12] });
    expect(server.calls[2]?.body).toEqual({ roleIds: [11] });
    // Без набору — «будь-яка зміна»: сервер рахує всі подані аркуші області.
    expect(server.calls[3]?.body).toEqual({ roleIds: null });
  });

  it('шаблони проєкту з маршрутом і ролі для кроків', async () => {
    const server = mockServer();

    await fetchProjectTemplateRoutes(5);
    await fetchApproverRoles();

    expect(server.calls.map((c) => `${c.method} ${c.url}`)).toEqual([
      'GET /api/v1/projects/5/template-routes',
      'GET /api/v1/roles/approvers',
    ]);
  });

  it('ключ кешу різний для різних областей', () => {
    const keys = scopes.map(([scope]) => JSON.stringify(approvalRouteKey(scope)));
    expect(new Set(keys).size).toBe(scopes.length);
  });
});

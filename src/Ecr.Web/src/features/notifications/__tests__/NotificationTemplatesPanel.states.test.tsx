import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { NotificationTemplatesPanel } from '@/features/notifications/NotificationTemplatesPanel';
import { renderWithQuery } from '@/test/render';

/**
 * Шаблони повідомлень — стани, яких не тримає `NotificationTemplatesPanel.test.tsx`: немає жодної
 * мови, адресати ще їдуть, друга половина запису відхилена.
 *
 * Мутаційні докази (лише локально): без гілки `languages.data.length === 0` — порожні селектори
 * замість пояснення; без перечитування в `onError` — після відмови тіла екран лишає стару тему,
 * хоча вона вже записана.
 */

const Subject = 'ECR: period {period} opened, project {project}';
const Body = 'A new reporting period {period} has opened for project {project}.';

interface Options {
  readonly languages?: unknown[];
  readonly rules?: 'ok' | 'pending';
  readonly bodyWrite?: 'ok' | 'refuse';
}

interface Call {
  readonly method: string;
  readonly path: string;
}

function json(body: unknown, status = 200): Promise<Response> {
  return Promise.resolve(
    new Response(JSON.stringify(body), {
      status,
      headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
    }),
  );
}

function mockServer(options: Options = {}): Call[] {
  const calls: Call[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const path = url.split('?')[0] ?? '';
      const method = (init?.method ?? 'GET').toUpperCase();
      calls.push({ method, path });

      if (path === '/api/v1/me') {
        return json({ userId: 1, userName: 'admin', language: 'en', permissions: ['System.ManageLocalization'] });
      }
      if (path === '/api/v1/languages') return json(options.languages ?? [{ code: 'en', nameNative: 'English', isDefault: true }]);
      if (path === '/api/v1/notifications/rules' || path === '/api/v1/notifications/channels') {
        if (options.rules === 'pending') return new Promise<Response>(() => undefined);

        return json(path.endsWith('rules') ? { eventKinds: [], rules: [] } : []);
      }
      if (path === '/api/v1/ui-strings' && method === 'GET') {
        return json({
          languageCode: 'en',
          items: [
            { key: 'notifications.periodOpened.subject', reference: Subject, value: Subject },
            { key: 'notifications.periodOpened.body', reference: Body, value: Body },
          ],
        });
      }
      if (path.endsWith('.body') && method === 'PUT' && options.bodyWrite === 'refuse') {
        return json(
          { title: 'Unprocessable', status: 422, detail: 'placeholder mismatch', errorCode: 'ECR-REQ-0422', correlationId: 'c-tpl' },
          422,
        );
      }
      if (path.startsWith('/api/v1/ui-strings/') && method === 'PUT') return json({ revision: 43 });

      throw new Error(`Непередбачена адреса: ${method} ${path}`);
    }),
  );

  return calls;
}

const subjectField = async (): Promise<HTMLElement> =>
  screen.findByLabelText(/notificationTemplates\.subject⟧/, {}, { timeout: 5000 });

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('NotificationTemplatesPanel — стани (2)', () => {
  it('жодної мови в системі — пояснення словами, а не порожні селектори й форма', async () => {
    mockServer({ languages: [] });
    renderWithQuery(<NotificationTemplatesPanel />);

    expect(await screen.findByText('⟦notificationTemplates.noLanguages⟧')).toBeDefined();
    expect(screen.queryByLabelText(/notificationTemplates\.language⟧/)).toBeNull();
    expect(screen.queryByLabelText(/notificationTemplates\.subject⟧/)).toBeNull();
  });

  it('адресати ще їдуть — блок «хто отримає» зайнятий (aria-busy), а не «ніхто», і редактор уже доступний', async () => {
    mockServer({ rules: 'pending' });
    const { container } = renderWithQuery(<NotificationTemplatesPanel />);

    expect(((await subjectField()) as HTMLInputElement).value).toBe(Subject);
    expect(container.querySelector('[data-notification-recipients="pending"][aria-busy="true"]')).not.toBeNull();
    expect(screen.queryByText('⟦notificationTemplates.recipientsNone⟧')).toBeNull();
    expect(screen.getByText('⟦notificationTemplates.recipients⟧')).toBeDefined();
  });

  it('тема записалась, тіло відхилено — каталог перечитується, чернетка лишається незбереженою', async () => {
    const calls = mockServer({ bodyWrite: 'refuse' });
    renderWithQuery(<NotificationTemplatesPanel />);

    fireEvent.change(await subjectField(), { target: { value: 'ECR: {project} {period}' } });
    fireEvent.change(screen.getByLabelText(/notificationTemplates\.body⟧/), {
      target: { value: 'Period {period} for {project}.' },
    });
    const reads = (): number => calls.filter((c) => c.method === 'GET' && c.path === '/api/v1/ui-strings').length;
    const before = reads();

    fireEvent.click(screen.getByRole('button', { name: '⟦notificationTemplates.save⟧' }));

    await waitFor(() => expect(reads()).toBeGreaterThan(before));
    // Послідовно: тема — першою, тіло — після її успіху.
    expect(calls.filter((c) => c.method === 'PUT').map((c) => c.path)).toEqual([
      '/api/v1/ui-strings/en/notifications.periodOpened.subject',
      '/api/v1/ui-strings/en/notifications.periodOpened.body',
    ]);
    expect(screen.getByTestId('notification-templates-unsaved')).toBeDefined();
  });
});

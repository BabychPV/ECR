import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SmtpSettingsPanel } from '@/features/notifications/SmtpSettingsPanel';
import { testTheme } from '@/test/render';

/**
 * ÐÐ°Ð»Ð°ÑˆÑ‚ÑƒÐ²Ð°Ð½Ð½Ñ SMTP (`D-263`).
 *
 * â›” Ð—Ð°Ñ€Ð°Ð´Ð¸ Ñ‡Ð¾Ð³Ð¾ Ñ„Ð°Ð¹Ð»: (1) Ð¿Ð°Ñ€Ð¾Ð»ÑŒ ÐÐ†ÐšÐžÐ›Ð˜ Ð½Ðµ Ð¿Ð¾Ð²ÐµÑ€Ñ‚Ð°Ñ”Ñ‚ÑŒÑÑ Ð· ÑÐµÑ€Ð²ÐµÑ€Ð° Ð¹ Ð½Ðµ Ð·'ÑÐ²Ð»ÑÑ”Ñ‚ÑŒÑÑ Ñƒ Ñ„Ð¾Ñ€Ð¼Ñ– â€” Ð¿Ð¾Ð»Ðµ
 * Ð¿Ð¾Ñ€Ð¾Ð¶Ð½Ñ”, Ð° Ð¿Ð¾Ñ€Ð¾Ð¶Ð½Ñ” = Â«Ð½Ðµ Ð·Ð¼Ñ–Ð½ÑŽÐ²Ð°Ñ‚Ð¸Â»; (2) Ð²Ñ–Ð´Ð¼Ð¾Ð²Ð° `GET` Ð½Ðµ Ð²Ð¸Ð³Ð»ÑÐ´Ð°Ñ” Ð¿Ð¾Ñ€Ð¾Ð¶Ð½ÑŒÐ¾ÑŽ Ñ„Ð¾Ñ€Ð¼Ð¾ÑŽ; (3) Ð¿Ñ€Ð¾Ð±Ð°
 * ÑˆÐ»Ðµ Ð»Ð¸ÑÑ‚ Ð½Ð° Ð²Ð²ÐµÐ´ÐµÐ½Ñƒ Ð°Ð´Ñ€ÐµÑÑƒ.
 */

const Refusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'Ð½Ð°Ð»Ð°ÑˆÑ‚ÑƒÐ²Ð°Ð½Ð½Ñ SMTP Ð¿Ñ€Ð¾Ñ‡Ð¸Ñ‚Ð°Ñ‚Ð¸ Ð½Ðµ Ð²Ð´Ð°Ð»Ð¾ÑÑ',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-smtp-1',
  messageKey: 'err.ECR-SYS-0500.unexpected',
};

const stored = {
  host: 'smtp.corp.example',
  port: 587,
  encryptionMode: 'StartTls',
  fromAddress: 'ecr@corp.example',
  fromName: 'ECR',
  authMode: 'Password',
  userName: 'mailer',
  hasPassword: true,
  isEnabled: true,
  source: 'database',
  configured: true,
  updatedAt: '2026-10-01T08:00:00Z',
};

interface Call {
  readonly method: string;
  readonly path: string;
  readonly body: unknown;
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json',
    },
  });
}

function mockServer(mode: 'ok' | 'refuse', testResult: unknown = { ok: true }): Call[] {
  const calls: Call[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = String(input).split('?')[0] ?? '';
      const method = init?.method ?? 'GET';
      calls.push({
        method,
        path,
        body: typeof init?.body === 'string' ? JSON.parse(init.body) : null,
      });

      if (path.endsWith('/api/v1/notifications/smtp/test')) return json(testResult);
      if (path.endsWith('/api/v1/notifications/smtp')) {
        if (mode === 'refuse') return json(Refusal, 500);

        return json(stored);
      }

      return json(null);
    }),
  );

  return calls;
}

function show(): void {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <SmtpSettingsPanel />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SmtpSettingsPanel', () => {
  it('Ð²Ñ–Ð´Ð¼Ð¾Ð²Ð° GET â€” Ð¿Ñ€Ð¸Ñ‡Ð¸Ð½Ð° Ð· ÐºÐ¾Ð´Ð¾Ð¼, Ñ„Ð¾Ñ€Ð¼Ð¸ Ð½ÐµÐ¼Ð°Ñ”', async () => {
    mockServer('refuse');
    show();

    const alert = await waitFor(() => screen.getByRole('alert'), {
      timeout: 10_000,
    });

    expect(alert.textContent ?? '').toContain('ECR-SYS-0500');
    expect(screen.queryByLabelText(/smtp\.host/)).toBeNull();
  }, 30_000);

  it('Ð·Ð±ÐµÑ€ÐµÐ¶ÐµÐ½Ñ– Ð·Ð½Ð°Ñ‡ÐµÐ½Ð½Ñ Ð¿Ð¾ÐºÐ°Ð·Ð°Ð½Ñ–, Ð° Ð¿Ð°Ñ€Ð¾Ð»ÑŒ â€” Ð½Ñ–ÐºÐ¾Ð»Ð¸: Ð¿Ð¾Ð»Ðµ Ð¿Ð¾Ñ€Ð¾Ð¶Ð½Ñ”, Ñ” Ð»Ð¸ÑˆÐµ Ð¿Ñ–Ð´ÐºÐ°Ð·ÐºÐ° Â«Ð·Ð±ÐµÑ€ÐµÐ¶ÐµÐ½Ð¾Â»', async () => {
    mockServer('ok');
    show();

    const host = await screen.findByLabelText(/smtp\.host/);
    expect((host as HTMLInputElement).value).toBe('smtp.corp.example');

    const password = document.querySelector('input[type="password"]') as HTMLInputElement;
    expect(password.value).toBe('');
    expect(document.body.textContent ?? '').toContain('smtp.passwordStored');
    expect(document.body.textContent ?? '').toContain('smtp.source.database');
  }, 30_000);

  it('Ð·Ð±ÐµÑ€ÐµÐ¶ÐµÐ½Ð½Ñ Ð±ÐµÐ· Ð½Ð¾Ð²Ð¾Ð³Ð¾ Ð¿Ð°Ñ€Ð¾Ð»Ñ ÑˆÐ»Ðµ password: null (Â«Ð½Ðµ Ð·Ð¼Ñ–Ð½ÑŽÐ²Ð°Ñ‚Ð¸Â»), Ð· Ð¿Ð°Ñ€Ð¾Ð»ÐµÐ¼ â€” Ð»Ð¸ÑˆÐµ Ð²Ð²ÐµÐ´ÐµÐ½Ðµ', async () => {
    const calls = mockServer('ok');
    show();

    const host = await screen.findByLabelText(/smtp\.host/);
    fireEvent.change(host, { target: { value: 'relay.corp.example' } });
    fireEvent.click(screen.getByText(/smtp\.save/));

    await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true));
    const first = calls.find((c) => c.method === 'PUT')?.body as Record<string, unknown>;
    expect(first.host).toBe('relay.corp.example');
    expect(first.password).toBeNull();
    expect(first.clearPassword).toBe(false);
    expect(JSON.stringify(first)).not.toContain('Sup3r');

    const typed = document.querySelector('input[type="password"]') as HTMLInputElement;
    fireEvent.change(typed, { target: { value: 'Sup3r-new' } });
    fireEvent.click(screen.getByText(/smtp\.save/));

    await waitFor(() => expect(calls.filter((c) => c.method === 'PUT')).toHaveLength(2));
    expect((calls.filter((c) => c.method === 'PUT')[1]?.body as Record<string, unknown>).password).toBe('Sup3r-new');
  }, 30_000);

  it('422 smtpPasswordReentryRequired â€” Ð¿Ñ–Ð´ÐºÐ°Ð·ÐºÐ° Ð¿Ñ–Ð´ Ð¿Ð¾Ð»ÐµÐ¼ Ð¿Ð°Ñ€Ð¾Ð»Ñ, Ñ– Ð·Ð½Ð¸ÐºÐ°Ñ”, Ñ‰Ð¾Ð¹Ð½Ð¾ Ð¿Ð°Ñ€Ð¾Ð»ÑŒ Ð²Ð²Ð¾Ð´ÑÑ‚ÑŒ', async () => {
    const reentry = {
      type: 'about:blank',
      title: 'Unprocessable Entity',
      status: 422,
      detail: 'x',
      errorCode: 'ECR-REQ-0422',
      correlationId: 'cid-smtp-2',
      messageKey: 'err.ECR-REQ-0422.smtpPasswordReentryRequired',
      field: 'password',
    };
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) =>
        (init?.method ?? 'GET') === 'PUT' ? json(reentry, 422) : json(stored),
      ),
    );
    show();

    const host = await screen.findByLabelText(/smtp\.host/);
    fireEvent.change(host, { target: { value: 'evil.example' } });
    fireEvent.click(screen.getByText(/smtp\.save/));

    await waitFor(() =>
      expect(document.body.textContent ?? '').toContain('err.ECR-REQ-0422.smtpPasswordReentryRequired'),
    );
    const password = document.querySelector('input[type="password"]') as HTMLInputElement;
    expect(password.getAttribute('data-invalid')).toBe('true');

    fireEvent.change(password, { target: { value: 'new' } });
    await waitFor(() => expect(password.getAttribute('data-invalid')).toBeNull());
  }, 30_000);

  it('Â«ÐÐ°Ð´Ñ–ÑÐ»Ð°Ñ‚Ð¸ Ñ‚ÐµÑÑ‚Â» ÑˆÐ»Ðµ POST Ð½Ð° Ð²Ð²ÐµÐ´ÐµÐ½Ñƒ Ð°Ð´Ñ€ÐµÑÑƒ', async () => {
    const calls = mockServer('ok', {
      ok: false,
      error: 'x',
      messageKey: 'notifications.test.smtp.dns',
    });
    show();

    const to = await screen.findByLabelText(/smtp\.testTo/);
    fireEvent.change(to, { target: { value: ' me@corp.example ' } });
    fireEvent.click(screen.getByText(/smtp\.testSend/));

    await waitFor(() => expect(calls.some((c) => c.path.endsWith('/smtp/test'))).toBe(true));
    const post = calls.find((c) => c.path.endsWith('/smtp/test'));
    expect(post?.method).toBe('POST');
    expect(post?.body).toEqual({ to: 'me@corp.example' });
  }, 30_000);
});

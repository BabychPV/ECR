import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SmtpSettingsPanel } from '@/features/notifications/SmtpSettingsPanel';
import { testTheme } from '@/test/render';

/**
 * Налаштування SMTP (`D-263`).
 *
 * ⛔ Заради чого файл: (1) пароль НІКОЛИ не повертається з сервера й не з'являється у формі — поле
 * порожнє, а порожнє = «не змінювати»; (2) відмова `GET` не виглядає порожньою формою; (3) проба
 * шле лист на введену адресу.
 */

const Refusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'налаштування SMTP прочитати не вдалося',
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
  it('відмова GET — причина з кодом, форми немає', async () => {
    mockServer('refuse');
    show();

    const alert = await waitFor(() => screen.getByRole('alert'), {
      timeout: 10_000,
    });

    expect(alert.textContent ?? '').toContain('ECR-SYS-0500');
    expect(screen.queryByLabelText(/smtp\.host/)).toBeNull();
  }, 30_000);

  it('збережені значення показані, а пароль — ніколи: поле порожнє, є лише підказка «збережено»', async () => {
    mockServer('ok');
    show();

    const host = await screen.findByLabelText(/smtp\.host/);
    expect((host as HTMLInputElement).value).toBe('smtp.corp.example');

    const password = document.querySelector('input[type="password"]') as HTMLInputElement;
    expect(password.value).toBe('');
    expect(document.body.textContent ?? '').toContain('smtp.passwordStored');
    expect(document.body.textContent ?? '').toContain('smtp.source.database');
  }, 30_000);

  it('збереження без нового пароля шле password: null («не змінювати»), з паролем — лише введене', async () => {
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

  it('«Надіслати тест» шле POST на введену адресу', async () => {
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

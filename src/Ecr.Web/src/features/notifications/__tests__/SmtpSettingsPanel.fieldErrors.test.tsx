import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SmtpSettingsPanel } from '@/features/notifications/SmtpSettingsPanel';
import { testTheme } from '@/test/render';

/**
 * SMTP: помилки біля поля, а не лише тостом (ent6 S1, `SaveSmtpSettingsHandler`).
 *
 * ⛔ Заради чого файл: (1) пароль без шифрування сервер відхиляє (`smtpPasswordNeedsTls`) — поле
 * шифрування каже це ДО збереження, з `aria-invalid`; (2) відмова «введіть пароль ще раз»
 * (`smtpPasswordReentryRequired`) переводить фокус у поле пароля — тост зникає, а читач екрана
 * інакше не знає, де виправляти.
 *
 * Мутаційні докази (лише локально): без `error=` у полі шифрування — тест 1 червоний; без умови
 * `authMode === 'Password'` — тест 2 червоний; без `useEffect` фокуса або без `aria-invalid`/
 * `aria-describedby` у `PasswordInput` (Mantine сам їх внутрішньому полю не ставить) — тест 3 червоний.
 */

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

const Reentry = {
  type: 'about:blank',
  title: 'Unprocessable Entity',
  status: 422,
  detail: 'x',
  errorCode: 'ECR-REQ-0422',
  correlationId: 'cid-smtp-3',
  messageKey: 'err.ECR-REQ-0422.smtpPasswordReentryRequired',
  field: 'password',
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

function serve(value: Record<string, unknown>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) =>
      (init?.method ?? 'GET') === 'PUT' ? json(Reentry, 422) : json(value),
    ),
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <SmtpSettingsPanel />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function encryption(): HTMLInputElement {
  return screen.getByRole('textbox', { name: /smtp\.encryption/ }) as HTMLInputElement;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SmtpSettingsPanel — помилки біля поля', () => {
  it('пароль без шифрування — поле шифрування каже це до збереження, aria-invalid', async () => {
    serve({ ...stored, encryptionMode: 'None' });
    show();

    await screen.findByLabelText(/smtp\.host/);
    const field = encryption();
    expect(field.getAttribute('aria-invalid')).toBe('true');
    expect(document.body.textContent ?? '').toContain('err.ECR-REQ-0422.smtpPasswordNeedsTls');
  }, 30_000);

  it('без автентифікації за паролем шифрування None — не помилка (внутрішній релей)', async () => {
    serve({ ...stored, encryptionMode: 'None', authMode: 'None', userName: null, hasPassword: false });
    show();

    await screen.findByLabelText(/smtp\.host/);
    expect(encryption().getAttribute('aria-invalid')).not.toBe('true');
    expect(document.body.textContent ?? '').not.toContain('smtpPasswordNeedsTls');
  }, 30_000);

  it('відмова «введіть пароль ще раз» — фокус у полі пароля', async () => {
    serve(stored);
    show();

    const host = await screen.findByLabelText(/smtp\.host/);
    fireEvent.change(host, { target: { value: 'relay.example' } });
    fireEvent.click(screen.getByText(/smtp\.save/));

    const password = document.querySelector('input[type="password"]') as HTMLInputElement;
    await waitFor(() => expect(document.activeElement).toBe(password));
    expect(password.getAttribute('aria-invalid')).toBe('true');
    const described = (password.getAttribute('aria-describedby') ?? '')
      .split(' ')
      .map((id) => document.getElementById(id)?.textContent ?? '')
      .join(' ');
    expect(described).toContain('err.ECR-REQ-0422.smtpPasswordReentryRequired');
    expect(described).toContain('smtp.passwordStored');
  }, 30_000);
});

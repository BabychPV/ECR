import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { loadCatalog } from '@/shared/i18n';
import { setLoginRedirect } from '@/api/client';
import { testTheme } from '@/test/render';
import { ChangePasswordPage } from '@/pages/ChangePasswordPage';

/**
 * S9: блокування (`423`) і межа частоти (`429`) на екрані зміни пароля.
 *
 * ⛔ Обидві відмови з'явилися в `change-password` разом із S9: хибний чинний
 * пароль тепер рахується спільним із входом лічильником, а сам ендпоінт — під
 * межею частоти на користувача. Людина має побачити ПРИЧИНУ з каталогу
 * (`messageKey` → `detail`), а не самий заголовок чи загальну помилку: «запис
 * заблоковано на 15 хв» і «зачекайте хвилину» вимагають різних дій.
 *
 * ⚠ Сторінка нічого особливого для них не робить — працює спільний шлях
 * (`ErrorAlert` → `problemText`): подробиця показується, бо сервер позначив її
 * ключем. Тест тримає саме цей контракт.
 */

const Strings: Record<string, string> = {
  'password.title': 'Change password',
  'password.current': 'Current password',
  'password.next': 'New password',
  'password.repeat': 'Repeat password',
  'password.mismatch': 'Passwords do not match',
  'password.submit': 'Change',
  'password.policy': 'At least 12 characters.',
};

interface Refusal {
  status: number;
  title: string;
  errorCode: string;
  messageKey: string;
  detail: string;
}

let refusal: Refusal | null = null;

function json(body: unknown, status = 200, extra: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json', ...extra },
  });
}

beforeEach(async () => {
  refusal = null;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: Strings });
      }
      if (method === 'POST' && url.includes('/change-password') && refusal !== null) {
        return json(
          {
            title: refusal.title,
            status: refusal.status,
            detail: refusal.detail,
            errorCode: refusal.errorCode,
            correlationId: 'c-9',
            messageKey: refusal.messageKey,
          },
          refusal.status,
          refusal.status === 429 ? { 'Retry-After': '42' } : {},
        );
      }
      if (method === 'POST') return new Response(null, { status: 204 });

      return json({});
    }),
  );

  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');
});

afterEach(() => {
  vi.unstubAllGlobals();
  setLoginRedirect(() => {});
});

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <ChangePasswordPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function submitOnce(): Promise<void> {
  const user = userEvent.setup();
  show();

  await user.type(await screen.findByLabelText('Current password'), 'OldPassword1');
  await user.type(screen.getByLabelText('New password'), 'NewPassword123');
  await user.type(screen.getByLabelText('Repeat password'), 'NewPassword123');
  await user.click(screen.getByRole('button', { name: 'Change' }));
}

describe('ChangePasswordPage: 423 і 429 показують причину з каталогу (S9)', () => {
  it('423 — запис заблоковано: банер із подробицею, не під полем і без виходу з системи', async () => {
    const redirect = vi.fn();
    setLoginRedirect(redirect);
    refusal = {
      status: 423,
      title: 'The account is locked.',
      errorCode: 'ECR-AUTH-0423',
      messageKey: 'err.ECR-AUTH-0423.lockedAfterFailures',
      detail: 'The account is temporarily locked after failed attempts.',
    };

    await submitOnce();

    // ⛔ Мутаційний доказ: `mayShowDetail` у `problemText.ts` → `false` —
    // подробиці на екрані немає, лише заголовок.
    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('The account is locked.');
    expect(alert.textContent).toContain('The account is temporarily locked after failed attempts.');

    // Не під полем поточного пароля: це не «пароль не той».
    const field = screen.getByLabelText('Current password');
    expect(field.closest('.mantine-InputWrapper-root')?.textContent)
      .not.toContain('temporarily locked');
    expect(redirect).not.toHaveBeenCalled();
  });

  it('429 — забагато спроб зміни пароля: банер із подробицею з каталогу', async () => {
    refusal = {
      status: 429,
      title: 'Too many requests',
      errorCode: 'ECR-REQ-0429',
      messageKey: 'err.ECR-REQ-0429.tooManyPasswordChanges',
      detail: 'Too many password change attempts. Try again in a minute.',
    };

    await submitOnce();

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('Too many requests');
    expect(alert.textContent).toContain('Too many password change attempts. Try again in a minute.');
  });
});

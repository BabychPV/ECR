import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SmtpSettingsPanel } from '@/features/notifications/SmtpSettingsPanel';
import { testTheme } from '@/test/render';

/**
 * Налаштування SMTP з клавіатури (`ФВ-14.4`, WCAG 2.1.1, 2.4.3): кожне поле досяжне табуляцією, і
 * порядок фокуса збігається з порядком на екрані — від сервера до проби.
 *
 * ⚠ Кнопки, недоступні в цьому стані («Зберегти» без змін, «Надіслати тест» без адреси), фокус не
 * беруть — і це частина очікування: недоступна кнопка в порядку табуляції мовчить для читалки.
 *
 * ⛔ Мутаційний доказ (локально, 2026-10-02): `tabIndex={-1}` на полі «smtp.testTo» → червоний (поле
 * випадає з порядку, одинадцятий крок табуляції — не воно).
 */
const Stored = {
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

afterEach(() => {
  vi.unstubAllGlobals();
});

/** Ім'я елемента у фокусі: підпис поля або текст кнопки. */
function focusedName(): string {
  const active = document.activeElement as HTMLElement | null;
  if (active === null || active === document.body) return '<body>';
  const labelled = active.id === '' ? null : document.querySelector(`label[for="${active.id}"]`);

  return (labelled?.textContent ?? active.textContent ?? '').replace(/[⟦⟧]/g, '').trim();
}

describe('SmtpSettingsPanel: порядок табуляції', () => {
  it('Tab проходить усі поля в порядку екрана', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        new Response(JSON.stringify(Stored), { status: 200, headers: { 'Content-Type': 'application/json' } }),
      ),
    );
    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <SmtpSettingsPanel />
        </QueryClientProvider>
      </MantineProvider>,
    );
    await screen.findByDisplayValue('smtp.corp.example');

    const user = userEvent.setup();
    const order: string[] = [];
    for (let step = 0; step < 11; step++) {
      await user.tab();
      order.push(focusedName());
    }

    expect(order).toEqual([
      'smtp.host',
      'smtp.port',
      'smtp.encryption',
      'smtp.from',
      'smtp.fromName',
      'smtp.auth',
      'smtp.user',
      'smtp.password',
      'smtp.clearPassword',
      'smtp.enabled',
      'smtp.testTo',
    ]);
  }, 60_000);
});

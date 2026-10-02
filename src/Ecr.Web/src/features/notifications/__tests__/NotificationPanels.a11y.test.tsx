import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, waitFor } from '@testing-library/react';
import { SmtpSettingsPanel } from '@/features/notifications/SmtpSettingsPanel';
import { NotificationTemplatesPanel } from '@/features/notifications/NotificationTemplatesPanel';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { Shell, Themes, createScanClient, settleQueries } from '@/test/__tests__/a11yFixtures';

/**
 * Нові панелі сторінки сповіщень — SMTP (`D-263`) і шаблони повідомлень (`CL-6`, `ФВ-12.4a`): axe без
 * блокуючих порушень в обох темах (`ФВ-14.16`).
 *
 * ⚠ Маршрут `/admin/notifications` у наборі маршрутів (`accessibility.part3`) сканує ці панелі на
 * загальній заглушці мережі: SMTP там без збереженого пароля (поля входу не малюються), шаблони — без
 * адресатів і без помилки шаблону. Тут — саме ті стани, де нові вузли: пароль-поле з описом,
 * «очистити пароль», перелік адресатів, поле з помилкою (`aria-invalid` + опис), відмова читання.
 *
 * ⛔ Мутаційний доказ (локально, 2026-10-02): прибрати `label` у `TextInput` «smtp.testTo» → червоний
 * `critical · label` в обох темах; прибрати `label` у `Textarea` тіла шаблону → так само.
 */
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

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

const Refusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'refused',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-a11y',
  messageKey: 'err.ECR-SYS-0500.unexpected',
};

const Subject = 'ECR: period {period} opened, project {project}';
const Body = 'A new reporting period {period} has opened for project {project}.';

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

function stub(mode: 'ok' | 'refuse'): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const path = url.split('?')[0] ?? '';

      if (path.startsWith('/api/v1/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: {} });
      if (mode === 'refuse' && (path.endsWith('/notifications/smtp') || path === '/api/v1/ui-strings')) {
        return json(Refusal, 500);
      }
      if (path.endsWith('/notifications/smtp')) return json(Stored);
      if (path === '/api/v1/me') {
        return json({
          userId: 1,
          userName: 'admin',
          language: 'en',
          permissions: ['System.ManageLocalization', 'Security.ManageRoles'],
        });
      }
      if (path === '/api/v1/languages') {
        return json([
          { code: 'en', nameNative: 'English', isDefault: true },
          { code: 'kz', nameNative: 'Қазақша', isDefault: false },
        ]);
      }
      if (path === '/api/v1/roles') return json([{ id: 3, code: 'ECOLOGIST' }]);
      if (path === '/api/v1/notifications/channels') {
        return json([
          {
            id: 7,
            name: 'Ops mailbox',
            kind: 'Smtp',
            isEnabled: true,
            hasSecret: false,
            modifiedAt: '2026-10-01T10:00:00Z',
            settings: { recipientRoleIds: [3] },
            transportFromConfiguration: true,
            transportConfigured: true,
          },
        ]);
      }
      if (path === '/api/v1/notifications/rules') {
        return json({
          eventKinds: ['PeriodOpened'],
          rules: [{ eventKind: 'PeriodOpened', channelId: 7, minSeverity: 'Warning', isEnabled: true }],
        });
      }
      if (path === '/api/v1/ui-strings') {
        return json({
          languageCode: 'en',
          items: [
            { key: 'notifications.periodOpened.subject', reference: Subject, value: Subject },
            { key: 'notifications.periodOpened.body', reference: Body, value: Body },
          ],
        });
      }

      return json([]);
    }),
  );
}

describe('Панелі сповіщень — axe без блокуючих порушень', () => {
  it.each(Themes)('тема %s: SMTP із збереженим паролем і полем проби', async (scheme) => {
    stub('ok');
    const client = createScanClient();
    const { container } = render(
      <Shell colorScheme={scheme} client={client}>
        <SmtpSettingsPanel />
      </Shell>,
    );
    await settleQueries(client);

    // Стан, заради якого тест: поля входу і «очистити пароль» на екрані.
    expect(container.querySelector('input[type="password"]')).not.toBeNull();
    expect(container.querySelectorAll('input[type="checkbox"]').length).toBeGreaterThanOrEqual(2);

    const violations = await findViolations(container);
    expect(violations, describeViolations(violations)).toHaveLength(0);
  });

  it.each(Themes)('тема %s: шаблон з адресатами і помилкою плейсхолдера', async (scheme) => {
    stub('ok');
    const client = createScanClient();
    const { container } = render(
      <Shell colorScheme={scheme} client={client}>
        <NotificationTemplatesPanel />
      </Shell>,
    );
    await settleQueries(client);

    // ⚠ Адресати монтуються лише після мов і рядків — їхні запити стартують другою хвилею.
    await waitFor(() => expect(container.querySelector('[data-notification-recipients="list"]')).not.toBeNull());
    await settleQueries(client);

    const subject = container.querySelector<HTMLInputElement>('input[value^="ECR: period"]');
    expect(subject).not.toBeNull();
    fireEvent.change(subject as HTMLInputElement, { target: { value: 'ECR: {unknown}' } });
    await waitFor(() => expect(subject?.getAttribute('aria-invalid')).toBe('true'));

    const violations = await findViolations(container);
    expect(violations, describeViolations(violations)).toHaveLength(0);
  });

  it.each(Themes)('тема %s: відмова читання обох панелей', async (scheme) => {
    stub('refuse');
    const client = createScanClient();
    const { container } = render(
      <Shell colorScheme={scheme} client={client}>
        <SmtpSettingsPanel />
        <NotificationTemplatesPanel />
      </Shell>,
    );
    await settleQueries(client);

    expect(container.querySelector('[data-smtp="pending"]')).toBeNull();
    expect(container.querySelectorAll('[role="alert"]').length).toBeGreaterThanOrEqual(2);

    const violations = await findViolations(container);
    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});

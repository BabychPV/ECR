import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { NotificationTemplatesPanel } from '@/features/notifications/NotificationTemplatesPanel';
import { RulesMatrixPanel } from '@/features/notifications/RulesMatrixPanel';
import { SmtpSettingsPanel } from '@/features/notifications/SmtpSettingsPanel';
import type { NotificationChannel, NotificationRuleMatrix } from '@/features/notifications/api';
import { renderWithQuery } from '@/test/render';

/**
 * Аудит L9-28 (клас L9-03): правка, зроблена ПІД ЧАС збереження, не губиться мовчки.
 *
 * ⛔ Усі три панелі в `onSuccess` знімають чернетку цілком (матриця — ставить відповідь сервера,
 * SMTP і шаблони — `setDraft(null)`). Тож поле, яке лишалося редагованим, поки `PUT` у дорозі,
 * приймало правку, якої немає в тілі запиту, — і відповідь її затирала. Перевіряємо, що на час
 * запиту поля замкнені, а після відповіді — знову редаговані.
 *
 * ⚠ `PUT` тримається відкладеним промісом: інакше відповідь прийшла б раніше за перевірку.
 */

const Channel: NotificationChannel = {
  id: 7,
  name: 'Ops mailbox',
  kind: 'Smtp',
  isEnabled: true,
  hasSecret: false,
  modifiedAt: '2026-10-01T10:00:00Z',
  settings: { recipientRoleIds: [3] },
  transportFromConfiguration: true,
  transportConfigured: true,
};

const Matrix: NotificationRuleMatrix = {
  eventKinds: ['PeriodOpened', 'PeriodGraceStarted'],
  rules: [{ eventKind: 'PeriodOpened', channelId: 7, minSeverity: 'Info', isEnabled: true }],
};

const Smtp = {
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

const Subject = 'ECR: period {period} opened, project {project}';
const Body = 'A new reporting period {period} has opened for project {project}.';

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

/** Сервер, у якого кожен `PUT` чекає, доки тест не відпустить його `release()`. */
function mockServer(): { release: () => void; puts: () => number } {
  const waiting: (() => void)[] = [];
  let puts = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const path = url.split('?')[0] ?? '';
      const method = (init?.method ?? 'GET').toUpperCase();

      if (method === 'PUT') {
        puts += 1;
        await new Promise<void>((resolve) => waiting.push(resolve));
        if (path === '/api/v1/notifications/rules') {
          const body = JSON.parse(String(init?.body)) as { rules: NotificationRuleMatrix['rules'] };
          return json({ eventKinds: Matrix.eventKinds, rules: body.rules });
        }
        if (path === '/api/v1/notifications/smtp') return json({ ...Smtp, ...(JSON.parse(String(init?.body)) as object) });

        return json({ revision: 42 });
      }

      if (path === '/api/v1/me') {
        return json({ userId: 1, userName: 'admin', language: 'en', permissions: ['System.ManageLocalization'] });
      }
      if (path === '/api/v1/languages') return json([{ code: 'en', nameNative: 'English', isDefault: true }]);
      if (path === '/api/v1/notifications/channels') return json([Channel]);
      if (path === '/api/v1/notifications/rules') return json(Matrix);
      if (path === '/api/v1/notifications/smtp') return json(Smtp);
      if (path === '/api/v1/ui-strings') {
        return json({
          languageCode: 'en',
          items: [
            { key: 'notifications.periodOpened.subject', reference: Subject, value: Subject },
            { key: 'notifications.periodOpened.body', reference: Body, value: Body },
          ],
        });
      }

      throw new Error(`Непередбачена адреса: ${method} ${path}`);
    }),
  );

  return {
    release: () => {
      for (const resolve of waiting.splice(0)) resolve();
    },
    puts: () => puts,
  };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('L9-28: поля замкнені, поки збереження в дорозі', () => {
  it('матриця правил: прапорці й межі неактивні до відповіді PUT, потім знову активні', async () => {
    const server = mockServer();
    renderWithQuery(<RulesMatrixPanel />);

    const other = (await screen.findByRole(
      'checkbox',
      { name: '⟦notifications.event.PeriodGraceStarted⟧ · Ops mailbox' },
      { timeout: 10_000 },
    )) as HTMLInputElement;
    fireEvent.click(other);
    fireEvent.click(screen.getByRole('button', { name: '⟦notifications.saveRules⟧' }));

    await waitFor(() => expect(server.puts()).toBe(1));
    await waitFor(() => expect(other.disabled).toBe(true));
    const first = screen.getByRole('checkbox', {
      name: '⟦notifications.event.PeriodOpened⟧ · Ops mailbox',
    }) as HTMLInputElement;
    expect(first.disabled).toBe(true);

    server.release();
    await waitFor(() => expect(other.disabled).toBe(false));
    expect(other.checked).toBe(true);
  }, 30_000);

  it('SMTP: текстові поля readOnly, перемикачі неактивні до відповіді PUT', async () => {
    const server = mockServer();
    renderWithQuery(<SmtpSettingsPanel />);

    const host = (await screen.findByLabelText(/smtp\.host/, {}, { timeout: 10_000 })) as HTMLInputElement;
    fireEvent.change(host, { target: { value: 'smtp2.corp.example' } });
    fireEvent.click(screen.getByRole('button', { name: '⟦smtp.save⟧' }));

    await waitFor(() => expect(server.puts()).toBe(1));
    await waitFor(() => expect(host.readOnly).toBe(true));
    expect((screen.getByLabelText(/smtp\.from⟧/) as HTMLInputElement).readOnly).toBe(true);
    expect((screen.getByRole('switch', { name: '⟦smtp.enabled⟧' }) as HTMLInputElement).disabled).toBe(true);

    server.release();
    await waitFor(() => expect(host.readOnly).toBe(false));
  }, 30_000);

  it('шаблони повідомлень: тема й тіло readOnly до відповіді PUT', async () => {
    const server = mockServer();
    renderWithQuery(<NotificationTemplatesPanel />);

    const subject = (await screen.findByLabelText(/notificationTemplates\.subject⟧/, {}, { timeout: 10_000 })) as
      HTMLInputElement;
    fireEvent.change(subject, { target: { value: 'ECR: {project} — {period}' } });
    fireEvent.click(screen.getByText('⟦notificationTemplates.save⟧'));

    await waitFor(() => expect(server.puts()).toBe(1));
    await waitFor(() => expect(subject.readOnly).toBe(true));
    expect((screen.getByLabelText(/notificationTemplates\.body⟧/) as HTMLTextAreaElement).readOnly).toBe(true);

    server.release();
    await waitFor(() => expect(subject.readOnly).toBe(false));
  }, 30_000);
});

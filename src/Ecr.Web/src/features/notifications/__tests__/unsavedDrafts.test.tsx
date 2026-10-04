import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { NotificationTemplatesPanel } from '@/features/notifications/NotificationTemplatesPanel';
import { SmtpSettingsPanel } from '@/features/notifications/SmtpSettingsPanel';
import { hasUnsavedChanges } from '@/shared/ui/unsavedSources';
import { renderWithQuery } from '@/test/render';

/**
 * Аудит L9-33: чернетки SMTP і шаблонів повідомлень захищені від мовчазної втрати — джерело для
 * `UnsavedGuard` (перехід маршрутом) і `beforeunload` (закриття вкладки), лише поки є чернетка.
 */

const Smtp = {
  host: 'smtp.corp.example',
  port: 587,
  encryptionMode: 'StartTls',
  fromAddress: 'ecr@corp.example',
  fromName: 'ECR',
  authMode: 'None',
  userName: null,
  hasPassword: false,
  isEnabled: true,
  source: 'database',
  configured: true,
  updatedAt: '2026-10-01T08:00:00Z',
};

const Subject = 'ECR: period {period} opened, project {project}';
const Body = 'A new reporting period {period} has opened for project {project}.';

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path === '/api/v1/me') {
        return json({ userId: 1, userName: 'admin', language: 'en', permissions: ['System.ManageLocalization'] });
      }
      if (path === '/api/v1/languages') return json([{ code: 'en', nameNative: 'English', isDefault: true }]);
      if (path === '/api/v1/notifications/channels') return json([]);
      if (path === '/api/v1/notifications/rules') return json({ eventKinds: ['PeriodOpened'], rules: [] });
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

      throw new Error(`Непередбачена адреса: ${path}`);
    }),
  );
}

/** `beforeunload`, як його шле браузер; повертає, чи сторінка попросила питання. */
function unloadAsks(): boolean {
  const event = new Event('beforeunload', { cancelable: true });
  window.dispatchEvent(event);

  return event.defaultPrevented;
}

let unmount: (() => void) | null = null;

afterEach(() => {
  unmount?.();
  unmount = null;
  vi.unstubAllGlobals();
});

describe('L9-33: незбережені чернетки SMTP і шаблонів', () => {
  it('SMTP: без правок — тихо; з правкою — джерело UnsavedGuard і beforeunload; після розмонтування — тихо', async () => {
    mockServer();
    unmount = renderWithQuery(<SmtpSettingsPanel />).unmount;

    const host = await screen.findByLabelText(/smtp\.host/, {}, { timeout: 10_000 });
    expect(hasUnsavedChanges()).toBe(false);
    expect(unloadAsks()).toBe(false);

    fireEvent.change(host, { target: { value: 'smtp2.corp.example' } });

    await waitFor(() => expect(hasUnsavedChanges()).toBe(true));
    expect(unloadAsks()).toBe(true);

    unmount();
    unmount = null;
    expect(hasUnsavedChanges()).toBe(false);
    expect(unloadAsks()).toBe(false);
  }, 30_000);

  it('шаблони: з правкою — beforeunload питає', async () => {
    mockServer();
    unmount = renderWithQuery(<NotificationTemplatesPanel />).unmount;

    const subject = await screen.findByLabelText(/notificationTemplates\.subject⟧/, {}, { timeout: 10_000 });
    expect(unloadAsks()).toBe(false);

    fireEvent.change(subject, { target: { value: 'ECR: {project} — {period}' } });

    await waitFor(() => expect(unloadAsks()).toBe(true));
    expect(hasUnsavedChanges()).toBe(true);
  }, 30_000);
});

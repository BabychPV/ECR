import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RulesMatrixPanel } from '@/features/notifications/RulesMatrixPanel';
import type { NotificationChannel, NotificationRuleMatrix } from '@/features/notifications/api';
import { hasUnsavedChanges, unsavedCount } from '@/shared/ui/unsavedSources';
import { testTheme } from '@/test/render';

/**
 * Аудит U14a: чернетка матриці правил сповіщень — ознака «змінено», реєстрація
 * в `UnsavedGuard`, перезапит не затирає змінену чернетку, `beforeunload`.
 *
 * ⚠ Підмінений `fetch`, а не `api.ts`: перезапит іде тим самим шляхом, що й у
 * продукті (`refetchQueries` — те, що робить фокус вікна).
 */

const Channels: NotificationChannel[] = [
  {
    id: 7,
    name: 'Пошта чергового',
    kind: 'Smtp',
    isEnabled: true,
    hasSecret: true,
    modifiedAt: '2026-09-01T10:00:00Z',
    settings: { recipients: ['ops@corp.example'] },
    transportFromConfiguration: true,
    transportConfigured: true,
  },
  {
    id: 9,
    name: 'Teams: черговий',
    kind: 'TeamsWebhook',
    isEnabled: true,
    hasSecret: true,
    modifiedAt: '2026-09-01T10:00:00Z',
    settings: { title: 'ECR' },
    transportFromConfiguration: false,
    transportConfigured: true,
  },
];

const EventKinds: NotificationRuleMatrix['eventKinds'] = ['JobFailed', 'CollectionFailed'];

const Initial: NotificationRuleMatrix = {
  eventKinds: EventKinds,
  rules: [{ channelId: 7, eventKind: 'JobFailed', isEnabled: true, minSeverity: 'Warning' }],
};

/** Те, що сервер віддає після чужої правки: інший адмін увімкнув CollectionFailed · Teams. */
const Changed: NotificationRuleMatrix = {
  eventKinds: EventKinds,
  rules: [
    { channelId: 7, eventKind: 'JobFailed', isEnabled: true, minSeverity: 'Warning' },
    { channelId: 9, eventKind: 'CollectionFailed', isEnabled: true, minSeverity: 'Error' },
  ],
};

interface Server {
  /** Що віддасть наступний `GET /rules`. */
  current: NotificationRuleMatrix;
  readonly puts: NotificationRuleMatrix['rules'][];
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function mockServer(): Server {
  const server: Server = { current: Initial, puts: [] };

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = String(input).split('?')[0] ?? '';
      const method = init?.method ?? 'GET';

      if (path === '/api/v1/notifications/rules' && method === 'PUT') {
        const body = JSON.parse(String(init?.body)) as { rules: NotificationRuleMatrix['rules'] };
        server.puts.push(body.rules);
        server.current = { eventKinds: EventKinds, rules: body.rules };

        return jsonResponse(server.current);
      }

      if (path === '/api/v1/notifications/rules') return jsonResponse(server.current);
      if (path === '/api/v1/notifications/channels') return jsonResponse(Channels);

      throw new Error(`Непередбачена адреса: ${method} ${path}`);
    }),
  );

  return server;
}

const SettleTimeout = 10_000;
const TestTimeout = 30_000;

async function show(): Promise<{ client: QueryClient; unmount: () => void }> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  const { unmount } = render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <RulesMatrixPanel />
      </QueryClientProvider>
    </MantineProvider>,
  );

  await screen.findByRole('table', {}, { timeout: SettleTimeout });

  return { client, unmount };
}

function cellBox(eventKind: string, channelName: string): HTMLInputElement {
  return screen.getByRole('checkbox', {
    name: `⟦notifications.event.${eventKind}⟧ · ${channelName}`,
  }) as HTMLInputElement;
}

function saveButton(): HTMLButtonElement {
  return screen.getByRole('button', { name: '⟦notifications.saveRules⟧' }) as HTMLButtonElement;
}

function cancelButton(): HTMLButtonElement {
  return screen.getByRole('button', { name: '⟦common.cancel⟧' }) as HTMLButtonElement;
}

/**
 * Перезапит так, як його робить фокус вікна, — і чекаємо, поки відповідь
 * дійде до КОМПОНЕНТА.
 *
 * ⛔ Мало дочекатися самого `refetchQueries`: `notifyManager` TanStack
 * розсилає зміну спостерігачам через `setTimeout(0)`, тож без паузи твердження
 * «чернетка не затерта» перевіряло б рендер ДО приходу відповіді й було б
 * зеленим навіть тоді, коли панель затирає чернетку (перевірено мутацією).
 */
async function refetch(client: QueryClient): Promise<void> {
  await act(async () => {
    await client.refetchQueries({ queryKey: ['notifications', 'rules'] });
    await new Promise((resolve) => setTimeout(resolve, 50));
  });
}

/** `beforeunload`, як його шле браузер; повертає, чи сторінка попросила питання. */
function unloadAsks(): boolean {
  const event = new Event('beforeunload', { cancelable: true });
  window.dispatchEvent(event);

  return event.defaultPrevented;
}

let mounted: (() => void) | null = null;

afterEach(() => {
  mounted?.();
  mounted = null;
  vi.unstubAllGlobals();
});

describe('RulesMatrixPanel: незбережені правки (U14a)', () => {
  it(
    'без змін — «Зберегти» і «Скасувати» вимкнені, джерела немає, beforeunload мовчить',
    async () => {
      mockServer();
      mounted = (await show()).unmount;

      expect(saveButton().disabled).toBe(true);
      expect(cancelButton().disabled).toBe(true);
      expect(screen.queryByTestId('notification-rules-unsaved')).toBeNull();
      expect(hasUnsavedChanges()).toBe(false);
      expect(unloadAsks()).toBe(false);
    },
    TestTimeout,
  );

  it(
    'правка — ознака «змінено», джерело з лічильником клітинок, кнопка активна, beforeunload питає',
    async () => {
      mockServer();
      mounted = (await show()).unmount;

      fireEvent.click(cellBox('JobFailed', 'Teams: черговий'));
      fireEvent.click(cellBox('CollectionFailed', 'Пошта чергового'));

      expect(screen.getByTestId('notification-rules-unsaved').textContent).toBe('⟦notifications.rulesUnsaved⟧');
      expect(saveButton().disabled).toBe(false);
      expect(hasUnsavedChanges()).toBe(true);
      expect(unsavedCount()).toBe(2);
      expect(unloadAsks()).toBe(true);
    },
    TestTimeout,
  );

  it(
    'повернення клітинки до вихідного стану — вже не зміна',
    async () => {
      mockServer();
      mounted = (await show()).unmount;

      fireEvent.click(cellBox('JobFailed', 'Пошта чергового'));
      expect(hasUnsavedChanges()).toBe(true);

      fireEvent.click(cellBox('JobFailed', 'Пошта чергового'));
      expect(hasUnsavedChanges()).toBe(false);
      expect(saveButton().disabled).toBe(true);
    },
    TestTimeout,
  );

  it(
    'перезапит із новою матрицею НЕ затирає змінену чернетку',
    async () => {
      const server = mockServer();
      const { client, unmount } = await show();
      mounted = unmount;

      fireEvent.click(cellBox('JobFailed', 'Teams: черговий'));

      server.current = Changed;
      await refetch(client);

      expect(client.getQueryData(['notifications', 'rules'])).toEqual(Changed);

      // Правка на місці, чужа клітинка в чернетку не влізла.
      expect(cellBox('JobFailed', 'Teams: черговий').checked).toBe(true);
      expect(cellBox('CollectionFailed', 'Teams: черговий').checked).toBe(false);
      expect(hasUnsavedChanges()).toBe(true);
      expect(unsavedCount()).toBe(1);
    },
    TestTimeout,
  );

  it(
    'незмінена чернетка йде за сервером',
    async () => {
      const server = mockServer();
      const { client, unmount } = await show();
      mounted = unmount;

      server.current = Changed;
      await refetch(client);

      await waitFor(() => expect(cellBox('CollectionFailed', 'Teams: черговий').checked).toBe(true), {
        timeout: SettleTimeout,
      });
      expect(hasUnsavedChanges()).toBe(false);
      expect(saveButton().disabled).toBe(true);
    },
    TestTimeout,
  );

  it(
    'після збереження джерело знято, beforeunload мовчить, кнопка знову вимкнена',
    async () => {
      const server = mockServer();
      mounted = (await show()).unmount;

      fireEvent.click(cellBox('JobFailed', 'Teams: черговий'));
      fireEvent.click(saveButton());

      await waitFor(() => expect(server.puts).toHaveLength(1), { timeout: SettleTimeout });
      await waitFor(() => expect(hasUnsavedChanges()).toBe(false), { timeout: SettleTimeout });

      expect(cellBox('JobFailed', 'Teams: черговий').checked).toBe(true);
      expect(saveButton().disabled).toBe(true);
      expect(screen.queryByTestId('notification-rules-unsaved')).toBeNull();
      expect(unloadAsks()).toBe(false);
    },
    TestTimeout,
  );

  it(
    'скасування повертає чинний стан сервера і знімає джерело',
    async () => {
      mockServer();
      mounted = (await show()).unmount;

      fireEvent.click(cellBox('JobFailed', 'Teams: черговий'));
      expect(hasUnsavedChanges()).toBe(true);

      fireEvent.click(cancelButton());

      expect(cellBox('JobFailed', 'Teams: черговий').checked).toBe(false);
      expect(hasUnsavedChanges()).toBe(false);
      expect(unloadAsks()).toBe(false);
    },
    TestTimeout,
  );

  it(
    'розмонтування знімає джерело й слухача beforeunload',
    async () => {
      mockServer();
      const { unmount } = await show();

      fireEvent.click(cellBox('JobFailed', 'Teams: черговий'));
      expect(hasUnsavedChanges()).toBe(true);

      unmount();

      expect(hasUnsavedChanges()).toBe(false);
      expect(unloadAsks()).toBe(false);
    },
    TestTimeout,
  );
});

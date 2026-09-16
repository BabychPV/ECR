import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RoleView, UserView } from '@/api/types';
import { UserAccessEditor } from '@/features/security/UserAccessEditor';

/**
 * Аудит 2026-09-16, §10.8: двоетапне збереження (ролі → пошта) не повідомляло
 * про ЧАСТКОВУ відмову і не інвалідувало кеш.
 *
 * ⛔ `mutationFn` виконує ДВА незалежні записи послідовно: `PUT …/roles`,
 * потім `PUT …/email`. Коли перший пройшов, а другий відмовив, `onSuccess` не
 * виконується взагалі — тож (а) кеш `['users']`/`['user-roles', id]` лишається
 * зі СТАРИМИ ролями, хоча сервер їх уже змінив, і (б) адмін бачить лише текст
 * помилки пошти й не знає, що ролі вже застосовано. Найгірший наслідок —
 * повторна спроба: адмін «виправляє» те, що вже збережено, дивлячись на
 * застарілий перелік.
 *
 * ⚠ `MultiSelect` підмінено легким заглушником — та сама причина й той самий
 * прийом, що в `UserAccessEditor.rolesDropdown.test.tsx` (під jsdom справжній
 * зависає).
 */
vi.mock('@mantine/core', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/core')>();

  function StubMultiSelect(props: {
    value?: string[];
    onChange?: (value: string[]) => void;
    rightSection?: React.ReactNode;
  }): JSX.Element {
    return (
      <div>
        <div data-testid="roles-value">{(props.value ?? []).join(',')}</div>
        {props.rightSection}
      </div>
    );
  }

  return { ...actual, MultiSelect: StubMultiSelect };
});

const user: UserView = {
  id: 7,
  userName: 'ivanov',
  displayName: 'Іванов',
  email: null,
  provider: 'Local',
  isActive: true,
  isBootstrapAdmin: false,
  isLockedOut: false,
  mustChangePassword: false,
  receivesAlerts: false,
};

const roles: RoleView[] = [
  {
    id: 1,
    code: 'DataEntry',
    isActive: true,
    isBuiltIn: false,
    dangerousPermissions: [],
    permissions: ['Document.View'],
  },
];

/** Повідомлення, які показав застосунок (будь-яким кольором). */
const shown: string[] = [];

vi.mock('@mantine/notifications', () => ({
  notifications: {
    show: (options: { message?: string }) => {
      shown.push(String(options.message ?? ''));
    },
  },
}));

/** Текст відмови другого етапу — саме його називає сервер. */
const EmailFailure = 'Адреса вже належить іншому обліковому запису.';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function mockServer(): void {
  shown.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (init?.method === 'PUT' && url.includes('/email')) {
        return jsonResponse(
          {
            title: 'Помилка валідації',
            status: 409,
            detail: EmailFailure,
            errorCode: 'ECR-USR-0409',
            correlationId: 'corr-1',
          },
          409,
        );
      }

      if (init?.method === 'PUT' && url.includes('/roles')) {
        return jsonResponse({ roles: 1 });
      }

      // `GET …/roles` — ролі, уже призначені користувачеві.
      return jsonResponse(['DataEntry']);
    }),
  );
}

interface Harness {
  closed: () => number;
  invalidated: unknown[][];
}

function show(): Harness {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  const invalidated: unknown[][] = [];
  const original = client.invalidateQueries.bind(client);
  client.invalidateQueries = ((filters?: { queryKey?: unknown[] }) => {
    if (filters?.queryKey !== undefined) invalidated.push(filters.queryKey);

    return original(filters as Parameters<typeof original>[0]);
  }) as typeof client.invalidateQueries;

  let closes = 0;

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <UserAccessEditor
          user={user}
          roles={roles}
          onClose={() => {
            closes += 1;
          }}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return { closed: () => closes, invalidated };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('UserAccessEditor: часткова відмова двоетапного збереження названа (§10.8)', () => {
  it('ролі збережено, пошта — ні: кеш перечитується, попри відмову', async () => {
    mockServer();
    const { invalidated } = show();

    await waitFor(() => expect(screen.getByTestId('roles-value').textContent).toBe('DataEntry'));

    fireEvent.change(screen.getByLabelText(/security\.email/), {
      target: { value: 'ivanov@example.com' },
    });
    fireEvent.click(screen.getByRole('button', { name: '⟦common.save⟧' }));

    await waitFor(() => expect(shown.length).toBeGreaterThan(0));

    // ⛔ Мутаційний доказ (RED до фіксу): інвалідація жила лише в `onSuccess`,
    // тож при відмові ДРУГОГО запису кеш лишався зі старими ролями, хоча
    // сервер уже застосував нові.
    expect(invalidated).toContainEqual(['user-roles', 7]);
    expect(invalidated).toContainEqual(['users']);
  });

  it('повідомлення каже і що збережено, і що ні', async () => {
    mockServer();
    show();

    await waitFor(() => expect(screen.getByTestId('roles-value').textContent).toBe('DataEntry'));

    fireEvent.change(screen.getByLabelText(/security\.email/), {
      target: { value: 'ivanov@example.com' },
    });
    fireEvent.click(screen.getByRole('button', { name: '⟦common.save⟧' }));

    await waitFor(() => expect(shown.length).toBeGreaterThan(0));

    // ⛔ До фіксу показувався ЛИШЕ текст відмови пошти: адмін не знав, що ролі
    // вже змінено, і повторна спроба «виправляла» вже збережене.
    const message = shown.join(' | ');
    expect(message).toContain(EmailFailure);
    expect(message).toContain('security.accessSaved');
  });

  it('діалог не закривається, поки другий етап не пройшов', async () => {
    mockServer();
    const { closed } = show();

    await waitFor(() => expect(screen.getByTestId('roles-value').textContent).toBe('DataEntry'));

    fireEvent.click(screen.getByRole('button', { name: '⟦common.save⟧' }));
    await waitFor(() => expect(shown.length).toBeGreaterThan(0));

    // ⚠ Закрити діалог означало б сховати те, що лишилося незбереженим.
    expect(closed()).toBe(0);
  });
});

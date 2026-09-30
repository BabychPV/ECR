import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import type { ComponentProps, JSX } from 'react';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { SecurityPage } from '@/pages/admin/SecurityPage';
import { withTestDefaults } from '@/test/render';

/**
 * Живий дефект (2026-09-24, замір на стенді): друк у діалозі «New role» —
 * медіана 97–120 мс на символ (dev) проти 28–52 мс у решті діалогів. Причина:
 * чернетка діалогу (код, назва, права) була станом `SecurityPage`, і кожен
 * символ перерендерював матрицю ролей × 41 право.
 *
 * ⛔ Шпигун — на `Table` Mantine: на вкладці «Ролі» це рівно матриця (у
 * діалогах таблиць немає), тож твердження саме про те, що друк не чіпає
 * матриці.
 *
 * ⛔ Мутаційний доказ: повернення чернетки на сторінку (попередня версія
 * `SecurityPage.tsx`, де `roleCode`/`roleName` — `useState` сторінки) дає
 * тут червоний тест — лічильник рендерів матриці росте на кожен символ.
 */

const renders = vi.hoisted(() => ({ table: 0 }));

vi.mock('@mantine/core', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/core')>();
  const Real = actual.Table;

  function SpyTable(props: ComponentProps<typeof Real>): JSX.Element {
    renders.table += 1;
    return <Real {...props} />;
  }

  return { ...actual, Table: Object.assign(SpyTable, Real) };
});

const SeededStrings: Record<string, string> = {
  'security.roles': 'Roles',
  'security.users': 'Users',
  'security.role': 'Role',
  'security.createRole': 'New role',
  'security.createUser': 'New user',
  'security.roleCode': 'Code',
  'security.roleName': 'Name',
  'security.login': 'Login',
  'security.name': 'Display name',
  'common.save': 'Save',
  'common.cancel': 'Cancel',
};

const Permissions = [
  { code: 'Document.View', isDangerous: false },
  { code: 'Template.Edit', isDangerous: false },
];

const Roles = [
  { id: 1, code: 'Viewer', isActive: true, isBuiltIn: false, dangerousPermissions: [], permissions: ['Document.View'] },
];

const Users = [
  {
    id: 5,
    userName: 'admin',
    displayName: 'Admin',
    provider: 'Local',
    isActive: true,
    receivesAlerts: false,
    email: 'a@example.test',
    mustChangePassword: false,
    isLockedOut: false,
    isBootstrapAdmin: false,
    lastSignInAt: null,
  },
];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

let fetchMock: ReturnType<typeof vi.fn>;

beforeEach(async () => {
  fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);

    if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
    if (url.includes('/me')) {
      return json({
        userId: 0,
        userName: 'test',
        language: 'en',
        permissions: ['Security.ManageRoles', 'Security.ManageUsers'],
        isSimulation: false,
      });
    }
    if (init?.method === 'POST') return json({ id: 99 });
    if (url.includes('/permissions')) return json(Permissions);
    if (url.includes('/languages')) return json([{ code: 'en', nameNative: 'English', ordinal: 1, isDefault: true }]);
    if (url.includes('/roles')) return json(Roles);

    if (url.includes('/users')) return json({ items: Users, nextCursor: null, totalCount: 1 });

    return json([]);
  });
  vi.stubGlobal('fetch', fetchMock);
  await loadCatalog('en', 'private');
  renders.table = 0;
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function show(tab: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={withTestDefaults(theme)}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[`/admin/security?tab=${tab}`]}>
          <SecurityPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Набирає текст посимвольно — кожен символ окремою подією, як людина. */
function typeInto(input: HTMLElement, text: string): void {
  let value = '';
  for (const ch of text) {
    value += ch;
    fireEvent.change(input, { target: { value } });
  }
}

/**
 * Чекає, доки дозавантаження (ліниві панелі, запити) перестануть рендерити
 * таблиці: інакше рендер від прибулої відповіді зарахувався б друку.
 */
async function settle(): Promise<void> {
  let last = -1;
  while (last !== renders.table) {
    last = renders.table;
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
}

function postBody(path: string): unknown {
  const call = fetchMock.mock.calls.find(
    ([input, init]) => String(input).endsWith(path) && (init as RequestInit | undefined)?.method === 'POST',
  );
  expect(call, `POST на ${path}`).toBeDefined();
  return JSON.parse(String((call?.[1] as RequestInit).body));
}

describe('SecurityPage — друк у діалогах не перерендерює матрицю ролей', () => {
  it('«New role»: набір коду й назви не рендерить матрицю, а збереження везе введене', async () => {
    show('roles');
    await screen.findByText('Viewer');

    fireEvent.click(screen.getByRole('button', { name: 'New role' }));
    const dialog = await screen.findByRole('dialog');
    const code = await within(dialog).findByRole('textbox', { name: /^Code/ });
    const name = await within(dialog).findByRole('textbox', { name: /Name · English/ });

    await settle();
    const before = renders.table;
    typeInto(code, 'AUDITOR');
    typeInto(name, 'Auditor');
    fireEvent.click(within(dialog).getByRole('checkbox', { name: /Template\.Edit/ }));

    // Форма справді оновлюється від кожного символу — інакше «нуль рендерів
    // матриці» було б правдою й для зламаного поля.
    expect((code as HTMLInputElement).value).toBe('AUDITOR');
    expect((name as HTMLInputElement).value).toBe('Auditor');
    expect(renders.table - before).toBe(0);

    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(fetchMock.mock.calls.some(([, init]) => (init as RequestInit | undefined)?.method === 'POST')).toBe(true);
    });
    expect(postBody('/api/v1/roles')).toEqual({
      code: 'AUDITOR',
      nameL10n: { en: 'Auditor' },
      permissionCodes: ['Template.Edit'],
    });
  });

  it('«New user»: набір логіна не рендерить таблицю користувачів, а збереження везе введене', async () => {
    show('users');
    await screen.findByText('admin');

    fireEvent.click(await screen.findByRole('button', { name: 'New user' }));
    const dialog = await screen.findByRole('dialog');
    const login = await within(dialog).findByRole('textbox', { name: /^Login/ });
    const display = within(dialog).getByRole('textbox', { name: /^Display name/ });

    await settle();
    const before = renders.table;
    typeInto(login, 'jdoe');
    typeInto(display, 'John Doe');

    expect((login as HTMLInputElement).value).toBe('jdoe');
    expect(renders.table - before).toBe(0);

    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(fetchMock.mock.calls.some(([, init]) => (init as RequestInit | undefined)?.method === 'POST')).toBe(true);
    });
    expect(postBody('/api/v1/users')).toMatchObject({ userName: 'jdoe', displayName: 'John Doe' });
  });
});

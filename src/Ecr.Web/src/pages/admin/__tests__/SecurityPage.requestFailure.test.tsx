import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { SecurityPage } from '@/pages/admin/SecurityPage';
import { withTestDefaults } from '@/test/render';

/**
 * Відмова запиту — не «даних немає» (клас `data ?? []`, зразок — #446).
 *
 * ⛔ Відмова `GET /permissions` малювала матрицю «ролі × права» без жодної
 * колонки прав: це читалося як «у ролей немає прав», а форма нової ролі
 * виходила без прапорців і дозволяла зберегти роль без прав. Відмова
 * `GET /roles` була видима лише на вкладці «Ролі».
 *
 * ⚠ Відмови справжні (`500` від `fetch`), не `mockRejectedValue`: шлях той
 * самий, яким відмова приходить у застосунку.
 */
const Strings: Record<string, string> = {
  'security.createRole': 'New role',
  'security.createUser': 'New user',
  'common.save': 'Save',
};

const Roles = [
  { id: 1, code: 'Operators', isActive: true, isBuiltIn: false, dangerousPermissions: [], permissions: ['Document.View'] },
];

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const refusal = (detail: string): Response =>
  new Response(
    JSON.stringify({ type: 'about:blank', title: 'Internal Server Error', status: 500, detail, errorCode: 'ECR-SYS-0500', correlationId: 'cid-1' }),
    { status: 500, headers: { 'Content-Type': 'application/problem+json' } },
  );

function stubFetch(failing: 'permissions' | 'roles' | 'none'): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });
      if (path.endsWith('/me')) {
        return json({ userId: 0, userName: 'test', language: 'en', isSimulation: false, permissions: ['Security.ManageRoles', 'Security.ManageUsers'] });
      }
      if (path.endsWith('/permissions')) {
        return failing === 'permissions' ? refusal('catalog') : json([{ code: 'Document.View', group: 'Document', isDangerous: false }]);
      }
      if (path.endsWith('/api/v1/roles')) return failing === 'roles' ? refusal('roles') : json(Roles);
      if (path.endsWith('/languages')) return json([{ code: 'en', nameNative: 'English', isDefault: true, isActive: true }]);
      if (path.endsWith('/users')) return json({ items: [], nextCursor: null, totalCount: 0 });
      return json([]);
    }),
  );
}

async function show(tab: string): Promise<void> {
  await loadCatalog('en', 'private');

  render(
    <MantineProvider theme={withTestDefaults(theme)}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={[`/admin/security?tab=${tab}`]}>
          <SecurityPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

// ⚠ Не 400 с, як у сусідів: на мутації кожне очікування висіло б до стелі.
const Slow = 30_000;

describe('SecurityPage: відмова запиту не виглядає як порожні дані', () => {
  it('каталог прав не приїхав — причина з кодом, а НЕ матриця без колонок прав', async () => {
    stubFetch('permissions');
    await show('roles');

    const alert = await screen.findByRole('alert', {}, { timeout: Slow });
    expect(alert.textContent ?? '').toContain('ECR-SYS-0500');

    // ⛔ Ролі приїхали — і саме тому стара версія малювала таблицю з однією
    // колонкою. Порядок важливий: твердження після банера, тобто коли
    // відмова вже в стані.
    expect(screen.queryByRole('table')).toBeNull();
    expect(screen.queryByText('Operators')).toBeNull();
  }, Slow);

  it('дзеркало: обидва запити вдалі — матриця з колонкою права, банера немає', async () => {
    stubFetch('none');
    await show('roles');

    const table = await screen.findByRole('table', {}, { timeout: Slow });
    expect(within(table).getByText('Document.View')).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
  }, Slow);

  it('форма ролі без каталогу: причина у вікні, «Зберегти» вимкнено', async () => {
    stubFetch('permissions');
    await show('roles');

    await userEvent.click(await screen.findByRole('button', { name: 'New role' }, { timeout: Slow }));

    const dialog = await screen.findByRole('dialog', {}, { timeout: Slow });
    expect(within(dialog).getByRole('alert').textContent ?? '').toContain('ECR-SYS-0500');

    // ⚠ Заповнюємо обов'язкове, інакше кнопка вимкнена й без правки.
    const boxes = await waitFor(() => {
      const found = within(dialog).getAllByRole('textbox');
      expect(found.length).toBe(2);
      return found;
    });
    await userEvent.type(boxes[0] as HTMLElement, 'NewRole');
    await userEvent.type(boxes[1] as HTMLElement, 'New role');

    expect((within(dialog).getByRole('button', { name: 'Save' }) as HTMLButtonElement).disabled).toBe(true);
  }, Slow);

  it('ролі не приїхали — на вкладці «Користувачі» це видно, а не лише на «Ролях»', async () => {
    stubFetch('roles');
    await show('users');

    const alert = await waitFor(() => screen.getByRole('alert'), { timeout: Slow });
    expect(alert.textContent ?? '').toContain('ECR-SYS-0500');
  }, Slow);
});

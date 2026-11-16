import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type { RoleView } from '@/api/types';
import { RoleBrowser } from '@/features/security/RoleBrowser';
import { keepMountedModalsTheme } from '@/test/keepMountedModals';

/**
 * B145: діалог видалення ролі, що вже закрився, не видаляє ВДРУГЕ — і тим паче не видаляє ІНШУ роль.
 *
 * ⛔ Діра (зонд на базі b746a5add, справжній `RoleBrowser` + перелік ролей із сервера): у шапці обрана
 * роль `Twin` (id 3) → More → Delete → підтвердження: `DELETE /roles/3`. Після відповіді перелік
 * перечитано — ролі 3 в ньому вже немає, і панель показує ПЕРШУ роль, `NightShift` (id 2): `RoleActions`
 * при цьому не перемонтовується, йому лише приходить інший `role`. Далі `close()`, і власний `Modal`
 * діалогу ще ~200 мс у DOM із кнопкою «Delete», яка читала `role.id` на момент кліку — другий клік
 * подвійного кліку слав `DELETE /roles/2`: видалення іншої ролі, якого ніхто не підтверджував.
 *
 * Захист — у самому `RoleActions` (власний `Modal`; кореневий захист `shared/ui/ConfirmModal` сюди не
 * дістає): кнопка діє лише у відкритому діалозі й не під час запиту, а ціль (`id`, `code`) зафіксована в
 * момент відкриття.
 *
 * Мутаційні докази (`RoleActions.tsx`):
 * - прибрати `action !== 'delete'` з умови кнопки «Delete» → червоний перший тест
 *   (`expected [ 'DELETE /api/v1/roles/3', …(1) ] to deeply equal [ 'DELETE /api/v1/roles/3' ]`; на базі
 *   другим ішов `DELETE /api/v1/roles/2`, із зафіксованою ціллю — повторний `DELETE /api/v1/roles/3`);
 * - слати `role.id` замість зафіксованого `target.id` → червоний другий тест
 *   (`expected [ 'DELETE /api/v1/roles/2' ] to deeply equal [ 'DELETE /api/v1/roles/3' ]`).
 *
 * ⚠ Без залежності від часу: `Modal` справжній, тема `keepMountedModalsTheme` лишає закритий діалог у DOM
 * назавжди; відповіді сервера віддає тест.
 */

const NightShift: RoleView = {
  id: 2,
  code: 'NightShift',
  isActive: true,
  isBuiltIn: false,
  permissions: ['Document.View'],
  dangerousPermissions: [],
};

const Twin: RoleView = {
  id: 3,
  code: 'Twin',
  isActive: true,
  isBuiltIn: false,
  permissions: ['Document.View'],
  dangerousPermissions: [],
};

/** Що зараз віддає `GET /roles`. */
let roles: RoleView[] = [];
/** Запити запису, що дійшли до «сервера». */
const sent: string[] = [];
/** Відпускає відповідь на останній `DELETE`; `null` — запиту ще не було. */
let respond: (() => void) | null = null;

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input).split('?')[0] ?? '';
      const method = String(init?.method ?? 'GET');

      if (method === 'DELETE') {
        sent.push(`DELETE ${url}`);
        await new Promise<void>((resolve) => {
          respond = resolve;
        });
        // Сервер видалив: у переліку ролі більше немає.
        roles = roles.filter((role) => !url.endsWith(`/roles/${String(role.id)}`));

        return new Response(null, { status: 204 });
      }

      if (url.endsWith('/api/v1/roles')) return json(roles);

      return json({});
    }),
  );
}

/** Як `SecurityPage`: перелік ролей — запитом `['roles']`, який `RoleActions` інвалідує після дії. */
function Host(): JSX.Element | null {
  const list = useQuery({ queryKey: ['roles'], queryFn: () => apiFetch<RoleView[]>('/api/v1/roles') });

  return list.data === undefined ? null : <RoleBrowser roles={list.data} permissions={[]} canManage />;
}

function show(): QueryClient {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={keepMountedModalsTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/security?role=Twin']}>
          <Host />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );

  return client;
}

/** Відкриває діалог видалення обраної ролі й повертає його кнопку «Delete». */
async function openDelete(): Promise<{ dialog: HTMLElement; confirm: HTMLButtonElement }> {
  fireEvent.click(await screen.findByTestId('role-more'));
  fireEvent.click(await screen.findByRole('menuitem', { name: '⟦common.delete⟧' }));

  const dialog = await screen.findByRole('dialog');
  const confirm = within(dialog).getByRole('button', { name: '⟦common.delete⟧' }) as HTMLButtonElement;

  return { dialog, confirm };
}

const shownRole = (): string | null => screen.getByTestId('role-detail').getAttribute('aria-label');

afterEach(() => {
  roles = [];
  sent.length = 0;
  respond = null;
  vi.unstubAllGlobals();
});

describe('B145: видалення ролі — діалог, що закрився', () => {
  it('клік по «Delete» діалогу після видалення не шле другого DELETE (панель уже показує іншу роль)', async () => {
    roles = [NightShift, Twin];
    mockServer();
    show();

    await waitFor(() => {
      expect(shownRole()).toBe('Twin');
    });
    const { dialog, confirm } = await openDelete();
    expect(dialog.textContent).toContain('Twin');

    fireEvent.click(confirm);
    await waitFor(() => {
      expect(sent).toEqual(['DELETE /api/v1/roles/3']);
    });

    // Сервер видалив, перелік перечитано: панель показує іншу роль, діалог закрито, зайнятість знято.
    respond?.();
    await waitFor(() => {
      expect(shownRole()).toBe('NightShift');
      expect(confirm).toHaveProperty('disabled', false);
      expect(screen.queryByRole('dialog')).toBeNull();
    });

    // Клік по кнопці діалогу, що закрився: вона ще в DOM і не вимкнена.
    expect(confirm.isConnected).toBe(true);
    fireEvent.click(confirm);

    // Незахищена мутація стартує одразу: кілька тіків циклу подій — і другий DELETE вже був би в журналі.
    await new Promise<void>((resolve) => setImmediate(resolve));
    await new Promise<void>((resolve) => setImmediate(resolve));
    expect(sent).toEqual(['DELETE /api/v1/roles/3']);
    // І справді нічого не видалено понад підтверджене.
    expect(roles).toEqual([NightShift]);
  });

  it('ціль зафіксована при відкритті: панель перемкнулася під відкритим діалогом — видаляється підтверджена роль', async () => {
    roles = [NightShift, Twin];
    mockServer();
    const client = show();

    await waitFor(() => {
      expect(shownRole()).toBe('Twin');
    });
    const { dialog, confirm } = await openDelete();
    expect(dialog.textContent).toContain('Twin');

    // Поки діалог відкритий, перелік перечитався без обраної ролі (її прибрав інший адміністратор).
    roles = [NightShift];
    await client.invalidateQueries({ queryKey: ['roles'] });
    await waitFor(() => {
      expect(shownRole()).toBe('NightShift');
    });

    // Діалог далі питає про ту роль, для якої його відкрили, — і видаляє саме її.
    expect(dialog.textContent).toContain('Twin');
    expect(dialog.textContent).not.toContain('NightShift');
    fireEvent.click(confirm);
    await waitFor(() => {
      expect(sent).toHaveLength(1);
    });
    expect(sent).toEqual(['DELETE /api/v1/roles/3']);
    respond?.();
  });
});

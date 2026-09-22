import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RoleView, UserView } from '@/api/types';
import { UserAccessEditor } from '@/features/security/UserAccessEditor';
import { testTheme } from '@/test/render';

/**
 * UI-аудит, lane 1 (`lane1-roles-dropdown-traps-clicks.md`): жодне з
 * `Escape`, кліку по шеврону мультиселекту чи кліку деінде в діалозі не
 * закривало відкритий список опцій ролей — він лишався ПОВЕРХ Save/Cancel і
 * перехоплював кліки, призначені для них (клік по координатах "Save" обирав
 * роль замість збереження). Єдиний робочий спосіб закрити список, лишившись
 * у діалозі, був `Tab` (blur).
 *
 * ✎ Тут `MultiSelect` підмінявся заглушником, що САМ малював свій
 * «дропдаун» (`roles-dropdown-state`, власна `role="listbox"`), — нібито
 * тому, що справжній «зависає під jsdom». Причина зависання знайдена й
 * усунена: взаємна рекурсія jsdom ↔ nwsapi на станових псевдокласах
 * (коментар у `src/test/setup.ts`). Тепер відкритість списку читається з
 * САМОГО компонента — за наявністю видимих `role="option"`, — тож тест
 * нарешті перевіряє ту поведінку, заради якої написаний, а не імітацію.
 */

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
  lastSignInAt: null,
};

const roles: RoleView[] = [
  { id: 1, code: 'DataEntry', isActive: true, isBuiltIn: false, dangerousPermissions: [], permissions: [] },
];

function respond(body: unknown): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    ),
  );
}

function show(onClose: () => void): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <UserAccessEditor user={user} roles={roles} onClose={onClose} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const RolesLabel = '⟦security.roles⟧';

/**
 * Чи розкритий список ролей. Читається з самого `MultiSelect`: доки список
 * закритий, Mantine ховає випадний блок, і жодної ВИДИМОЇ `role="option"` у
 * дереві немає (перевірено: `queryAllByRole('option', { hidden: true })`
 * теж порожній).
 */
function rolesDropdownState(): 'open' | 'closed' {
  return screen.queryAllByRole('option').length > 0 ? 'open' : 'closed';
}

/**
 * Обрані ролі так, як їх показує сам `MultiSelect` — «пігулками» над полем.
 * Це те саме твердження, що читав `data-testid="roles-value"` заглушника,
 * але з реального дерева компонента.
 */
function selectedRoles(): string[] {
  return Array.from(document.querySelectorAll('.mantine-Pill-label')).map(
    (pill) => pill.textContent ?? '',
  );
}

/**
 * ⚠ Повертає саме поле пошуку: щойно список розкрито, підпис «ролі» мають
 * ДВА елементи (видиме поле й приховане поле значення), і `getByLabelText`
 * після відкриття вже неоднозначний.
 */
async function openRolesDropdown(): Promise<HTMLElement> {
  const field = await screen.findByLabelText(RolesLabel);
  expect(rolesDropdownState()).toBe('closed');

  fireEvent.click(field);
  expect(rolesDropdownState()).toBe('open');

  return field;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('UserAccessEditor: відкритий список ролей не блокує діалог (lane1)', () => {
  it('Escape закриває ЛИШЕ список ролей, не весь діалог', async () => {
    respond(['DataEntry']);
    const onClose = vi.fn();
    show(onClose);
    const rolesField = await openRolesDropdown();

    // ⛔ Мутаційний доказ (RED до фіксу): без `closeOnEscape={!rolesOpened}`
    // на `Modal` цей самий `Escape` викликав би `onClose` (Modal's власний
    // window-level capture-listener).
    fireEvent.keyDown(rolesField, { key: 'Escape' });

    expect(rolesDropdownState()).toBe('closed');
    expect(onClose).not.toHaveBeenCalled();
  });

  it('шеврон мультиселекту перемикає (toggle) список — відкриває і закриває', async () => {
    respond(['DataEntry']);
    show(() => {});
    await screen.findByLabelText(RolesLabel);
    expect(rolesDropdownState()).toBe('closed');

    const toggle = screen.getByTestId('roles-dropdown-toggle');

    // ⛔ Мутаційний доказ: стоковий `MultiSelect.onClick` для `searchable`
    // викликає лише `openDropdown()` — без власного toggle-обробника цей
    // клік не міняв би стан узагалі, якщо він уже відкритий.
    fireEvent.click(toggle);
    expect(rolesDropdownState()).toBe('open');

    fireEvent.click(toggle);
    expect(rolesDropdownState()).toBe('closed');
  });

  it('клік по реальній опції (role="option") всередині дропдауна проходить як звичайно', async () => {
    respond(['DataEntry']);
    show(() => {});
    await openRolesDropdown();

    // Роль `DataEntry` вже призначена (відповідь сервера вище), тож клік по
    // її опції — це зняття вибору.
    expect(selectedRoles()).toEqual(['DataEntry']);

    fireEvent.click(screen.getByRole('option', { name: 'DataEntry' }));

    // Клік усередині випадного блоку НЕ мав бути перехоплений
    // capture-обробником — `onChange` мультиселекту спрацював як зазвичай.
    expect(selectedRoles()).toEqual([]);
  });

  it('клік по Cancel, поки список відкритий, лише закриває список і НЕ скасовує діалог', async () => {
    respond(['DataEntry']);
    const onClose = vi.fn();
    show(onClose);
    await openRolesDropdown();

    // ⛔ Найважливіший мутаційний доказ: до фіксу цей клік або скасовував
    // би ВЕСЬ діалог (якщо влучав по Cancel), або (в реальному, не
    // заглушеному компоненті) обирав би роль під дропдауном, що лишався
    // поверх кнопки, — жодного разу не робив «просто закрий список».
    fireEvent.click(screen.getByRole('button', { name: '⟦common.cancel⟧' }));

    expect(rolesDropdownState()).toBe('closed');
    expect(onClose).not.toHaveBeenCalled();

    // Другий клік, коли список уже закритий, працює як звичайна кнопка.
    fireEvent.click(screen.getByRole('button', { name: '⟦common.cancel⟧' }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});

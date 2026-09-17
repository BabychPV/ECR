import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
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
 * ⚠ Той самий відтворюваний факт, що й в інших тестах цього файлу:
 * `MultiSelect` під jsdom «зависає» (floating-ui/portal-позиціонування
 * потребують реального layout), тож підмінено легким заглушником, що
 * форвардить рівно ті пропси, від яких залежить фікс: `dropdownOpened`,
 * `onDropdownOpen`/`onDropdownClose`, `rightSection`, `onChange`. Саму
 * логіку показу/приховування дропдауна (`Combobox.Chevron` для шеврону,
 * `role="listbox"`/`role="option"` для реальних опцій) НЕ підмінено — вона
 * рендериться напряму з некомпрометованого `@mantine/core`, тож перевіряє
 * справжню поведінку `stopPropagation`/capture-обробників, доданих у
 * `UserAccessEditor.tsx`.
 */
vi.mock('@mantine/core', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/core')>();

  function StubMultiSelect(props: {
    label?: string;
    value?: string[];
    onChange?: (value: string[]) => void;
    dropdownOpened?: boolean;
    onDropdownOpen?: () => void;
    rightSection?: React.ReactNode;
  }): JSX.Element {
    return (
      <div>
        <div data-testid="roles-value">{(props.value ?? []).join(',')}</div>
        <div data-testid="roles-dropdown-state">{props.dropdownOpened ? 'open' : 'closed'}</div>
        <button type="button" onClick={() => props.onDropdownOpen?.()}>
          open roles dropdown
        </button>
        {props.rightSection}
        {props.dropdownOpened && (
          <div role="listbox">
            <button type="button" role="option" onClick={() => props.onChange?.(['DataEntry'])}>
              DataEntry
            </button>
          </div>
        )}
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

async function openRolesDropdown(): Promise<void> {
  await waitFor(() => {
    expect(screen.getByTestId('roles-dropdown-state').textContent).toBe('closed');
  });
  fireEvent.click(screen.getByText('open roles dropdown'));
  expect(screen.getByTestId('roles-dropdown-state').textContent).toBe('open');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('UserAccessEditor: відкритий список ролей не блокує діалог (lane1)', () => {
  it('Escape закриває ЛИШЕ список ролей, не весь діалог', async () => {
    respond(['DataEntry']);
    const onClose = vi.fn();
    show(onClose);
    await openRolesDropdown();

    // ⛔ Мутаційний доказ (RED до фіксу): без `closeOnEscape={!rolesOpened}`
    // на `Modal` цей самий `Escape` викликав би `onClose` (Modal's власний
    // window-level capture-listener).
    fireEvent.keyDown(screen.getByText('open roles dropdown'), { key: 'Escape' });

    expect(screen.getByTestId('roles-dropdown-state').textContent).toBe('closed');
    expect(onClose).not.toHaveBeenCalled();
  });

  it('шеврон мультиселекту перемикає (toggle) список — відкриває і закриває', async () => {
    respond(['DataEntry']);
    show(() => {});
    await waitFor(() => {
      expect(screen.getByTestId('roles-dropdown-state').textContent).toBe('closed');
    });

    const toggle = screen.getByTestId('roles-dropdown-toggle');

    // ⛔ Мутаційний доказ: стоковий `MultiSelect.onClick` для `searchable`
    // викликає лише `openDropdown()` — без власного toggle-обробника цей
    // клік не міняв би стан узагалі, якщо він уже відкритий.
    fireEvent.click(toggle);
    expect(screen.getByTestId('roles-dropdown-state').textContent).toBe('open');

    fireEvent.click(toggle);
    expect(screen.getByTestId('roles-dropdown-state').textContent).toBe('closed');
  });

  it('клік по реальній опції (role="option") всередині дропдауна проходить як звичайно', async () => {
    respond(['DataEntry']);
    show(() => {});
    await openRolesDropdown();

    fireEvent.click(screen.getByRole('option', { name: 'DataEntry' }));

    // Клік усередині `role="listbox"` НЕ мав бути перехоплений
    // capture-обробником — `onChange` мультиселекту спрацював як зазвичай.
    expect(screen.getByTestId('roles-value').textContent).toBe('DataEntry');
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

    expect(screen.getByTestId('roles-dropdown-state').textContent).toBe('closed');
    expect(onClose).not.toHaveBeenCalled();

    // Другий клік, коли список уже закритий, працює як звичайна кнопка.
    fireEvent.click(screen.getByRole('button', { name: '⟦common.cancel⟧' }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});

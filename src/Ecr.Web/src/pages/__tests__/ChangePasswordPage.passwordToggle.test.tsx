import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { JSX } from 'react';
import { ChangePasswordPage } from '@/pages/ChangePasswordPage';

/**
 * Тумблери видимості пароля на екрані зміни пароля (`Q-260`).
 *
 * ⛔ Три поля `PasswordInput` (поточний, новий, повтор) — і всі три ділять
 * ту саму ваду Mantine: `aria-hidden="true"`/`tabIndex={-1}` на кнопці
 * тумблера за замовчуванням, доки `visibilityToggleButtonProps` цього не
 * перекриє. Перевіряються всі три, а не один представник: три окремі
 * елементи `PasswordInput` — три окремі виклики компонента з окремими
 * пропсами, і пропущений на одному з них `visibilityToggleButtonProps`
 * ніяк не виявився б перевіркою лише першого поля.
 */
function show(): JSX.Element {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <ChangePasswordPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  ) as unknown as JSX.Element;
}

describe('ChangePasswordPage: тумблери видимості пароля (Q-260)', () => {
  it('усі три тумблери доступні з клавіатури і мають ім\'я для читалки', () => {
    show();

    const toggles = screen.getAllByRole('button', { name: 'Toggle password visibility' });

    // ⛔ Головне заперечення: без фіксу `aria-hidden="true"` виключає кнопку
    // з дерева доступності, і `getAllByRole` вище знайшов би НУЛЬ елементів
    // замість трьох.
    expect(toggles).toHaveLength(3);

    for (const toggle of toggles) {
      expect(toggle.getAttribute('aria-hidden')).not.toBe('true');
      expect(toggle.getAttribute('tabindex')).toBe('0');
    }
  });
});

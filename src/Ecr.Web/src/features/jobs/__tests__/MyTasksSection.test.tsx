import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { MeQueryKey } from '@/shared/session/useSession';
import { MyTasksSection } from '@/features/jobs/MyTasksSection';
import { collapsedStorageKey } from '@/features/jobs/myTasksCollapse';

/**
 * ✎ UAT my-tasks-collapse: блок шухляди «My tasks» згортається, стан пам'ятається для КОЖНОГО користувача окремо.
 * Мутація: прибрати id користувача з `collapsedStorageKey` - «незалежно для іншого користувача» червоний; прибрати
 * try/catch у читанні сховища - «сховище кидає» червоний; не писати в сховище - «після перемонтування» червоний.
 */

function show(userId: number | null): void {
  const client = new QueryClient();
  if (userId !== null) client.setQueryData(MeQueryKey, { userId });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MyTasksSection id="my-tasks-test" title="Returned for rework" count={3}>
          <p>row body</p>
        </MyTasksSection>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function toggle(): HTMLElement {
  return screen.getByRole('button', { name: /Returned for rework/ });
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  localStorage.clear();
});

describe('MyTasksSection', () => {
  it('заголовок - кнопка з лічильником, aria-expanded і aria-controls на тіло', () => {
    show(7);
    const button = toggle();
    expect(button.getAttribute('aria-expanded')).toBe('true');
    expect(button.textContent).toContain('3');
    const body = document.getElementById(button.getAttribute('aria-controls') ?? '');
    expect(body).not.toBeNull();
    expect(body?.hidden).toBe(false);
    expect(screen.getByRole('region', { name: 'Returned for rework' })).toBeTruthy();
  });

  it('згортання переживає перемонтування для того самого користувача', async () => {
    show(7);
    await userEvent.click(toggle());
    expect(toggle().getAttribute('aria-expanded')).toBe('false');
    expect(screen.getByText('row body').closest('[hidden]')).not.toBeNull();
    cleanup();

    show(7);
    expect(toggle().getAttribute('aria-expanded')).toBe('false');
    expect(localStorage.getItem(collapsedStorageKey(7, 'my-tasks-test'))).toBe('1');
  });

  it('незалежно для іншого користувача', async () => {
    show(7);
    await userEvent.click(toggle());
    cleanup();

    show(8);
    expect(toggle().getAttribute('aria-expanded')).toBe('true');
  });

  it('сховище кидає - блок рендериться розгорнутим і все одно згортається', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('denied');
    });
    show(7);
    expect(toggle().getAttribute('aria-expanded')).toBe('true');
    await userEvent.click(toggle());
    expect(toggle().getAttribute('aria-expanded')).toBe('false');
  });

  it('клавіатура: Enter і Space перемикають, фокус лишається на заголовку', async () => {
    show(7);
    const user = userEvent.setup();
    toggle().focus();
    await user.keyboard('{Enter}');
    expect(toggle().getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(toggle());
    await user.keyboard(' ');
    expect(toggle().getAttribute('aria-expanded')).toBe('true');
    expect(document.activeElement).toBe(toggle());
  });
});

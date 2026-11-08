import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CollapsibleNote, noteStorageKey, readNoteCollapsed } from '@/features/common/CollapsibleNote';
import { testTheme } from '@/test/render';

/**
 * ✎ fg6 (людина 08.11: «ця частина не має займати все вікно і має мати можливість згортатися»): пояснення
 * згортається, згорнуте показує один рядок підсумку, вибір пам'ятається на людину, а без сховища сторінка працює.
 */

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient()}>
        <CollapsibleNote noteId="t" summary="Summary line">
          Details text
        </CollapsibleNote>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  window.localStorage.clear();
  vi.restoreAllMocks();
});

describe('CollapsibleNote (fg6)', () => {
  it('згортання ховає подробиці, лишає підсумок і пам’ятається', async () => {
    const user = userEvent.setup();
    show();

    const toggle = screen.getByRole('button', { name: /Summary line/ });
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(screen.getByText('Details text').hidden).toBe(false);

    await user.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(screen.getByText('Details text').hidden).toBe(true);
    // ⛔ Мутація «не писати в сховище» дає тут null.
    expect(readNoteCollapsed(noteStorageKey('t', undefined))).toBe(true);
  });

  it('збережений вибір відновлюється при наступному відкритті', () => {
    window.localStorage.setItem(noteStorageKey('t', undefined), '1');
    show();

    expect(screen.getByRole('button', { name: /Summary line/ }).getAttribute('aria-expanded')).toBe('false');
  });

  it('сховище недоступне — не падає, показує розгорнутим', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    show();

    expect(screen.getByRole('button', { name: /Summary line/ }).getAttribute('aria-expanded')).toBe('true');
  });
});

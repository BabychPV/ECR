import { describe, it, expect, vi, afterEach } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ImportPreview } from '@/api/types';
import { ImportPanel } from '../ImportPanel';
import { testTheme } from '@/test/render';

/**
 * T3-05: після Esc у діалозі «Review the import» фокус падав на `BODY` (кнопка «Import from Excel»
 * його не отримувала). Системне вікно вибору файлу забирає фокус, тож у мить відкриття діалогу
 * Mantine запам'ятовував уже `body`.
 */
const Preview: ImportPreview = { previewToken: 'tok', changes: [], rejected: [], conflicts: [] };

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ImportPanel: повернення фокуса після закриття перегляду', () => {
  it('Esc повертає фокус на кнопку вибору файлу, навіть якщо системне вікно забрало його', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(JSON.stringify(Preview), { status: 200, headers: { 'Content-Type': 'application/json' } })),
    );

    const { container } = render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <ImportPanel documentId={1} periodKey={202609} />
        </QueryClientProvider>
      </MantineProvider>,
    );

    const user = userEvent.setup();
    const button = screen.getByRole('button', { name: '⟦import.pick⟧' });
    await user.click(button);

    // Системне вікно вибору файлу: фокус пішов із кнопки.
    (document.activeElement as HTMLElement | null)?.blur();
    expect(document.activeElement).toBe(document.body);

    const input = container.querySelector('input[type="file"]');
    if (!(input instanceof HTMLInputElement)) throw new Error('немає поля вибору файлу');
    fireEvent.change(input, { target: { files: [new File(['x'], 'book.xlsx')] } });

    await screen.findByRole('dialog');
    await user.keyboard('{Escape}');

    // ⛔ Мутація «прибрати remember()/restore()» лишає фокус на body — тест червоний.
    await waitFor(() => expect(document.activeElement).toBe(button));
  });
});

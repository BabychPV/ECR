import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { Button, MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RegistryEntryDto } from '@/api/types';
import { testTheme } from '@/test/render';
import { LazyValidityEditor } from '../lazyDialogs';

/**
 * WCAG 2.4.3: «Validity» запису довідника (`LazyValidityEditor`, D-132) — натиснули РАНІШЕ, ніж приїхав чанк.
 *
 * ⛔ Діалог монтується ВЖЕ відкритим, `returnFocus` Mantine переходу `opened` false → true не бачить, і після закриття
 * фокус падав на `BODY`. Чанк тримає `vi.mock` із воротами — модуль віддається лише після кліку. Окремий файл, бо модуль
 * спільний із `RegistryEntryEditor` (`lazyDialogs.focus.test.tsx`), а вантажиться у файлі лише раз.
 *
 * Мутаційний доказ: прибрати `useRestoreFocus(props.entry !== null)` з `LazyValidityEditor` — тест червоний.
 *
 * ⚠ Каталог не вантажиться: `t()` дає `⟦ключ⟧`.
 */
const chunk = vi.hoisted(() => {
  let release: () => void = () => undefined;
  const ready = new Promise<void>((resolve) => {
    release = resolve;
  });

  return { ready, open: () => release() };
});

vi.mock('@/features/registries/RegistryEntryEditor', async (importOriginal) => {
  await chunk.ready;

  return importOriginal();
});

const Entry = {
  id: 42,
  code: 'KG',
  display: 'Kilogram',
  parentEntryId: null,
  validFrom: null,
  validTo: null,
} as unknown as RegistryEntryDto;

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('LazyValidityEditor: фокус після закриття, коли чанк запізнився', () => {
  it('«Validity» → чанк приїхав → «Cancel» → фокус знову на кнопці', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } })),
    );
    function Harness(): JSX.Element {
      const [validity, setValidity] = useState<RegistryEntryDto | null>(null);

      return (
        <>
          <Button onClick={() => setValidity(Entry)}>Validity</Button>
          <LazyValidityEditor registryCode="CHEM" entry={validity} onClose={() => setValidity(null)} />
        </>
      );
    }
    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Harness />
        </QueryClientProvider>
      </MantineProvider>,
    );
    const opener = screen.getByRole('button', { name: 'Validity' });
    opener.focus();

    fireEvent.click(opener);
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    expect(screen.queryByRole('dialog')).toBeNull();

    chunk.open();
    const dialog = await screen.findByRole('dialog');
    await waitFor(() => expect(dialog.contains(document.activeElement)).toBe(true));

    fireEvent.click(within(dialog).getByRole('button', { name: /common\.cancel/ }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(opener));
  });
});

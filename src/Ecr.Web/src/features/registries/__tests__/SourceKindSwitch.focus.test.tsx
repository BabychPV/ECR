import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RegistryDefDto } from '@/api/types';
import { SourceKindSwitch } from '@/features/registries/SourceKindSwitch';
import { testTheme } from '@/test/render';

/**
 * WCAG 2.4.3: «Switch source» (`SourceKindSwitch`, діалог лінивим чанком, D-132) — натиснули РАНІШЕ, ніж приїхав чанк.
 *
 * ⛔ Діалог монтується ВЖЕ відкритим, `returnFocus` Mantine переходу `opened` false → true не бачить, і після закриття
 * фокус падав на `BODY`. Чанк тримає `vi.mock` із воротами — модуль віддається лише після кліку.
 *
 * Мутаційний доказ: прибрати `useRestoreFocus(opened)` з `SourceKindSwitch` — тест червоний.
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

vi.mock('../SourceKindSwitchDialog', async (importOriginal) => {
  await chunk.ready;

  return importOriginal();
});

const Registries: RegistryDefDto[] = [
  {
    id: 1,
    code: 'UNITS',
    nameL10n: { values: { en: 'Units' } },
    fields: [],
    isTemporal: false,
    isHierarchical: false,
    sourceKind: 'Local',
  },
];

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('SourceKindSwitch: фокус після закриття, коли чанк запізнився', () => {
  it('кнопка → чанк приїхав → «Cancel» → фокус знову на кнопці', async () => {
    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={new QueryClient()}>
          <SourceKindSwitch registries={Registries} />
        </QueryClientProvider>
      </MantineProvider>,
    );
    const opener = screen.getByRole('button', { name: /registries\.sourceSwitch/ });
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

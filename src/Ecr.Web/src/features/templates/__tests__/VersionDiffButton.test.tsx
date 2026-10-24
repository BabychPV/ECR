import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { VersionDiffButton } from '../VersionDiffButton';

/**
 * `VersionDiffButton` (бюджет `D-132`): діалог порівняння — лінивий чанк, змонтований уже відкритим.
 *
 * ⛔ Головний ризик такого винесення — фокус. Власний `returnFocus` Mantine запам'ятовує кнопку лише на зміні
 * `opened` false → true, а лінивий діалог її не бачить: без `useRestoreFocus` у `VersionDiffDialog` фокус після
 * закриття падав би на `BODY`.
 *
 * Мутаційний доказ (вручну, відновлено): прибрати `useRestoreFocus(opened)` з `VersionDiffDialog` — другий тест
 * червоний.
 *
 * ⚠ Каталог не вантажиться: `t()` дає `⟦ключ⟧`.
 */
const Versions = [
  { id: 10, version: '1.0', status: 'Published', presentationRevision: 0, clonedFromVersionId: null, publishedAt: null },
  { id: 11, version: '2.0', status: 'Draft', presentationRevision: 0, clonedFromVersionId: 10, publishedAt: null },
] as const;

function renderButton(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } })),
  );
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <VersionDiffButton templateVersionId={11} versions={Versions} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('VersionDiffButton: лінивий діалог порівняння', () => {
  it('до натискання діалогу немає; після — той самий діалог із вибором другої версії', async () => {
    renderButton();

    expect(screen.queryByRole('dialog')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: '⟦version.diff⟧' }));
    const dialog = await screen.findByRole('dialog');

    expect(within(dialog).getByLabelText(/version\.diffOther⟧/)).toBeDefined();
    expect(within(dialog).getByRole('button', { name: '⟦common.close⟧' })).toBeDefined();
  });

  it('«Close» повертає фокус на кнопку «Порівняти», а повторне відкриття — той самий діалог', async () => {
    renderButton();
    const trigger = screen.getByRole('button', { name: '⟦version.diff⟧' });
    trigger.focus();

    fireEvent.click(trigger);
    const dialog = await screen.findByRole('dialog');
    await waitFor(() => expect(dialog.contains(document.activeElement)).toBe(true));

    fireEvent.click(within(dialog).getByRole('button', { name: '⟦common.close⟧' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(trigger));

    fireEvent.click(trigger);
    expect(await screen.findByRole('dialog')).toBeDefined();
  });
});

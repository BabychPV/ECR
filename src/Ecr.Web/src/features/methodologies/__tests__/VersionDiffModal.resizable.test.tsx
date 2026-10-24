import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { MethodologyDraftVersionDto } from '@/api/types';
import { VersionDiffModal } from '@/features/methodologies/VersionDiffModal';
import { storageKey } from '@/shared/ui/modalSize';
import { testTheme } from '@/test/render';

/**
 * Порівняння версій методології — у вікні зі змінним розміром (P44): людина розгортає довгий
 * diff на весь екран або тягне кут, і вікно пам'ятає це для наступного порівняння.
 *
 * ⚠ Справжній компонент і справжній запит (`fetch`-стаб): перевіряється, що обгортка дійшла
 * до вікна методології, а не лише до себе самої (`ResizableModal.test.tsx`).
 */

function version(id: number, status: 'Draft' | 'Published', number: string): MethodologyDraftVersionDto {
  return {
    calendarMode: 'Actual',
    createdByUserId: 1,
    effectiveFrom: status === 'Draft' ? null : '2026-01-01',
    id,
    isEditable: status === 'Draft',
    level: 'Configuration',
    numericMode: 'Strict',
    status,
    traceLevel: 'ErrorsOnly',
    versionNumber: number,
  };
}

const Versions = [version(11, 'Published', '2026.1'), version(13, 'Draft', '2026.2')];

function show(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(
      async () =>
        new Response(JSON.stringify({ baseVersionId: 11, methodologyVersionId: 13, items: [] }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
    ),
  );

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const target = Versions[1] ?? null;

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <VersionDiffModal methodologyId={5} versions={Versions} target={target} onClose={() => {}} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  try {
    globalThis.localStorage.clear();
  } catch {
    // Сховища може не бути.
  }
});

describe('VersionDiffModal: розмір вікна (P44)', () => {
  it('розгортається на весь екран і пам’ятає це під ключем methodology.versionDiff', async () => {
    show();
    const dialog = await screen.findByRole('dialog');
    const toggle = within(dialog).getByRole('button', { name: 'Full screen' });

    expect(toggle.getAttribute('aria-pressed')).toBe('false');
    fireEvent.click(toggle);

    expect(toggle.getAttribute('aria-pressed')).toBe('true');
    expect(dialog.getAttribute('data-rm-size')).toBe('full');
    expect(globalThis.localStorage.getItem(storageKey('methodology.versionDiff'))).toBe('{"mode":"full"}');
  });

  it('ручка розміру є і змінює розмір стрілками', async () => {
    show();
    const dialog = await screen.findByRole('dialog');
    const handle = within(dialog).getByRole('button', { name: 'Resize window' });

    fireEvent.keyDown(handle, { key: 'ArrowDown' });
    expect(dialog.getAttribute('data-rm-size')).toBe('custom');
    expect(globalThis.localStorage.getItem(storageKey('methodology.versionDiff'))).toContain('"mode":"custom"');
  });
});

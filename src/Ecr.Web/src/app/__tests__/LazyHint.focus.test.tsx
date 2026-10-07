import { act, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * Чанк `Hint` доїжджає ПІСЛЯ того, як людина сфокусувала тригер: заміна
 * `fallback` → `Hint` створює тригер заново, і фокус падав на `body`
 * (e2e `navbarCollapse`, WCAG 2.4.3). `LazyHint` тепер повертає його сам.
 *
 * Модуль `LazyHint` тримає кеш завантаженого чанка, тож кожен тест бере його
 * свіжим (`vi.resetModules`), а завантаження чанка вручну керує `release`.
 */
let release: () => void = () => undefined;

beforeEach(() => {
  vi.resetModules();
  vi.doUnmock('@/shared/ui/Hint');
});

async function mountWithSlowChunk(): Promise<void> {
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  vi.doMock('@/shared/ui/Hint', async () => {
    await gate;
    return vi.importActual<typeof import('@/shared/ui/Hint')>('@/shared/ui/Hint');
  });
  const { LazyHint } = await import('../LazyHint');
  render(
    <MantineProvider>
      <LazyHint label="Hint text" position="right">
        <button type="button">Toggle</button>
      </LazyHint>
    </MantineProvider>,
  );
}

describe('LazyHint: фокус переживає доїзд чанка Hint', () => {
  it('тригер у фокусі на момент підміни лишається в фокусі (новий вузол)', async () => {
    await mountWithSlowChunk();
    const before = screen.getByRole('button', { name: 'Toggle' });
    before.focus();
    expect(document.activeElement).toBe(before);

    await act(async () => {
      release();
    });
    await waitFor(() => {
      expect(document.querySelector('[data-hint-text]')).not.toBeNull();
    });

    const after = screen.getByRole('button', { name: 'Toggle' });
    expect(after).not.toBe(before);
    expect(document.activeElement).toBe(after);
  });

  it('тригер не у фокусі — фокус не забирається', async () => {
    await mountWithSlowChunk();
    const outside = document.createElement('input');
    document.body.appendChild(outside);
    outside.focus();

    await act(async () => {
      release();
    });
    await waitFor(() => {
      expect(document.querySelector('[data-hint-text]')).not.toBeNull();
    });

    expect(document.activeElement).toBe(outside);
    outside.remove();
  });
});

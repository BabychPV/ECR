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
  // Справжній модуль вантажимо ДО монтування: далі `gate` — єдине, що тримає чанк.
  const actual = await vi.importActual<typeof import('@/shared/ui/Hint')>('@/shared/ui/Hint');
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  vi.doMock('@/shared/ui/Hint', async () => {
    await gate;
    return actual;
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

  it('фокус пішов з тригера між доїздом чанка і комітом заміни — назад не віддається (AN-58)', async () => {
    await mountWithSlowChunk();
    const before = screen.getByRole('button', { name: 'Toggle' });
    const outside = document.createElement('input');
    document.body.appendChild(outside);
    before.focus();

    // Усередині act оновлення стану чекає виходу зі сфери: continuation чанка
    // (де фіксується «тригер у фокусі») встигає, а коміт заміни — ще ні.
    await act(async () => {
      release();
      // Без реальних таймерів: `Hint` уже прогрітий у `mountWithSlowChunk`, тож після
      // `release` чанк доїжджає самими мікротасками. Чекаємо завершення динамічних
      // імпортів і зливаємо чергу мікротасок — continuation `loadHint().then` (де
      // фіксується «тригер у фокусі») гарантовано відпрацював, а коміт заміни стану
      // чекає виходу з `act`.
      await vi.dynamicImportSettled();
      for (let i = 0; i < 20; i += 1) {
        await Promise.resolve();
      }
      outside.focus();
    });
    await waitFor(() => {
      expect(document.querySelector('[data-hint-text]')).not.toBeNull();
    });

    expect(document.activeElement).toBe(outside);
    outside.remove();
  });
});

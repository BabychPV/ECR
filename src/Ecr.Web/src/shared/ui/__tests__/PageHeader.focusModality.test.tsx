import { describe, it, expect, vi, afterEach } from 'vitest';
import { fireEvent, render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { PageHeader } from '@/shared/ui/PageHeader';

/**
 * `X-37`: заголовок сторінки отримує фокус програмно, і Chromium малював йому
 * рамку `:focus-visible` після переходу мишею чи відкриття адреси.
 *
 * ⚠ jsdom не рахує `:focus-visible`, тож доводиться саме рішення компонента —
 * з яким `focusVisible` він просить фокус. Поведінку браузера на цьому
 * параметрі перевірено живцем (Chromium, `/admin/*`, 1280 і 1920).
 */

afterEach(() => {
  vi.restoreAllMocks();
});

function mountAndCaptureFocus(): FocusOptions | undefined {
  const focus = vi.spyOn(HTMLElement.prototype, 'focus');

  render(
    <MantineProvider>
      <MemoryRouter>
        <PageHeader title="Periods" />
      </MemoryRouter>
    </MantineProvider>,
  );

  const call = focus.mock.calls.at(-1);
  expect(call, 'заголовок має отримати фокус при монтуванні').toBeDefined();

  return call?.[0];
}

describe('PageHeader: видимість програмного фокуса за модальністю', () => {
  it('після миші — фокус переїжджає, але без рамки', () => {
    fireEvent.pointerDown(document.body);

    // ⛔ Мутація «`focus()` без параметра» (стара поведінка) дає `undefined` —
    // рішення знову віддане евристиці браузера.
    expect(mountAndCaptureFocus()).toEqual({ focusVisible: false });
  });

  it('після клавіатури — рамка лишається: людина бачить, звідки продовжиться Tab', () => {
    fireEvent.keyDown(document.body, { key: 'Enter' });

    // ⛔ Мутація «завжди `focusVisible: false`» червоніє тут.
    expect(mountAndCaptureFocus()).toEqual({ focusVisible: true });
  });
});

describe('PageHeader: рамка програмного фокуса знімається й там, де браузер ігнорує focusVisible', () => {
  it('після миші — заголовок без рамки, доки не втратить фокус', () => {
    fireEvent.pointerDown(document.body);

    render(
      <MantineProvider>
        <MemoryRouter>
          <PageHeader title="Jobs" />
        </MemoryRouter>
      </MantineProvider>,
    );

    const heading = document.querySelector('h3') as HTMLElement;

    // ⛔ Мутація «без `quietFocus`» лишає outline браузера (живцем — Chromium).
    expect(heading.style.outline).toBe('none');

    fireEvent.blur(heading);
    expect(heading.style.outline).toBe('');
  });

  it('після клавіатури — власний стиль рамки не знімає', () => {
    fireEvent.keyDown(document.body, { key: 'Tab' });

    render(
      <MantineProvider>
        <MemoryRouter>
          <PageHeader title="Jobs" />
        </MemoryRouter>
      </MantineProvider>,
    );

    expect((document.querySelector('h3') as HTMLElement).style.outline).toBe('');
  });
});
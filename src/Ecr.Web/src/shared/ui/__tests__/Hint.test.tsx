import { readFileSync } from 'node:fs';
import path from 'node:path';
import type { JSX } from 'react';
import { describe, expect, it } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Badge, Button, DEFAULT_THEME, MantineProvider, mergeMantineTheme } from '@mantine/core';
import { Hint } from '@/shared/ui/Hint';
import { surfaces, theme } from '@/shared/theme/theme';
import { AA, contrast } from '@/shared/theme/contrast';
import { testTheme } from '@/test/render';

/**
 * `Hint` — підказка замість атрибута `title`.
 *
 * ⚠ Опис перевіряється фільтром `description` у `getByRole` — він рахує
 * accname через `dom-accessibility-api`, ту саму реалізацію, що стоїть за
 * `toHaveAccessibleDescription` у `jest-dom` (його проєкт не підключає, а
 * пряма залежність від `dom-accessibility-api` не типізується під
 * `moduleResolution: bundler`).
 *
 * ⛔ Фокус — ТАБУЛЯЦІЄЮ (`user.tab()`), а не `element.focus()`: скриптовий
 * фокус проходить і на елемент без `tabIndex`, тобто довів би не те.
 */

const Text = 'Numbers are rounded to 15 significant digits';

function show(ui: JSX.Element): void {
  render(<MantineProvider theme={testTheme}>{ui}</MantineProvider>);
}

function tooltip(): HTMLElement | null {
  return screen.queryByRole('tooltip');
}

/** Елементи ролі `role`, чий обчислений опис — рівно `text`. */
function describedAs(role: string, text: string): HTMLElement[] {
  return screen.queryAllByRole(role, { description: text });
}

describe('Hint', () => {
  it('бейдж із focusable: табуляція доходить до нього, опис є без наведення, підказка видна на фокусі', async () => {
    const user = userEvent.setup();
    show(
      <Hint label={Text} focusable>
        <Badge data-testid="badge">legacy</Badge>
      </Hint>,
    );

    const badge = screen.getByTestId('badge');

    // Опис прив'язаний ще ДО будь-якої взаємодії — читач озвучить його при
    // фокусі, не чекаючи, поки відкриється видима підказка.
    expect(describedAs('generic', Text)).toContain(badge);
    expect(tooltip()).toBeNull();

    await user.tab();

    expect(document.activeElement).toBe(badge);
    expect((await screen.findByRole('tooltip')).textContent).toContain(Text);

    // ⛔ `title` не лишився: інакше браузер показав би ДРУГУ, рідну підказку.
    expect(badge.hasAttribute('title')).toBe(false);
  });

  it('Escape закриває підказку, фокус лишається на тригері, опис — теж', async () => {
    const user = userEvent.setup();
    show(
      <Hint label={Text} focusable>
        <Badge data-testid="badge">legacy</Badge>
      </Hint>,
    );

    await user.tab();
    await screen.findByRole('tooltip');

    await user.keyboard('{Escape}');

    await waitFor(() => expect(tooltip()).toBeNull());
    expect(document.activeElement).toBe(screen.getByTestId('badge'));
    expect(describedAs('generic', Text)).toContain(screen.getByTestId('badge'));

    // Наступний фокус відкриває знову: Escape закриває цей показ, а не назавжди.
    await user.tab({ shift: true });
    await user.tab();
    expect((await screen.findByRole('tooltip')).textContent).toContain(Text);
  });

  it('Escape, коли підказку відкрила миша, а фокус деінде, — теж закриває', async () => {
    const user = userEvent.setup();
    show(
      <Hint label={Text} focusable>
        <Badge data-testid="badge">legacy</Badge>
      </Hint>,
    );

    await user.hover(screen.getByTestId('badge'));
    await screen.findByRole('tooltip');
    expect(document.activeElement).toBe(document.body);

    await user.keyboard('{Escape}');

    await waitFor(() => expect(tooltip()).toBeNull());
  });

  it('наведення відкриває, відведення закриває', async () => {
    const user = userEvent.setup();
    show(
      <Hint label={Text} focusable>
        <Badge data-testid="badge">legacy</Badge>
      </Hint>,
    );

    await user.hover(screen.getByTestId('badge'));
    expect((await screen.findByRole('tooltip')).textContent).toContain(Text);

    await user.unhover(screen.getByTestId('badge'));
    await waitFor(() => expect(tooltip()).toBeNull());

    // Звичайне закриття не «відхиляє» підказку: наступне наведення — знову.
    await user.hover(screen.getByTestId('badge'));
    expect((await screen.findByRole('tooltip')).textContent).toContain(Text);
  });

  it('без focusable не додає зайвої зупинки табуляції фокусованому тригеру', () => {
    show(
      <Hint label={Text}>
        <Button data-testid="action">Export</Button>
      </Hint>,
    );

    const action = screen.getByTestId('action');

    expect(action.hasAttribute('tabindex')).toBe(false);
    expect(describedAs('button', Text)).toContain(action);
  });

  it('власний aria-describedby і обробники тригера зберігаються', async () => {
    const user = userEvent.setup();
    let focused = 0;

    show(
      <>
        <span id="own">Own description</span>
        <Hint label={Text}>
          <Button data-testid="action" aria-describedby="own" onFocus={() => (focused += 1)}>
            Export
          </Button>
        </Hint>
      </>,
    );

    const action = screen.getByTestId('action');

    expect(describedAs('button', `Own description ${Text}`)).toContain(action);

    await user.tab();

    expect(focused).toBe(1);
    await screen.findByRole('tooltip');
  });

  it('наведення на саму підказку тримає її відкритою (WCAG 1.4.13)', async () => {
    const user = userEvent.setup();
    show(
      <Hint label={Text} focusable>
        <Badge data-testid="badge">legacy</Badge>
      </Hint>,
    );

    await user.hover(screen.getByTestId('badge'));
    const tip = await screen.findByRole('tooltip');

    await user.hover(tip);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(tooltip()?.textContent).toContain(Text);

    await user.unhover(tip);
    await waitFor(() => expect(tooltip()).toBeNull());
  });

  it('підказка — не діалог: немає aria-haspopup/aria-expanded, фокус лишається на тригері', async () => {
    const user = userEvent.setup();
    show(
      <Hint label={Text} focusable>
        <Badge data-testid="badge">legacy</Badge>
      </Hint>,
    );

    await user.tab();
    await screen.findByRole('tooltip');

    const badge = screen.getByTestId('badge');
    expect(badge.hasAttribute('aria-haspopup')).toBe(false);
    expect(badge.hasAttribute('aria-expanded')).toBe(false);
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(badge);
  });

  /**
   * ⛔ Контраст підказки — на ЗЛИТІЙ темі застосунку, а не на палітрі Mantine
   * напряму: перевизначення `dark` чи `white` у `theme.ts` або тексту в
   * `surfaces` змінило б колір підказки, і тест має це побачити.
   *
   * ⚠ Тло — з CSS самого Mantine (`Popover.css`), а не з пам'яті: оновлення
   * Mantine, що змінить дефолт, зробить перші твердження червоними, замість
   * того щоб тест тихо міряв колір, якого на екрані вже немає. Текст —
   * `--mantine-color-text`, його задає `cssVariables.ts` із `surfaces.*.text`.
   */
  it('текст підказки контрастний в обох темах (AA)', () => {
    const css = readFileSync(
      path.resolve(process.cwd(), 'node_modules/@mantine/core/styles/Popover.css'),
      'utf8',
    );

    expect(css).toMatch(/color-scheme='light'\]\) \.m_38a85659 \{[^}]*background-color: var\(--mantine-color-white\)/);
    expect(css).toMatch(/color-scheme='dark'\]\) \.m_38a85659 \{[^}]*background-color: var\(--mantine-color-dark-6\)/);

    const merged = mergeMantineTheme(DEFAULT_THEME, theme);

    expect(contrast(merged.white, surfaces.light.text)).toBeGreaterThanOrEqual(AA.text);
    expect(contrast(merged.colors.dark[6], surfaces.dark.text)).toBeGreaterThanOrEqual(AA.text);
  });
});

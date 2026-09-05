import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, it, expect } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import type { JSX } from 'react';
import { theme } from '../theme';
import { router } from '@/app/router';
import { PageHeader } from '@/shared/ui/PageHeader';
import { RouteAnnouncer } from '@/shared/ui/RouteAnnouncer';
import { MantineProvider } from '@mantine/core';

const cssWithComments = readFileSync(
  path.resolve(process.cwd(), 'src/shared/theme/motion.css'),
  'utf8',
);

// ⚠ Коментарі прибираються ПЕРЕД перевіркою. Без цього заборона `outline:
// none` спрацьовувала на власному коментарі, який цю заборону й пояснює, —
// тобто тест падав саме тому, що правило задокументоване.
const css = cssWithComments.replace(/\/\*[\s\S]*?\*\//g, '');

const routerSource = readFileSync(path.resolve(process.cwd(), 'src/app/router.tsx'), 'utf8');

/**
 * Рух мінімальний і функціональний (`ФВ-14.27`, `ФВ-14.28`, `D-129`).
 *
 * ⛔ Оператор змінює маршрут двісті разів на день. 300 мс анімації переходу —
 * це п'ять хвилин очікування за робочий тиждень, і ці п'ять хвилин він
 * витрачає на те, щоб дивитися, як з'їжджає екран.
 */
describe('Рух (ФВ-14.27, ФВ-14.28)', () => {
  it('prefers-reduced-motion ВИМИКАЄ рух, а не послаблює', () => {
    const block = css.slice(css.indexOf('prefers-reduced-motion'));
    const body = block.slice(0, block.indexOf('\n}\n'));

    // ⛔ Саме вимикає. Для частини людей рух на екрані спричиняє нудоту; вони
    // вже зробили цю системну налаштування, і тлумачити її як «поменше
    // анімації» означає її проігнорувати.
    expect(body).toContain('animation-duration: 0.01ms !important');
    expect(body).toContain('transition-duration: 0.01ms !important');
    expect(body).toContain('scroll-behavior: auto !important');

    // Правило застосоване до ВСЬОГО, включно з псевдоелементами.
    expect(body).toContain('*::before');
    expect(body).toContain('*::after');
  });

  it('у темі немає тривалості понад 150 мс', () => {
    const durations = JSON.stringify(theme).match(/"?duration"?:\s*(\d+)/g) ?? [];
    const values = durations.map((d) => Number(d.replace(/\D/g, '')));

    // ⛔ Довших значень у темі НЕМАЄ навмисно, щоб їх не було й у
    // компонентах: значення, якого немає в джерелі, не можна взяти звідти.
    for (const value of values) expect(value).toBeLessThanOrEqual(150);

    const other = theme.other as { motionFast: string; motionBase: string };
    expect(Number.parseInt(other.motionBase, 10)).toBeLessThanOrEqual(150);
    expect(Number.parseInt(other.motionFast, 10)).toBeLessThanOrEqual(150);
  });

  it('переходів між сторінками немає в жодному стані', () => {
    // ⛔ Маршрут змінюється МИТТЄВО. Перевіряється джерело роутера, а не
    // поведінка: анімація переходу — це те, що додають одним рядком, і
    // помітити її в тесті поведінки можна лише випадково.
    expect(routerSource).not.toContain('Transition');
    expect(routerSource).not.toContain('motion');
    expect(routerSource).not.toContain('animate');

    // А самі маршрути існують — інакше перевірка вище нічого не значить.
    expect(router.routes.length).toBeGreaterThan(0);
  });

  it('кільце фокуса не ховається: outline: none без заміни немає', () => {
    // `ФВ-14.19`. Користувач клавіатури без кільця не знає, де він.
    expect(css).toContain(':focus-visible');
    expect(css).toContain('outline: 2px solid');
    expect(css).not.toMatch(/outline:\s*none/);
  });
});

/**
 * Перехід на новий маршрут переміщує фокус і оголошується (`ФВ-14.19`).
 *
 * ⚠ Без цього користувач клавіатури після переходу опиняється «ніде»: фокус
 * лишається на пункті меню попереднього екрана, і `Tab` веде його по
 * навігації заново — тобто кожен перехід коштує десятка натискань.
 */
function Screen({ title }: { title: string }): JSX.Element {
  return (
    <MantineProvider>
      <RouteAnnouncer />
      <PageHeader title={title} />
    </MantineProvider>
  );
}

describe('Фокус і оголошення на зміні маршруту (ФВ-14.19)', () => {
  it('заголовок сторінки отримує фокус при відкритті', () => {
    render(<Screen title="Документи" />);

    const heading = screen.getByRole('heading', { name: 'Документи' });

    expect(document.activeElement).toBe(heading);
  });

  it('заголовок приймає фокус програмно, але не стає зупинкою табу', () => {
    render(<Screen title="Документи" />);

    // ⚠ `tabIndex={-1}`: інакше кожен екран додавав би зайве натискання на
    // шляху до першого поля.
    expect(screen.getByRole('heading', { name: 'Документи' }).getAttribute('tabindex')).toBe('-1');
  });

  it('назва екрана оголошується через aria-live', async () => {
    render(<Screen title="Реєстри" />);

    const live = screen.getByRole('status');

    expect(live.getAttribute('aria-live')).toBe('polite');

    // ⛔ Оголошується НАЗВА екрана, а не шлях: читалка, що вимовляє
    // `/admin/template-versions/42`, гірша за мовчання.
    //
    // ⚠ Перевіряється саме область оголошень, а не `findByText`: назва є ще й
    // у заголовку, і пошук за текстом знайшов би ДВА збіги і впав — на
    // правильному коді.
    await waitFor(() => {
      expect(live.textContent).toBe('Реєстри');
    });
  });
});

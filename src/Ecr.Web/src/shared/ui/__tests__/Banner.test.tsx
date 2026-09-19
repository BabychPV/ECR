import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import type { JSX, ReactNode } from 'react';
import { theme } from '@/shared/theme/theme';
import { Banner, ResultBanner } from '@/shared/ui/Banner';

/**
 * `Banner` / `ResultBanner` — директива №15 §2, шар 2:
 * «`role="status"` для success, `role="alert"` для danger».
 *
 * ⛔ Перевіряється САМЕ РОЛЬ, а не колір. Колір для читалки не існує взагалі,
 * а роль визначає, чи буде людину перебито посеред речення. Тест на колір був
 * би тестом на те, що ніхто не чує.
 *
 * ⛔ Чому це не «зайва перевірка бібліотеки»: Mantine `Alert` зашиває
 * `role="alert"` ПІСЛЯ розгортання чужих пропсів, тож `<Alert role="status">`
 * мовчки не працює. Перша ж наївна реалізація дала б danger правильно й
 * success — ні, і різниця не видна ані на екрані, ані в жодному знімку.
 */

function mount(node: ReactNode): JSX.Element {
  return <MantineProvider theme={theme}>{node}</MantineProvider>;
}

afterEach(() => {
  cleanup();
});

describe('Banner — роль живої області відповідає тону', () => {
  it('success — role="status" (ввічливо), а НЕ alert', () => {
    render(mount(<Banner tone="success" title="Saved" text="Next: submit for approval." testId="b" />));

    const banner = screen.getByTestId('b');

    // Позитивне твердження: роль саме `status`. Формулювання «не alert»
    // лишалося б зеленим і тоді, коли смуги немає взагалі (#384).
    expect(banner.getAttribute('role')).toBe('status');
    expect(screen.getByRole('status')).toBe(banner);
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('danger — role="alert" (перебиває), а НЕ status', () => {
    render(mount(<Banner tone="danger" title="Save failed" text="Nothing was written." testId="b" />));

    const banner = screen.getByTestId('b');

    expect(banner.getAttribute('role')).toBe('alert');
    expect(screen.getByRole('alert')).toBe(banner);
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('warning перебиває, info — ні (рішення набору, не цитата директиви)', () => {
    const { unmount } = render(mount(<Banner tone="warning" text="Period closes in 2 days." testId="w" />));
    expect(screen.getByTestId('w').getAttribute('role')).toBe('alert');
    unmount();

    render(mount(<Banner tone="info" text="Waiting for approval." testId="i" />));
    expect(screen.getByTestId('i').getAttribute('role')).toBe('status');
  });

  it('ResultBanner — це success: роль status, і тон не перевизначається', () => {
    render(mount(<ResultBanner title="Version published" text="Next: open the document." testId="r" />));

    const banner = screen.getByTestId('r');

    expect(banner.getAttribute('role')).toBe('status');
    expect(banner.getAttribute('data-tone')).toBe('success');
  });
});

describe('Banner — колір береться з теми, а не з літерала', () => {
  it.each([
    ['danger', 'statusError'],
    ['warning', 'statusWarning'],
    ['success', 'statusSuccess'],
    ['info', 'brand'],
  ] as const)('тон %s малюється кольором теми %s', (tone, token) => {
    render(mount(<Banner tone={tone} text="text" testId="b" />));

    /*
     * ⚠ Перевіряється не сам рядок токена (це була б перевірка власного
     * коду), а те, що Mantine РОЗВ'ЯЗАВ його у змінну теми: `--alert-bg`
     * отримує значення виду `var(--mantine-color-<token>-light)`. Якби в
     * компоненті стояв літерал `#B42318`, тут був би саме він — і тест
     * почервонів би, не чекаючи на лінтер.
     */
    const style = screen.getByTestId('b').getAttribute('style') ?? '';

    expect(style).toContain(`--mantine-color-${token}-`);
    expect(style).not.toMatch(/#[0-9a-fA-F]{3,8}\b/);
  });
});

describe('Banner — елемент без даних не малюється (D15-06)', () => {
  it('без заголовка, тексту й дій смуга не з’являється взагалі', () => {
    const { container } = render(mount(<Banner tone="success" testId="empty" />));

    // Порожня кольорова стрічка вгорі екрана повідомляє рівно нічого, а для
    // читалки ще й «оголошує» порожнечу.
    //
    // ⚠ `container.textContent` тут НЕ годиться: `MantineProvider` вставляє
    // в те саме піддерево `<style>` зі змінними теми, тож текст контейнера
    // ніколи не порожній. Перевіряємо наявність самого вузла смуги.
    expect(screen.queryByTestId('empty')).toBeNull();
    expect(screen.queryByRole('status')).toBeNull();
    expect(container.querySelectorAll('[data-tone]')).toHaveLength(0);
  });

  /*
   * ⚠ Наступні два тести — ПАРА, і поодинці жоден із них не доказ.
   * Твердження «тіла немає» стало б зеленим від самого зникнення смуги
   * (урок #384: «атрибута немає» читається як «атрибут правильний»). Тому
   * поруч стоїть дзеркальне твердження «з текстом тіло Є» — разом вони
   * відрізняють «не намалювали порожнє» від «не намалювали нічого».
   */
  it('є лише заголовок — смуга Є (це дані), але порожнього ТІЛА немає', () => {
    render(mount(<Banner tone="info" title="Read-only period" testId="b" />));

    const banner = screen.getByTestId('b');

    expect(banner.textContent).toBe('Read-only period');
    expect(banner.querySelectorAll('[id$="-body"]')).toHaveLength(0);
  });

  it('дзеркало: щойно текст є — тіло з’являється', () => {
    render(mount(<Banner tone="info" title="Read-only period" text="Ends on 30 Sep." testId="b" />));

    const banner = screen.getByTestId('b');

    expect(banner.querySelectorAll('[id$="-body"]')).toHaveLength(1);
    expect(banner.textContent).toContain('Ends on 30 Sep.');
  });
});

describe('Banner — дії та закривання', () => {
  it('дії стають кнопками і викликають свій обробник', async () => {
    const user = userEvent.setup();
    const onClick = vi.fn();

    render(
      mount(
        <Banner
          tone="success"
          title="Job restarted"
          actions={[{ label: 'Follow in My tasks', onClick }]}
          testId="b"
        />,
      ),
    );

    await user.click(screen.getByRole('button', { name: 'Follow in My tasks' }));

    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it('кнопка закривання має ІМ’Я, а не лише значок', async () => {
    const user = userEvent.setup();
    const onDismiss = vi.fn();

    render(
      mount(<Banner tone="info" text="Filter applied." dismiss={{ label: 'Dismiss', onDismiss }} testId="b" />),
    );

    // ⛔ Саме `getByRole(..., {name})`: кнопка без доступного імені
    // знаходиться, але оголошується як «кнопка» — і єдиний спосіб прибрати
    // смугу стає невидимим для того, хто не бачить значка.
    await user.click(screen.getByRole('button', { name: 'Dismiss' }));

    expect(onDismiss).toHaveBeenCalledTimes(1);
  });

  it('без `dismiss` кнопки закривання немає взагалі', () => {
    render(mount(<Banner tone="info" text="Filter applied." testId="b" />));

    expect(screen.queryAllByRole('button')).toHaveLength(0);
  });
});

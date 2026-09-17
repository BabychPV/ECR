import { describe, it, expect } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { RouteAnnouncer, announceRoute } from '@/shared/ui/RouteAnnouncer';

/**
 * `announceRoute` дедуплікує оголошення за текстом у модульній змінній
 * `last` — інакше повторний запис того самого рядка в уже наявну
 * `aria-live`-область читалка просто не озвучує.
 *
 * ⛔ Ця пам'ять правдива рівно доти, доки жива сама область. Коли
 * `RouteAnnouncer` знімається, нова копія монтується з ПОРОЖНІМ `message` —
 * і `last`, що пережив свою область, змусив би `announceRoute` мовчки вийти
 * по дедуплікації проти тексту, якого в DOM уже немає. Користувач читалки в
 * такому разі потрапляє на екран без жодного оголошення.
 *
 * ⚠ Де це буває в застосунку: цикл StrictMode «ефект → прибирання → ефект»
 * і будь-яке перемонтування кореня (наприклад, зміна мови з новим `key`).
 * Перші ж, хто на це наштовхнувся, — тести
 * (`ChangePasswordPage.pageHeader.test.tsx`, `RouteGuard.a11yFocus.test.tsx`),
 * які були змушені підлаштовувати під `last` свій порядок і навіть поділ на
 * файли; це наслідок дефекту, а не його причина.
 */
function show(): ReturnType<typeof render> {
  return render(
    <MantineProvider>
      <RouteAnnouncer />
    </MantineProvider>,
  );
}

const Title = 'Зміна пароля';

describe("RouteAnnouncer: пам'ять про вміст живої області", () => {
  it('однаковий заголовок оголошується знову після перемонтування області', () => {
    const first = show();
    act(() => {
      announceRoute(Title);
    });
    expect(screen.getByRole('status').textContent).toBe(Title);

    first.unmount();

    // ⛔ Мутаційний доказ: приберіть `if (listeners.size === 0) last = '';` у
    // `RouteAnnouncer.tsx` — і тут лишиться порожній рядок, бо `last` усе ще
    // дорівнює `Title`, хоча область із цим текстом уже знята.
    show();
    act(() => {
      announceRoute(Title);
    });
    expect(screen.getByRole('status').textContent).toBe(Title);
  });

  it('область оголошень — polite і атомарна, і живе постійно', () => {
    show();

    const live = screen.getByRole('status');
    expect(live.getAttribute('aria-live')).toBe('polite');
    expect(live.getAttribute('aria-atomic')).toBe('true');

    // ⚠ Порожня область присутня ДО першого оголошення: читалка озвучує
    // зміну всередині вже наявного регіону, а не появу нового вузла.
    expect(live.textContent).toBe('');
  });
});

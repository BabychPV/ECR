import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { RenderErrorCode, RenderErrorScreen, StaleChunkErrorCode } from '@/app/RenderErrorScreen';
import { isStaleVersion, NewVersionBanner, resetStaleVersion } from '@/app/staleVersion';
import { theme } from '@/shared/theme/theme';

/**
 * `DAT-08` (клієнтська половина): застарілий чанк після оновлення.
 *
 * ⛔ Перевіряється не «функція існує», а той самий шлях, яким це відбувається
 * у продукті: рантайм Vite кидає `vite:preloadError` на `window`, і застосунок
 * має ПОЯСНИТИ це користувачеві замість білого екрана. Подія створюється
 * рівно так, як її створює `__vitePreload` — `new Event(..., { cancelable:
 * true })` на `window`.
 *
 * Мутаційна перевірка (вручну, RED → GREEN): у `staleVersion.tsx` прибрано
 * `window.addEventListener('vite:preloadError', …)` з `watchPreloadErrors` —
 * падають обидва тести нижче (банера немає, `isStaleVersion()` лишається
 * `false`, екран помилки показує загальний код замість `ECR-WEB-CHUNK-STALE`).
 * З обробником — зелено.
 */
function firePreloadError(): void {
  act(() => {
    window.dispatchEvent(new Event('vite:preloadError', { cancelable: true }));
  });
}

function renderBanner(): ReturnType<typeof render> {
  return render(
    <MantineProvider theme={theme}>
      <div id="root">
        <NewVersionBanner />
      </div>
    </MantineProvider>,
  );
}

describe('DAT-08 — банер «встановлено нову версію»', () => {
  beforeEach(() => {
    resetStaleVersion();
  });

  afterEach(() => {
    resetStaleVersion();
    vi.restoreAllMocks();
  });

  it('до події банера немає — він не шумить на кожному відкритті', () => {
    const { container } = renderBanner();

    expect(container.querySelector('[data-testid="new-version-banner"]')).toBeNull();
    expect(isStaleVersion()).toBe(false);
  });

  it('подія vite:preloadError дає банер із дією, а не порожній #root', () => {
    const { container } = renderBanner();

    firePreloadError();

    const root = container.querySelector('#root');
    expect(root).not.toBeNull();

    // ⛔ Головне твердження: корінь застосунку НЕ порожній. Саме порожній
    // `#root` і є тим білим екраном, який `DAT-08` описує як
    // «невідрізнимий від дефекту продукту».
    expect(root?.innerHTML).not.toBe('');

    const banner = screen.getByTestId('new-version-banner');
    expect(banner.getAttribute('role')).toBe('alert');
    expect(banner.textContent).toContain('new version');

    // Тупикових екранів не буває (`ФВ-14.24`): дія тут же.
    expect(screen.getByRole('button', { name: /reload page/i })).toBeTruthy();
  });

  it('повторні події не множать банер', () => {
    renderBanner();

    firePreloadError();
    firePreloadError();
    firePreloadError();

    expect(screen.getAllByTestId('new-version-banner').length).toBe(1);
  });

  it('після розмонтування слухач знято — подія більше нікого не чіпає', () => {
    const { unmount } = renderBanner();
    unmount();
    resetStaleVersion();

    window.dispatchEvent(new Event('vite:preloadError', { cancelable: true }));

    expect(isStaleVersion()).toBe(false);
  });
});

/**
 * Друга половина того ж дефекту: відхилений `import()` доходить до межі
 * маршруту як звичайна помилка рендера (`D14-11`), і екран має назвати
 * ПРАВДИВУ причину — оновлення, а не «внутрішню помилку».
 */
describe('DAT-08 — екран помилки називає справжню причину', () => {
  beforeEach(() => {
    resetStaleVersion();
  });

  afterEach(() => {
    resetStaleVersion();
  });

  it('після vite:preloadError екран показує код застарілого чанка, а не загальний', () => {
    const failedImport = new Error(
      'Failed to fetch dynamically imported module: /assets/DocumentsPage-a1b2c3.js',
    );

    // ⚠ Дерево те саме, що в продукті: банер живе в `App.tsx` (він і тримає
    // слухача `vite:preloadError`), екран помилки — всередині маршруту.
    // Рендерити сам екран без банера означало б перевіряти стан, який у
    // застосунку ніхто не вмикає.
    //
    // ⚠ Дерево будується ФУНКЦІЄЮ, а не один раз у змінну: React пропускає
    // перерендер піддерева, якщо йому передали ТОЙ САМИЙ елемент за
    // посиланням, і `rerender(tree)` зі спільною змінною нічого б не
    // перемалював — тест був би хибно червоним.
    const tree = (): JSX.Element => (
      <MantineProvider theme={theme}>
        <NewVersionBanner />
        <RenderErrorScreen error={failedImport} />
      </MantineProvider>
    );

    const { rerender } = render(tree());

    // До події — загальний код помилки рендера.
    expect(screen.getByTestId('render-error-screen').textContent).toContain(RenderErrorCode);

    firePreloadError();
    rerender(tree());

    const alert = screen.getByTestId('render-error-screen');
    expect(alert.textContent).toContain(StaleChunkErrorCode);
    expect(alert.textContent).not.toContain(RenderErrorCode);
    expect(alert.textContent).toContain('Reload the page');
  });
});

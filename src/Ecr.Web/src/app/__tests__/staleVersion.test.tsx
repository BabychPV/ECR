import type { JSX } from 'react';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { RenderErrorCode, RenderErrorScreen, StaleChunkErrorCode } from '@/app/RenderErrorScreen';
import { isStaleVersion, NewVersionBanner, resetStaleVersion } from '@/app/staleVersion';
import { theme } from '@/shared/theme/theme';
import { cancelAutosave, registerSliceSaver } from '@/features/grid/autosave';
import {
  discardPendingRows,
  hasPending,
  openDocument,
  putPendingEdit,
  resetPending,
} from '@/features/grid/pendingStore';
import type { PendingEdit } from '@/features/grid/useCellPatch';

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
 * `DIRECTIVE-16` §1, «DAT-08 залишок», частина (1): застарілий чанк НЕ губить
 * незбережене.
 *
 * ⛔ Доказ — не «`flushUnsaved` викликано», а те, що ПРАВКУ віддано
 * зберігачеві зрізу (у продукті це `PATCH` сітки чи безхазяйного зрізу):
 * зберігач тут той самий, яким реєстр `grid` (`features/grid/autosave.ts`)
 * користується в продукті, — підмінено лише мережу.
 *
 * Мутаційна перевірка (вручну, RED → GREEN): у `watchPreloadErrors` прибрано
 * виклик `flushUnsaved(...)` разом із `.then` — перший тест червоний
 * (`expected [] to have a length of 1`: зберігач не отримав жодної правки),
 * другий теж (`expected 'saving' to be 'failed'`). Друга мутація — виклик
 * замінено на обіцянку без збереження: червоний перший тест. З викликом —
 * зелено.
 */
describe('DAT-08 — незбережене зберігається до пропозиції перезавантажитись', () => {
  const Document = 7;
  const Table = 700;
  const Period = 202609;

  /** Таймаут відстоювання в тестах: три секунди продукту — це три секунди набору. */
  const SettleMs = 50;

  const unregister: (() => void)[] = [];

  function edit(rowKey: string, columnCode: string, value: unknown): PendingEdit {
    return { rowKey, columnCode, value, isEmpty: false, baseVersion: 'v1' };
  }

  function renderWithSettle(): void {
    render(
      <MantineProvider theme={theme}>
        <NewVersionBanner settleTimeoutMs={SettleMs} />
      </MantineProvider>,
    );
  }

  beforeEach(() => {
    resetStaleVersion();
  });

  afterEach(() => {
    cleanup();
    for (const off of unregister.splice(0)) off();
    cancelAutosave();
    resetPending();
    resetStaleVersion();
  });

  it('незбережена правка + vite:preloadError → правку надіслано, і лише потім кнопка Reload', async () => {
    const sent: PendingEdit[][] = [];

    openDocument(Document);
    putPendingEdit(Table, Period, edit('R1', 'C1', 12.4));

    // Зберігач, який ВДАЛО зберіг: підтверджені рядки зникають зі сховища.
    unregister.push(
      registerSliceSaver(Table, Period, (edits) => {
        sent.push([...edits]);
        discardPendingRows(
          Table,
          Period,
          edits.map((item) => item.rowKey),
        );
      }),
    );

    renderWithSettle();
    firePreloadError();

    // ⛔ Головне: правку віддано на збереження — синхронно в обробнику події.
    expect(sent).toHaveLength(1);
    expect(sent[0]?.map((item) => [item.rowKey, item.columnCode, item.value])).toEqual([
      ['R1', 'C1', 12.4],
    ]);

    // Доки результату немає — «зберігаємо», і кнопки, що обірвала б запит, немає.
    expect(screen.getByTestId('new-version-save-state').getAttribute('data-state')).toBe('saving');
    expect(screen.queryByRole('button', { name: /reload page/i })).toBeNull();

    await waitFor(() => {
      expect(screen.getByTestId('new-version-save-state').getAttribute('data-state')).toBe('saved');
    });

    expect(hasPending()).toBe(false);
    expect(screen.getByRole('button', { name: /reload page/i })).toBeTruthy();
  });

  it('збереження не вдалося → банер прямо каже «не збережено», кнопка є', async () => {
    openDocument(Document);
    putPendingEdit(Table, Period, edit('R1', 'C1', 1));

    // Сервер відмовив: жоден рядок не підтверджено — правка лишається.
    unregister.push(registerSliceSaver(Table, Period, () => undefined));

    renderWithSettle();
    firePreloadError();

    await waitFor(() => {
      expect(screen.getByTestId('new-version-save-state').getAttribute('data-state')).toBe(
        'failed',
      );
    });

    // ⚠ Правка не зникла: користувач, що не натисне Reload, її не втратив.
    expect(hasPending()).toBe(true);
    expect(screen.getByTestId('new-version-save-state').textContent).toContain(
      'unsaved.staleFailed',
    );
    expect(screen.getByRole('button', { name: /reload page/i })).toBeTruthy();
  });

  it('незбереженого немає → про збереження банер мовчить, кнопка одразу', () => {
    renderWithSettle();
    firePreloadError();

    expect(screen.queryByTestId('new-version-save-state')).toBeNull();
    expect(screen.getByRole('button', { name: /reload page/i })).toBeTruthy();
  });

  it('тексти стану збереження є в MERGE каталогу', () => {
    const seed = readFileSync(
      path.resolve(process.cwd(), '../../src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql'),
      'utf8',
    );
    const start = seed.indexOf('MERGE sys_ecr.UiString AS t');
    const end = seed.indexOf(') AS s ([Key], Lang, Val, Scope)', start);
    const merge = seed.slice(start, end);

    expect(start).toBeGreaterThan(-1);
    for (const key of ['unsaved.staleSaving', 'unsaved.staleSaved', 'unsaved.staleFailed']) {
      expect(merge, key).toMatch(new RegExp(`\\(N'${key.replace(/\./gu, '\\.')}',\\s*N'en',`, 'u'));
    }
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

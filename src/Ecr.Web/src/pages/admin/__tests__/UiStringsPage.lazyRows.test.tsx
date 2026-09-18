import { describe, it, expect, vi, afterEach } from 'vitest';
import { act, render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { UiStringsPage, RowsPerChunk } from '../UiStringsPage';
import { testTheme } from '@/test/render';

/**
 * F10 (`docs/build/UI-WALKTHROUGH.md`): `/admin/ui-strings` малював **увесь**
 * каталог однією таблицею — 1340 рядків, висота сторінки 50 730 px, і жодного
 * підсумку, скільки їх узагалі.
 *
 * ⚠ Два незалежні твердження, і кожне падає окремо:
 * 1. рядки домальовуються ПОРЦІЯМИ за прокруткою (`RowsPerChunk`);
 * 2. лічильник каже «намальовано / усього» — тобто відповідає на питання,
 *    якого на екрані не було зовсім.
 *
 * ⚠ Каталог тут — 250 ключів: більше за дві порції, тож видно і перший крок,
 * і межу. `alpha.*` (150) сортується перед `beta.*` (100), і це навмисно —
 * сторінка сортує ключі `localeCompare`, тож порядок у тесті відомий.
 */

const SlowEnvTimeout = 400_000;

const AlphaCount = 150;
const BetaCount = 100;

function pad(value: number): string {
  return String(value).padStart(3, '0');
}

/** Каталог: `alpha.000…alpha.149` і `beta.000…beta.099`. */
const Strings: Record<string, string> = Object.fromEntries([
  ...Array.from({ length: AlphaCount }, (_, i) => [`alpha.${pad(i)}`, `Alpha ${pad(i)}`]),
  ...Array.from({ length: BetaCount }, (_, i) => [`beta.${pad(i)}`, `Beta ${pad(i)}`]),
]);

const TotalKeys = AlphaCount + BetaCount;

interface ObserverProbe {
  /** Повідомляє всім живим спостерігачам, що їхні цілі у видимій області. */
  readonly appear: () => void;
  /** Чи стежить хтось бодай за одним елементом. */
  readonly watching: () => boolean;
}

/**
 * Підміна `IntersectionObserver`, якою керує тест.
 *
 * ⚠ У `src/test/setup.ts` уже є заглушка-порожнеча (у jsdom самого класу
 * немає). Вона нічого не повідомляє НІКОЛИ — тобто саме з нею сторінка й
 * лишається на першій порції. Ця підміна додає єдине, чого там бракує:
 * керований такт «маячок з'явився».
 *
 * ⛔ І одну властивість справжнього спостерігача, без якої тест доводив би
 * менше, ніж здається: подія — це ЗМІНА стану перетину. Про ціль, про яку цей
 * екземпляр уже повідомив, він мовчить, доки вона не вийде з області й не
 * зайде знову. Маячок нікуди не виходить — він лише з'їжджає нижче, — тож
 * другу порцію може дати лише НОВИЙ спостерігач. Заглушка, що повідомляє
 * щоразу, зеленіла б і на компоненті, який спостерігача не перестворює.
 */
function installObserver(): ObserverProbe {
  const live = new Set<{
    targets: Set<Element>;
    reported: Set<Element>;
    notify: IntersectionObserverCallback;
  }>();

  class FakeObserver {
    private readonly targets = new Set<Element>();
    private readonly entry: {
      targets: Set<Element>;
      reported: Set<Element>;
      notify: IntersectionObserverCallback;
    };

    constructor(callback: IntersectionObserverCallback) {
      this.entry = { targets: this.targets, reported: new Set<Element>(), notify: callback };
      live.add(this.entry);
    }

    observe(target: Element): void {
      this.targets.add(target);
    }

    unobserve(target: Element): void {
      this.targets.delete(target);
    }

    disconnect(): void {
      this.targets.clear();
      live.delete(this.entry);
    }

    takeRecords(): IntersectionObserverEntry[] {
      return [];
    }
  }

  vi.stubGlobal('IntersectionObserver', FakeObserver);

  return {
    watching: () => [...live].some((entry) => entry.targets.size > 0),
    appear: () => {
      act(() => {
        for (const entry of [...live]) {
          const hits = [...entry.targets].filter((target) => !entry.reported.has(target));
          if (hits.length === 0) continue;

          for (const target of hits) entry.reported.add(target);

          entry.notify(
            hits.map(
              (target) => ({ target, isIntersecting: true }) as unknown as IntersectionObserverEntry,
            ),
            null as unknown as IntersectionObserver,
          );
        }
      });
    },
  };
}

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) {
        return new Response(JSON.stringify({ languageCode: 'en', revision: 1, strings: Strings }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/api/v1/languages')) {
        return new Response(JSON.stringify([{ code: 'en', nameNative: 'English' }]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/ui-strings']}>
          <UiStringsPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Текст лічильника «намальовано / усього». */
function counter(): string {
  return screen.getByTestId('ui-strings-count').textContent ?? '';
}

async function firstChunk(): Promise<void> {
  await screen.findByText('alpha.000', {}, { timeout: SlowEnvTimeout });
}

/**
 * Поле «Filter by key».
 *
 * ⚠ Не `getByRole('textbox')`: Mantine `Select` вибору мови поруч — теж
 * `textbox`, і пошук за роллю знаходить ДВА елементи. Мітка тут — неперекладений
 * ключ (`⟦uiStrings.filter⟧`), бо каталог інтерфейсу в цьому тесті навмисно не
 * піднімається: той самий запит `/ui-strings/` віддає дані САМОЇ сторінки.
 */
function filterInput(): HTMLElement {
  return screen.getByLabelText(/uiStrings\.filter/);
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('UiStringsPage: каталог малюється порціями, а не цілком', () => {
  it(
    'перший кадр — рівно одна порція, решта каталогу не в DOM',
    async () => {
      installObserver();
      mockFetch();
      show();
      await firstChunk();

      expect(RowsPerChunk).toBe(100);

      // ⛔ Мутаційний доказ: поверни `visible.map` на `keys.map` — цей рядок
      // почервоніє, бо в DOM опиняться всі 250 ключів.
      expect(screen.getByText(`alpha.${pad(RowsPerChunk - 1)}`)).toBeTruthy();
      expect(screen.queryByText(`alpha.${pad(RowsPerChunk)}`)).toBeNull();
      expect(screen.queryByText('beta.000')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'лічильник називає і намальоване, і повний розмір каталогу',
    async () => {
      installObserver();
      mockFetch();
      show();
      await firstChunk();

      // ⛔ Мутаційний доказ: прибери лічильник (або віддай у ньому саме
      // `keys.length`) — рядок почервоніє. Саме цього числа на екрані й не
      // було: «сторінок і підсумку немає» (F10).
      expect(counter()).toBe(`${String(RowsPerChunk)} / ${String(TotalKeys)}`);
    },
    SlowEnvTimeout,
  );

  it(
    'маячок з’явився — домальовується наступна порція, і так до кінця',
    async () => {
      const observer = installObserver();
      mockFetch();
      show();
      await firstChunk();

      observer.appear();

      await waitFor(() =>
        expect(counter()).toBe(`${String(RowsPerChunk * 2)} / ${String(TotalKeys)}`),
      );
      expect(screen.getByText(`alpha.${pad(AlphaCount - 1)}`)).toBeTruthy();
      expect(screen.queryByText(`beta.${pad(BetaCount - 1)}`)).toBeNull();

      // ⚠ Другий такт іде по НОВОМУ спостерігачеві: попередній уже
      // повідомив про цей самий маячок і мовчав би далі. Якщо компонент
      // перестане перестворювати спостерігача, список застрягне на 200.
      observer.appear();

      await waitFor(() => expect(counter()).toBe(`${String(TotalKeys)} / ${String(TotalKeys)}`));
      expect(screen.getByText(`beta.${pad(BetaCount - 1)}`)).toBeTruthy();
    },
    SlowEnvTimeout,
  );

  it(
    'каталог домальовано — маячка більше немає',
    async () => {
      const observer = installObserver();
      mockFetch();
      show();
      await firstChunk();

      observer.appear();
      await waitFor(() =>
        expect(counter()).toBe(`${String(RowsPerChunk * 2)} / ${String(TotalKeys)}`),
      );
      observer.appear();
      await waitFor(() => expect(counter()).toBe(`${String(TotalKeys)} / ${String(TotalKeys)}`));

      expect(screen.queryByTestId('ui-strings-sentinel')).toBeNull();
      expect(observer.watching()).toBe(false);
    },
    SlowEnvTimeout,
  );

  it(
    'фільтр після прокрутки скидає порцію — лічильник не бреше «200 з 150»',
    async () => {
      const observer = installObserver();
      mockFetch();
      show();
      await firstChunk();

      observer.appear();
      await waitFor(() =>
        expect(counter()).toBe(`${String(RowsPerChunk * 2)} / ${String(TotalKeys)}`),
      );

      // ⚠ Фільтр лишає 150 збігів — БІЛЬШЕ за порцію, але МЕНШЕ за
      // догорнуте. Саме на цьому проміжку видно різницю: без скидання
      // `shown` лічильник показав би «150 / 150» і маячка не було б, тобто
      // сторінка мовчки намалювала б увесь відфільтрований каталог.
      fireEvent.change(filterInput(), { target: { value: 'alpha' } });

      // ⛔ Мутаційний доказ: прибери `useEffect(() => setShown(RowsPerChunk),
      // [filter, lang])` — цей рядок почервоніє («150 / 150»).
      await waitFor(() =>
        expect(counter()).toBe(`${String(RowsPerChunk)} / ${String(AlphaCount)}`),
      );
      expect(screen.getByTestId('ui-strings-sentinel')).toBeTruthy();
    },
    SlowEnvTimeout,
  );

  it(
    'збігів менше за порцію — каталог показано цілком і без маячка',
    async () => {
      installObserver();
      mockFetch();
      show();
      await firstChunk();

      fireEvent.change(filterInput(), { target: { value: 'beta.00' } });

      await waitFor(() => expect(counter()).toBe('10 / 10'));
      expect(screen.queryByTestId('ui-strings-sentinel')).toBeNull();
    },
    SlowEnvTimeout,
  );
});

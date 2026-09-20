import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { DeliveriesPanel } from '@/features/notifications/DeliveriesPanel';
import type { NotificationDelivery, NotificationDeliveryPage } from '@/features/notifications/api';
import { renderWithQuery } from '@/test/render';

/**
 * Журнал доставок: відмова запиту ≠ «сповіщень не було» (`L10`).
 *
 * ⛔ Найдорожчий дефект цього блоку — не поломка, а ТИХА НЕПРАВДА. Журнал
 * доставок є твердженням про минуле: «ось усе, що система намагалася
 * надіслати». Таблиця без рядків під таким заголовком читається однозначно —
 * «сповіщень не було», — і якщо насправді `GET …/deliveries` ВІДМОВИВ,
 * застосунок сказав неправду, яка виглядає точнісінько як правда. Оператор
 * після цього не шукає, чому не прийшов лист: він уже «знає», що листа не
 * було.
 *
 * П'ять випадків, і другий з них обов'язковий:
 *
 * - **А** — запит відмовив: видно причину з кодом, таблиці НЕМАЄ ЗОВСІМ;
 * - **Б** (дзеркало) — рядки приїхали: таблиця на місці, банера немає. Без
 *   цього випадку «полагодити» А можна було б компонентом, який не малює
 *   таблиці ніколи;
 * - **В** — канал уже видалено (`channelName === null`): на екрані
 *   ідентифікатор саме в `<code>`, а не текстом. Перевіряється `tagName`, а
 *   не наявність тексту: число `4217` видно на екрані і до фіксу, тож
 *   твердження про текст було б зеленим на зламаному коді;
 * - **Г** — журнал СПРАВДІ порожній (успішна відповідь): пояснення, а не
 *   «таблиця без рядків». Це друга половина А: розрізнити «не знаю» і
 *   «нічого не було» можна лише маючи обидва;
 * - **Д** — «показати ще» дочитує наступну сторінку ЗА КУРСОРОМ, і кнопка
 *   зникає, коли курсора більше немає.
 */

/** Стеля тесту: блок у jsdom піднімається за секунди. */
const SlowEnvTimeout = 30_000;

/**
 * Стеля очікування самого твердження — навмисно нижча за стелю тесту: інакше
 * `waitFor`/`findBy*` не встигають скласти власну відмову, і причина падіння
 * зникає за «Test timed out».
 *
 * ⚠ Це не теорія: на мутації «порожній журнал малює таблицю без рядків» із
 * рівними стелями падіння читалося рівно як «Test timed out in 30000ms» —
 * тобто діагностика, заради якої тест і писався, губилася.
 */
const AssertTimeout = 10_000;

/** Позначений ключ: каталог у тестах не вантажиться, тож `t()` віддає саме це. */
function key(name: string): string {
  return `⟦${name}⟧`;
}

/**
 * Відмова сервера.
 *
 * ⚠ `messageKey` ОБОВ'ЯЗКОВИЙ: без нього `problemText` ховає `detail`
 * (показувати сире серверне речення чужою мовою заборонено), і твердження про
 * текст причини було б зеленим на будь-якому коді — банер просто не мав би що
 * показати. Прив'язуємось усе одно й до КОДУ з кореляцією: вони показуються
 * завжди й від каталогу не залежать.
 */
const Refusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'журнал доставок прочитати не вдалося',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-deliveries-1',
  messageKey: 'err.ECR-SYS-0500.unexpected',
};

function delivery(overrides: Partial<NotificationDelivery>): NotificationDelivery {
  return {
    id: overrides.id ?? 1,
    at: overrides.at ?? '2026-09-20T08:15:42.1234567Z',
    channelId: overrides.channelId ?? 7,
    channelName: overrides.channelName === undefined ? 'Пошта бухгалтерії' : overrides.channelName,
    eventKind: overrides.eventKind ?? 'JobFailed',
    eventKey: overrides.eventKey ?? 'job:42',
    status: overrides.status ?? 'Failed',
    error: overrides.error === undefined ? 'SMTP 550 mailbox unavailable' : overrides.error,
  };
}

function page(items: NotificationDelivery[], nextCursor: string | null): NotificationDeliveryPage {
  return { items, nextCursor, totalCount: items.length };
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json',
    },
  });
}

/** Адреси, якими блок насправді сходив, — у порядку викликів. */
let requested: string[] = [];

/**
 * Сервер, який або відмовляє, або віддає сторінки ПО ЧЕРЗІ.
 *
 * ⚠ Заглушається саме `fetch`, а не модуль `api.ts`: так відмова проходить
 * увесь справжній шлях (`apiFetch` → `EcrApiError` → `problemText` →
 * `ErrorAlert`), і код із кореляцією в банері доводять, а не інсценують.
 */
function mockApi(answers: NotificationDeliveryPage[] | 'refuse'): void {
  requested = [];
  let call = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = String(input);
      requested.push(url);

      if (!url.includes('/api/v1/notifications/deliveries')) {
        throw new Error(`Немає мока для ${url}`);
      }

      if (answers === 'refuse') {
        return Promise.resolve(json(Refusal, 500));
      }

      // ⚠ Остання відповідь повторюється: зайвий запит має бути ВИДНИЙ у
      // `requested`, а не мовчки впасти на відсутньому елементі масиву.
      const answer = answers[Math.min(call, answers.length - 1)];
      call += 1;

      return Promise.resolve(json(answer));
    }),
  );
}

function table(): HTMLElement | null {
  return screen.queryByRole('table');
}

/**
 * Банер відмови шукається за СТАБІЛЬНИМ КОДОМ, а не за роллю: `role="alert"`
 * у блоці може поставити й інший `Alert`.
 */
function refusalBanner(): HTMLElement | undefined {
  return screen
    .queryAllByRole('alert')
    .find((element) => (element.textContent ?? '').includes('ECR-SYS-0500'));
}

function showMoreButton(): HTMLElement | null {
  return screen.queryByRole('button', { name: key('notifications.showMore') });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DeliveriesPanel: відмова журналу не читається як «сповіщень не було»', () => {
  it(
    'А: запит відмовив — причина з кодом на екрані, таблиці НЕМАЄ',
    async () => {
      mockApi('refuse');
      renderWithQuery(<DeliveriesPanel />);

      const banner = await waitFor(
        () => {
          const found = refusalBanner();
          if (found === undefined) {
            throw new Error('немає банера з кодом відмови ECR-SYS-0500');
          }
          return found;
        },
        { timeout: AssertTimeout },
      );

      expect(banner.textContent ?? '').toContain('cid-deliveries-1');
      expect(banner.textContent ?? '').toContain('журнал доставок прочитати не вдалося');

      // ⛔ Головне твердження: порожньої таблиці під заголовком журналу немає
      // ЗОВСІМ. Саме вона й була б неправдою про минуле.
      expect(table()).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'Б (дзеркало): рядки приїхали — таблиця на місці, банера немає',
    async () => {
      mockApi([page([delivery({ id: 11, channelName: 'Пошта бухгалтерії' })], null)]);
      renderWithQuery(<DeliveriesPanel />);

      // ⚠ Спершу дочекатись ДАНИХ: доти таблиці немає й за правильним кодом
      // (скелет), тож обидва твердження нижче були б зеленими на будь-чому.
      await screen.findByText('Пошта бухгалтерії', {}, { timeout: AssertTimeout });

      expect(table()).not.toBeNull();
      expect(refusalBanner()).toBeUndefined();
    },
    SlowEnvTimeout,
  );

  it(
    'В: канал видалено — на екрані ідентифікатор у <code>, а не текстом',
    async () => {
      mockApi([page([delivery({ id: 12, channelId: 4217, channelName: null })], null)]);
      renderWithQuery(<DeliveriesPanel />);

      const shown = await screen.findByText('4217', {}, { timeout: AssertTimeout });

      // ⛔ Перевіряється САМЕ тег. Число видно на екрані і тоді, коли воно
      // надруковане голим текстом у комірці, — тобто твердження «на екрані є
      // 4217» лишалося б зеленим після зламу.
      expect(shown.tagName).toBe('CODE');
      expect(shown.getAttribute('title')).toBe(key('notifications.channelGone'));
    },
    SlowEnvTimeout,
  );

  it(
    'Г: журнал справді порожній — пояснення, а не таблиця без рядків',
    async () => {
      mockApi([page([], null)]);
      renderWithQuery(<DeliveriesPanel />);

      await screen.findByText(key('notifications.noDeliveries'), {}, { timeout: AssertTimeout });

      expect(screen.queryByText(key('notifications.noDeliveriesHint'))).not.toBeNull();
      expect(table()).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'Д: «показати ще» дочитує сторінку за курсором, далі кнопка зникає',
    async () => {
      mockApi([
        page([delivery({ id: 21, channelName: 'Пошта бухгалтерії' })], 'cursor-21'),
        page([delivery({ id: 22, channelName: 'Teams чергових' })], null),
      ]);
      renderWithQuery(<DeliveriesPanel />);

      await screen.findByText('Пошта бухгалтерії', {}, { timeout: AssertTimeout });

      const more = showMoreButton();
      if (more === null) throw new Error('після першої сторінки немає кнопки «показати ще»');

      fireEvent.click(more);

      // Друга сторінка ДОЧИТАНА: перший рядок лишився на місці.
      await screen.findByText('Teams чергових', {}, { timeout: AssertTimeout });
      expect(screen.queryByText('Пошта бухгалтерії')).not.toBeNull();

      // ⛔ Саме КУРСОР, а не «ще раз те саме»: без нього кнопка перечитувала б
      // першу сторінку, і журнал ніколи не показав би нічого, крім її рядків.
      expect(requested).toHaveLength(2);
      expect(requested[1] ?? '').toContain('cursor=cursor-21');

      await waitFor(
        () => {
          expect(showMoreButton()).toBeNull();
        },
        { timeout: AssertTimeout },
      );
    },
    SlowEnvTimeout,
  );
});

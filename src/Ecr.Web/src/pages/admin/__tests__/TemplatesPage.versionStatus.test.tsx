import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { TemplatesPage } from '@/pages/admin/TemplatesPage';

/**
 * Статус версії шаблону показувався КОДОМ СЕРВЕРА.
 *
 * ⛔ У переліку друкувалося `{version.version} · {version.status} ·
 * r{version.presentationRevision}` — тобто `Published` і `Deprecated`
 * потрапляли на екран англійськими словами з `Ecr.Domain/Enums`. Це той самий
 * дефект, що вже знято з `SourcesPage`, `JobsPage`, `HealthPage`,
 * `PeriodsPage` і `DocumentsPage`: код сервера не є текстом інтерфейсу й не
 * перекладається, а продукт тримає три мови (`D-95`).
 *
 * ⚠ Гірший наслідок був не в мові. Застаріла версія (`Deprecated`) не
 * відрізнялася від чинної нічим, окрім `variant` бейджа, якого ніхто не
 * пояснює, — і саме в переліку, звідки на версію переходять, щоб нею
 * скористатися.
 *
 * ⛔ У цієї сторінки не було ЖОДНОГО тесту — тому дефект і прожив стільки.
 */

const templates = {
  items: [{ id: 7, code: 'AIR', nameL10n: { values: { en: 'Air emissions' } } }],
  nextCursor: null,
  totalCount: 1,
};

const versions = {
  items: [
    { id: 1, version: '1.0', status: 'Deprecated', presentationRevision: 2 },
    { id: 2, version: '2.0', status: 'Published', presentationRevision: 1 },
  ],
  nextCursor: null,
  totalCount: 2,
};

function respond(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return new Response(
          JSON.stringify({
            denies: [],
            grants: {},
            isSimulation: false,
            language: 'en',
            mustChangePassword: false,
            permissions: [],
            simulatedForUserId: null,
            userId: 1,
            userName: 'tester',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      // ⚠ Порядок важливий: адреса версій містить `/api/v1/templates` як
      // префікс, тож вужчу гілку перевіряємо ПЕРШОЮ. Зворотний порядок віддав
      // би перелік шаблонів у відповідь на запит версій — і тест упав би не з
      // тієї причини, яку перевіряє.
      if (/\/api\/v1\/templates\/\d+\/versions/.test(url)) {
        return new Response(JSON.stringify(versions), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/api/v1/templates')) {
        return new Response(JSON.stringify(templates), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify(null), { status: 200 });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <MemoryRouter>
        <QueryClientProvider client={client}>
          <TemplatesPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('TemplatesPage: статус версії — з набору, не кодом сервера', () => {
  it('кожна версія має власну позначку статусу з розпізнаним станом', async () => {
    respond();
    show();

    await screen.findByText('AIR');

    /*
     * ⛔ Головне твердження. `data-status-state` кладе `StatusBadge` — тобто
     * стан пройшов через таблицю набору (`statusTable.version`), а не був
     * надрукований рядком. Мутація «повернути `{version.status}` у текст»
     * лишає цей запит порожнім.
     */
    const marked = [...document.querySelectorAll('[data-status-state]')].map((node) =>
      node.getAttribute('data-status-state'),
    );

    expect(marked).toEqual(['Deprecated', 'Published']);
  });

  it('код сервера НЕ потрапляє на екран як видимий текст', async () => {
    respond();
    show();

    await screen.findByText('AIR');

    /*
     * ⚠ Підпис береться з каталогу за ключем `status.version.<стан>`, а без
     * завантаженого каталогу `t()` чесно повертає позначений ключ
     * `⟦status.version.Deprecated⟧`. Тобто голого слова `Deprecated` на
     * екрані бути не може — і саме це тут і перевіряється.
     *
     * ⛔ Мутаційний доказ: поверніть `{version.status}` у текст чипа — і
     * `queryAllByText('Deprecated')` знайде рівно один вузол замість нуля.
     */
    expect(screen.queryAllByText('Deprecated')).toHaveLength(0);
    expect(screen.queryAllByText('Published')).toHaveLength(0);

    // Дзеркало: підпис усе-таки є, просто він із каталогу.
    expect(document.body.textContent ?? '').toContain('status.version.Deprecated');
  });

  it('перехід на версію оголошується ПОСИЛАННЯМ, а не позначкою', async () => {
    /*
     * ⚠ Було `Badge component={Link}` — візуально клікабельне, для читалки
     * ніщо. Курсор-палець бачить лише той, хто дивиться на екран.
     */
    respond();
    show();

    const link = await screen.findByRole('link', { name: /2\.0/ });

    expect(link.getAttribute('href')).toBe('/admin/templates/7/versions/2');
  });
});

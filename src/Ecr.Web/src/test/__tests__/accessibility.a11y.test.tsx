import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe as suite, it, expect, vi, afterEach, beforeEach } from 'vitest';
import { render } from '@testing-library/react';
import type { JSX, ReactNode } from 'react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { describe as report, findViolations } from '@/test/a11y';
import { describeHits, findKeyLikeText } from '@/test/keyLikeText';
import { loadCatalog } from '@/shared/i18n';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { LoginPage } from '@/pages/LoginPage';
import { ChangePasswordPage } from '@/pages/ChangePasswordPage';
import { HealthPage } from '@/pages/admin/HealthPage';
import { JobsPage } from '@/pages/admin/JobsPage';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { RegistriesPage } from '@/pages/admin/RegistriesPage';
import { SecurityPage } from '@/pages/admin/SecurityPage';
import { SourcesPage } from '@/pages/admin/SourcesPage';
import { TemplatesPage } from '@/pages/admin/TemplatesPage';
import { MethodologiesPage } from '@/pages/admin/MethodologiesPage';
import { KitchenSinkPage } from '@/pages/KitchenSinkPage';

/**
 * WCAG 2.1 AA на кожному маршруті (`ФВ-14.16`, `D-127`).
 *
 * ⛔ Нуль порушень рівня `critical` і `serious` — це поріг, що блокує CI.
 *
 * ⚠ Сторінки рендеряться **в порожньому стані**: сервер замокано порожньою
 * відповіддю. Це навмисно: порожній стан — той, у якому найлегше забути
 * підпис, роль або зв'язок поля з помилкою, бо на ньому нічого не видно.
 */
function Shell({ children }: { children: ReactNode }): JSX.Element {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return (
    <MantineProvider>
      <QueryClientProvider client={client}>
        <MemoryRouter>{children}</MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>
  );
}

const Pages: [string, () => JSX.Element][] = [
  ['/login', LoginPage],
  ['/change-password', ChangePasswordPage],
  ['/', DocumentsPage],
  ['/admin/templates', TemplatesPage],
  ['/admin/registries', RegistriesPage],
  ['/admin/methodologies', MethodologiesPage],
  ['/admin/security', SecurityPage],
  ['/admin/periods', PeriodsPage],
  ['/admin/sources', SourcesPage],
  ['/admin/jobs', JobsPage],
  ['/admin/health', HealthPage],
  ['/_kitchen-sink', KitchenSinkPage],
];

/**
 * Порожня відповідь ПОТРІБНОЇ форми для кожного маршруту.
 *
 * ⛔ Одна відповідь на всі адреси не годиться: сторінка, яка чекає масив,
 * падає на об'єкті — і тест перевіряв би не доступність, а власну заглушку.
 * Це та сама помилка, що й `A7-04`, тільки в зворотний бік.
 */
/**
 * СПРАВЖНІЙ каталог рядків із `09-seed.sql`.
 *
 * ⛔ Не порожній словник. Порожній каталог означав би, що сторож ключів
 * перевіряє власну заглушку: він падав би завжди і його б вимкнули. А з
 * реальним seed він перевіряє те саме, що побачить користувач, — і ключ,
 * забутий у seed, стає падінням збірки (той самий клас, що й `A7-12`).
 */
function seededStrings(): Record<string, string> {
  const seed = readFileSync(
    path.resolve(process.cwd(), '../../src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql'),
    'utf8',
  );

  const strings: Record<string, string> = {};
  for (const row of seed.matchAll(/\(N'([^']+)',\s*N'[a-z]{2}',\s*N'([^']*)'/g)) {
    strings[row[1] ?? ''] = row[2] ?? '';
  }

  return strings;
}

const Catalog = seededStrings();

function emptyBodyFor(url: string): unknown {
  if (url.includes('/ui-strings/')) {
    return { languageCode: 'en', revision: 1, strings: Catalog };
  }
  if (url.includes('/health/')) return { status: 'Healthy', totalDurationMs: 1, checks: [] };
  if (url.includes('/periods')) return { projectId: 0, periods: [] };
  if (url.includes('/me')) {
    return { userId: 0, userName: 'test', language: 'en', permissions: [], isSimulation: false };
  }

  // Сторінкові переліки віддають конверт із курсором, решта — масив.
  const paged = ['/documents', '/templates', '/users', '/projects'];

  return paged.some((path) => url.includes(path)) ? { items: [], nextCursor: null } : [];
}

beforeEach(() => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) =>
      Promise.resolve(
        new Response(JSON.stringify(emptyBodyFor(String(input))), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    ),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

suite('Доступність маршрутів', () => {
  it.each(Pages)('ФВ-14.16: %s не має порушень critical і serious', async (_path, Page) => {
    const { container } = render(
      <Shell>
        <Page />
      </Shell>,
    );

    const violations = await findViolations(container);

    expect(violations, report(violations)).toHaveLength(0);
  }, 120_000);
});

/**
 * Сторож проти технічних ключів (`D-138`).
 *
 * ⛔ Ті самі маршрути, що й вище: обхід уже є, і додати до нього перевірку
 * дешевше, ніж завести другий. `A7-33` була видима кожному й прожила до
 * живого запуску саме тому, що зникала від першого дотику до екрана — тут
 * екран не торкається ніхто.
 *
 * ⚠ Сторінки рендеряться в порожньому стані, тобто саме тоді, коли ключі й
 * лізуть назовні: порожній стан складається з написів і більше нічого.
 */
suite('Технічні ключі на екрані', () => {
  it.each(Pages)('ФВ-14.9: %s показує людський текст, а не ключі', async (_path, Page) => {
    // ⚠ Каталог розв'язується ДО рендера: оболонка навмисно не малює тексту,
    // доки він не приїхав (`D-138`), і без цього рядка тест дивився б на
    // стан завантаження, а не на написи.
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    const { container } = render(
      <Shell>
        <Page />
      </Shell>,
    );

    const hits = findKeyLikeText(container);

    expect(hits, describeHits(hits)).toHaveLength(0);
  });
});

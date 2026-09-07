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
import { MappingPreviewPage } from '@/pages/admin/MappingPreviewPage';
import { TemplatesPage } from '@/pages/admin/TemplatesPage';
import { TableRelationsPage } from '@/pages/admin/TableRelationsPage';
import { MethodologiesPage } from '@/pages/admin/MethodologiesPage';
import { MethodologyVersionsPage } from '@/pages/admin/MethodologyVersionsPage';
import { ExpressionsPage } from '@/pages/admin/ExpressionsPage';
import { KitchenSinkPage } from '@/pages/KitchenSinkPage';
import { MyGroupsPage } from '@/pages/MyGroupsPage';

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

/**
 * ⚠ Редактор виразів потрапляє сюди СВОЄЮ сторінкою, а не Monaco: у jsdom той
 * падає в службі тем ще до першого токена. Перевіряється те, що в jsdom
 * існує, — обрамлення сторінки і **видиме повідомлення про незавантажений
 * редактор**. Це не обхід: саме такий вигляд має сторінка, коли чанк на 818 КБ
 * не дійшов через корпоративний канал, і саме тоді доступність важить
 * найбільше.
 */
const Pages: [string, () => JSX.Element][] = [
  ['/login', LoginPage],
  ['/change-password', ChangePasswordPage],
  ['/', DocumentsPage],
  ['/admin/templates', TemplatesPage],
  ['/admin/templates/1/versions/1/relations', TableRelationsPage],
  ['/admin/registries', RegistriesPage],
  ['/admin/methodologies', MethodologiesPage],
  ['/admin/methodologies/1/versions', MethodologyVersionsPage],
  ['/admin/expressions', ExpressionsPage],
  ['/admin/security', SecurityPage],
  ['/admin/periods', PeriodsPage],
  ['/admin/sources', SourcesPage],
  ['/admin/mapping', MappingPreviewPage],
  ['/admin/jobs', JobsPage],
  ['/admin/health', HealthPage],
  ['/my-groups', MyGroupsPage],
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

  // ⛔ Діагностика доступу віддає ОБ'ЄКТ, і порожній масив тут зламав би
  // панель на першому ж `.map`. Це та сама помилка, що й `A7-04`: заглушка,
  // яка не має форми відповіді, перевіряє саму себе.
  if (url.includes('/security/my-groups') || url.endsWith('/groups')) {
    return {
      userId: 0,
      userName: 'test',
      provider: 'Windows',
      principalSid: null,
      groupsFromTicket: true,
      groups: [],
      unmatchedSids: [],
      personalRoleCodes: [],
      effectiveRoleCodes: [],
      expiredRoleCodes: [],
      groupAssignmentsInSystem: [],
    };
  }
  if (url.includes('/periods')) return { projectId: 0, periods: [] };

  // ⛔ Перегляд мапінгу віддає ОБ'ЄКТ із чотирма переліками. Порожній масив
  // тут упав би на першому `.map`, і тест перевіряв би власну заглушку.
  if (url.includes('/mapping/preview')) {
    return {
      sourceEntityId: 0,
      code: 'test',
      displayName: null,
      fromUtc: '2026-09-01T00:00:00Z',
      toUtc: '2026-09-08T00:00:00Z',
      pointsSeen: 0,
      isTruncated: false,
      fields: [],
      rows: [],
      unmappedSourceFields: [],
      uncoveredColumns: [],
    };
  }
  // ⛔ Структура версії — ОБ'ЄКТ із аркушами; порожній масив тут упав би на
  // `structure.sheets`, і редактор зв'язків «не мав би порушень доступності»
  // рівно тому, що не намалювався б.
  if (url.includes('/structure')) {
    return { templateVersionId: 0, presentationRevision: 0, sheets: [] };
  }

  // ⛔ Зв'язки таблиць віддають КОНВЕРТ із `isEditable`: порожній масив тут
  // упав би на `relations.data.relations`, і редактор «не мав би порушень»
  // рівно тому, що не намалювався б.
  if (url.includes('/relations')) return { isEditable: true, relations: [] };
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

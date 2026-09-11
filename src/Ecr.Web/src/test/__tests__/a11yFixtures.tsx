import { readFileSync } from 'node:fs';
import path from 'node:path';
import { vi, beforeEach, afterEach } from 'vitest';
import type { JSX, ReactNode } from 'react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';

/**
 * Спільні прилади для розбитого набору перевірки доступності (`ФВ-14.16`,
 * `D-127`, `Q-270`).
 *
 * ⛔ Один файл `accessibility.a11y.test.tsx` (24 маршрути × 2 схеми) виносив
 * усі перевірки в один послідовний прогін Vitest — Vitest не розпаралелює
 * `it.each` усередині ОДНОГО файлу, лише файли між собою. Замір 2026-09-11
 * (baseline) показав ХХ с на повний послідовний прогін; після розбиття на
 * чотири файли (по шість маршрутів) із `maxWorkers: 2` — ХХ с (див.
 * `vitest.a11y.config.ts` і Q-270 для точних чисел).
 *
 * ⛔ Це не рерайт перевірки: тіла тестів (рендер, `findViolations`,
 * `findKeyLikeText`, пороги) лишаються дослівно тими самими в кожному з
 * чотирьох файлів — сюди винесено лише спільні приладдя (обгортка,
 * заглушка мережі, каталог рядків, профіль прав), щоб чотири копії не
 * розійшлися одна з одною з часом. Перелік маршрутів і власне тіло `it.each`
 * лишаються в кожному файлі — це і є межа «спільне приладдя» / «що саме
 * перевіряється».
 */
export function Shell({
  children,
  colorScheme,
}: {
  children: ReactNode;
  colorScheme: 'light' | 'dark';
}): JSX.Element {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return (
    <MantineProvider theme={theme} forceColorScheme={colorScheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter>{children}</MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>
  );
}

/**
 * Яку(і) схему(и) проганяти (`W4.3`).
 *
 * ⛔ `ECR_A11Y_THEME` — ЄДИНЕ джерело: без нього (локальний прогін)
 * проганяються ОБИДВІ схеми в одному виклику Vitest, з ним (CI-матриця,
 * `.github/workflows/ci.yml`, джоба `a11y`) — рівно ОДНА.
 */
const RequestedTheme = process.env['ECR_A11Y_THEME'];
export const Themes: readonly ('light' | 'dark')[] =
  RequestedTheme === 'light' || RequestedTheme === 'dark' ? [RequestedTheme] : ['light', 'dark'];

/**
 * СПРАВЖНІЙ каталог рядків із `09-seed.sql` (незмінно з попереднього єдиного
 * файлу — див. коментар там-таки в git-історії).
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

export const Catalog = seededStrings();

/** Профіль `/me` для сканування — ПОВНИЙ каталог прав, а не порожній масив. */
export const FullAccessPermissions: readonly string[] = [
  'Template.View',
  'Template.Edit',
  'Template.Publish',
  'Template.Migrate',
  'Registry.View',
  'Registry.EditData',
  'Registry.EditDefinition',
  'Registry.Publish',
  'Document.View',
  'Document.Create',
  'Document.Delete',
  'Document.Import',
  'Document.Export',
  'Document.Reopen',
  'Project.Manage',
  'Period.Configure',
  'Period.Reopen',
  'Calculation.View',
  'Calculation.EditFormula',
  'Calculation.EditConstant',
  'Calculation.EditRule',
  'Calculation.EditScript',
  'Calculation.Publish',
  'Calculation.Recalculate',
  'Report.ViewRegulatory',
  'Report.BuildSnapshot',
  'Report.MarkSubmitted',
  'Report.Export',
  'Report.EditDefinition',
  'Integration.View',
  'Integration.Manage',
  'Integration.EditSchedule',
  'Security.ManageUsers',
  'Security.ManageRoles',
  'Security.ViewAudit',
  'Security.Simulate',
  'System.ViewHealth',
  'System.RunJob',
  'System.ManageLocalization',
];

/** Один непорожній документ для `DocumentPage` (`ФВ-14.16`). */
export const DocumentTableFixture = {
  allowsDynamicRows: false,
  maxDynamicRows: null as number | null,
  sheetCode: 'GEN',
  sheetDefId: 1,
  sheetNameL10n: { values: { en: 'General' } },
  sheetOrdinal: 0,
  tableCode: 'T1',
  tableDefId: 1,
  tableInstanceId: 1,
  tableNameL10n: { values: { en: 'Table 1' } },
  tableOrdinal: 0,
};

/**
 * Порожня відповідь ПОТРІБНОЇ форми для кожного маршруту (незмінно з
 * попереднього єдиного файлу).
 */
export function emptyBodyFor(url: string): unknown {
  if (url.includes('/ui-strings/')) {
    return { languageCode: 'en', revision: 1, strings: Catalog };
  }
  if (url.includes('/health/')) return { status: 'Healthy', totalDurationMs: 1, checks: [] };

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
  if (url.includes('/structure')) {
    return { templateVersionId: 0, presentationRevision: 0, sheets: [], isEditable: true };
  }

  if (url.includes('/relations')) return { isEditable: true, relations: [] };

  if (url.includes('/definition')) {
    return {
      id: 0,
      code: 'test',
      nameL10n: { values: {} },
      isTemporal: false,
      sourceKind: 'Local',
      definitionVersion: 1,
      dataRevision: 0,
      fields: [],
      relations: [],
      rules: [],
      mappings: [],
    };
  }

  if (url.includes('/me')) {
    return {
      userId: 0,
      userName: 'test',
      language: 'en',
      permissions: FullAccessPermissions,
      isSimulation: false,
    };
  }

  if (url.includes('/audit/cells')) return { items: [], nextCursor: null };

  if (url.includes('/calculation-results')) return [];

  if (/\/tables\/[^/?]+/.test(url)) {
    return { cellPermissions: {}, columns: [], periodKey: 0, rows: [], tableInstanceId: 1 };
  }

  if (url.includes('/tables')) return [DocumentTableFixture];

  if (url.includes('/validation')) return { documentId: 1, messages: [], periodKey: 0 };

  if (/\/documents\/[^/?]+\?/.test(url)) {
    return {
      businessKey: 'DOC-0001',
      createdAt: '2026-01-01T00:00:00Z',
      id: 1,
      projectId: 1,
      sheetCount: 1,
      sheetStates: { GEN: 'Draft' },
    };
  }

  const paged = ['/documents', '/templates', '/users', '/projects'];

  return paged.some((entry) => url.includes(entry)) ? { items: [], nextCursor: null } : [];
}

/**
 * Реєструє заглушку `fetch` на весь файл (`beforeEach`/`afterEach`
 * кореневого рівня) — та сама поведінка, що й у попередньому єдиному файлі,
 * лише винесена сюди, щоб чотири розбиті файли викликали ОДИН і той самий
 * код, а не чотири копії, що можуть розійтися.
 */
export function registerA11yFetchMock(): void {
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
}

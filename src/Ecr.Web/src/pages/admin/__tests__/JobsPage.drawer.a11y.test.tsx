import { describe as suite, it, expect, afterEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { describe as report, findViolations } from '@/test/a11y';
import { loadCatalog } from '@/shared/i18n';
import { routes } from '@/app/routes';
import { Catalog, FullAccessPermissions, ScanShell, Themes, createScanClient, settlePage } from '@/test/__tests__/a11yFixtures';
import { JobsPage } from '../JobsPage';

/**
 * Доступність журналу задач З ВІДКРИТОЮ шторкою проваленої задачі (UI-28) в
 * обох темах.
 *
 * ⚠ Шторка — у порталі, поза `container`: сканується `document.body`, інакше
 * «порушень немає» було б правдою про сам перелік, а не про шторку.
 *
 * ⚠ Мутаційний доказ (локально): кнопка назви задачі без тексту
 * (`{jobKindLabel(job.jobCode)}` → `''`) → `button-name` critical, обидві теми
 * червоні.
 */
const failed = {
  jobCode: 'Ecr.Application.Ports.IExcelExportJob',
  jobId: 'IExcelExportJob-0123456789abcdef0123456789abcdef',
  percent: 35,
  state: 'Failed',
  startedAt: '2026-10-06T08:00:00Z',
  updatedAt: '2026-10-06T08:01:00Z',
  createdByDisplayName: 'Olena',
  errorCode: 'ECR-JOB-0409',
  correlationId: '0af7651916cd43dd8448eb211c80319c',
  attempt: 3,
  maxAttempts: 3,
  documentId: 12,
};
const running = { ...failed, jobId: 'IRecalculationJob-1', jobCode: 'Ecr.Application.Ports.IRecalculationJob', state: 'Running', percent: 40, errorCode: null, correlationId: null, attempt: 1 };

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Catalog });
      if (url.endsWith('/api/v1/me')) {
        return json({
          userId: 1,
          userName: 'bootstrap',
          language: 'en',
          permissions: FullAccessPermissions,
          isSimulation: false,
          denies: [],
          grants: {},
          mustChangePassword: false,
          simulatedForUserId: null,
        });
      }
      if (url.endsWith(`/api/v1/jobs/${failed.jobId}`)) {
        return json({ ...failed, error: 'Violation of PRIMARY KEY constraint', message: null, createdAt: '2026-10-06T07:59:00Z' });
      }
      if (url.endsWith('/api/v1/jobs')) return json([running, failed]);

      return new Response('{}', { status: 404, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

suite.each(Themes)('Доступність шторки задачі (%s)', (colorScheme) => {
  it(`ФВ-14.16: /admin/jobs?panel=<failed> не має порушень critical і serious`, async () => {
    mockServer();
    await loadCatalog('en', 'private');
    await loadCatalog('en', 'public');

    const client = createScanClient();
    const route = {
      path: routes.adminJobs.path,
      entry: `/admin/jobs?panel=${failed.jobId}`,
      content: async () => screen.findByText('Violation of PRIMARY KEY constraint', undefined, { timeout: 10_000 }),
    };
    render(
      <ScanShell colorScheme={colorScheme} client={client} route={route}>
        <JobsPage />
      </ScanShell>,
    );

    await settlePage(client, route);
    const violations = await findViolations(document.body);

    expect(violations, report(violations)).toHaveLength(0);
  });
});

import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { JSX } from 'react';
import type { CurrentUserDto } from '@/api/types';
import { RouteGuard } from '@/app/RouteGuard';
import { routes } from '@/app/routes';
import type { CampaignProject, CampaignSummary } from '@/features/campaign/api';
import { CampaignOverview } from '@/features/campaign/CampaignOverview';
import { CampaignOverviewPage } from '@/pages/admin/CampaignOverviewPage';
import { MeQueryKey } from '@/shared/session/useSession';
import { testTheme } from '@/test/render';

/**
 * Огляд звітної кампанії (`BE-22`, екран `/admin/campaign`).
 *
 * ⛔ Три твердження, заради яких цей файл існує:
 *   1. обрізаний стелею перелік НАЗВАНИЙ обрізаним («показано N із M») — мовчки
 *      показати 200 із 300 означає відповісти на «хто затримує кампанію»
 *      неправдою; і дзеркало — повна відповідь банера не має;
 *   2. відмова сервера не виглядає ні як порожня кампанія, ні як старий
 *      перелік із кешу (`L10`: помилка → очікування → дані);
 *   3. без права `Report.ViewCampaign` екрана немає і запит не йде.
 */

const Period = 202601;

const Refusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'зведення кампанії прочитати не вдалося',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-campaign-1',
  messageKey: 'err.ECR-SYS-0500.unexpected',
};

/** Проєкт, що вже дійшов до кінця: усе затверджено, зріз є. */
function done(id: number, code: string): CampaignProject {
  return {
    projectId: id,
    projectCode: code,
    nameL10n: { values: { en: `Project ${code}` } },
    documents: 4,
    draft: 0,
    submitted: 0,
    approved: 4,
    rejected: 0,
    snapshots: 1,
  };
}

/** Проєкт, що затримує кампанію: чернетки, подані, відхилені, зрізу немає. */
function behind(id: number, code: string): CampaignProject {
  return {
    projectId: id,
    projectCode: code,
    nameL10n: { values: { en: `Project ${code}` } },
    documents: 6,
    draft: 2,
    submitted: 1,
    approved: 1,
    rejected: 2,
    snapshots: 0,
  };
}

function summaryOf(projects: CampaignProject[], total: number): CampaignSummary {
  return { periodKey: Period, totalProjects: total, projects };
}

/** Перелік потрібної довжини: кожен другий проєкт затримує кампанію. */
function manyProjects(count: number): CampaignProject[] {
  return Array.from({ length: count }, (_, i) =>
    i % 2 === 0 ? behind(i + 1, `P${String(i + 1)}`) : done(i + 1, `P${String(i + 1)}`),
  );
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

/**
 * Заглушка сервера; повертає лічильник запитів огляду кампанії.
 *
 * ⚠ `/me` відповідає тим самим профілем, що й засіяний у кеш: `useSession`
 * перезапитує його одразу після монтування, і інший об'єкт посеред тесту
 * змінив би права, які тест перевіряє.
 */
function mockServer(
  answer: CampaignSummary | 'refuse',
  profile: CurrentUserDto | null = null,
): { calls: () => number } {
  let calls = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/campaign/summary')) {
        calls += 1;

        return answer === 'refuse' ? json(Refusal, 500) : json(answer);
      }

      if (url.includes('/me')) return json(profile);

      return json(null);
    }),
  );

  return { calls: () => calls };
}

function clientWith(seed?: CampaignSummary): QueryClient {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  if (seed !== undefined) client.setQueryData(['campaign', 'summary', Period], seed);

  return client;
}

function show(ui: JSX.Element, client = clientWith()): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[`/admin/campaign?periodKey=${String(Period)}`]}>{ui}</MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Рядки таблиці відстаючих — за кодом проєкту. */
function laggingCodes(): string[] {
  return [...document.querySelectorAll('tbody tr[data-row-key]')].map(
    (row) => row.querySelector('td')?.textContent ?? '',
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('огляд кампанії: усічення', () => {
  it('сервер віддав 200 із 300 — екран каже «показано 200 із 300»', async () => {
    mockServer(summaryOf(manyProjects(200), 300));
    show(<CampaignOverview periodKey={Period} />);

    const banner = await screen.findByTestId('campaign-truncated');

    expect(banner.textContent).toContain('campaign.truncatedTitle');
    expect(banner.textContent).toContain('200');
    expect(banner.textContent).toContain('300');
  });

  it('дзеркало: сервер віддав усе — банера усічення немає', async () => {
    mockServer(summaryOf([behind(1, 'P1'), done(2, 'P2')], 2));
    show(<CampaignOverview periodKey={Period} />);

    await waitFor(() => expect(laggingCodes()).toEqual(['P1']));

    expect(screen.queryByTestId('campaign-truncated')).toBeNull();
  });
});

describe('огляд кампанії: хто затримує', () => {
  it('у переліку лише ті, хто не дійшов до кінця; код — рядком, без роздільників', async () => {
    const notStarted: CampaignProject = { ...behind(3, 'P-03'), documents: 0, draft: 0, submitted: 0, approved: 0, rejected: 0 };
    const approvedNoSnapshot: CampaignProject = { ...done(4, 'P-04'), snapshots: 0 };

    mockServer(summaryOf([done(1, 'P-01'), behind(2, '10000'), notStarted, approvedNoSnapshot], 4));
    show(<CampaignOverview periodKey={Period} />);

    await waitFor(() => expect(laggingCodes()).toEqual(['10000', 'P-03', 'P-04']));
  });

  it('лічильники станів складено по ВСІХ проєктах, включно з тими, що вже завершили', async () => {
    mockServer(summaryOf([done(1, 'P1'), behind(2, 'P2'), behind(3, 'P3')], 3));
    show(<CampaignOverview periodKey={Period} />);

    const approved = await waitFor(() => {
      const node = document.querySelector('[data-stat="approved"] [data-stat-value]');
      expect(node).not.toBeNull();
      return node;
    });

    // 4 від завершеного + по 1 від кожного з двох відстаючих.
    expect(approved?.textContent).toBe('6');
    expect(document.querySelector('[data-stat="rejected"]')?.getAttribute('data-stat-tone')).toBe('danger');
  });
});

describe('огляд кампанії: L10', () => {
  it('відмова сервера — це помилка, а не порожня кампанія', async () => {
    mockServer('refuse');
    show(<CampaignOverview periodKey={Period} />);

    const alert = await screen.findByRole('alert');

    expect(alert.textContent).toContain('ECR-SYS-0500');
    expect(screen.queryByText('⟦campaign.emptyTitle⟧')).toBeNull();
    expect(document.querySelector('[data-stat-strip]')).toBeNull();
  });

  it('відмова при повторному запиті не показує старий перелік із кешу', async () => {
    mockServer('refuse');
    show(<CampaignOverview periodKey={Period} />, clientWith(summaryOf([behind(1, 'STALE-1')], 1)));

    const alert = await screen.findByRole('alert');

    expect(alert.textContent).toContain('ECR-SYS-0500');
    expect(laggingCodes()).toEqual([]);
    expect(screen.queryByText('STALE-1')).toBeNull();
  });

  it('кампанія без проєктів — пояснення, а не таблиця з нулями', async () => {
    mockServer(summaryOf([], 0));
    show(<CampaignOverview periodKey={Period} />);

    expect(await screen.findByText('⟦campaign.emptyTitle⟧')).toBeTruthy();
    expect(document.querySelector('[data-stat-strip]')).toBeNull();
  });
});

describe('огляд кампанії: право', () => {
  function me(permissions: string[]): CurrentUserDto {
    return {
      userId: 1,
      userName: 'tester',
      language: 'en',
      permissions,
      denies: [],
      grants: {},
      isSimulation: false,
      simulatedForUserId: null,
      mustChangePassword: false,
    };
  }

  function showGuarded(permissions: string[]): void {
    const client = clientWith();
    client.setQueryData(MeQueryKey, me(permissions));

    show(
      <RouteGuard handle={routes.adminCampaign.handle}>
        <CampaignOverviewPage />
      </RouteGuard>,
      client,
    );
  }

  it('без Report.ViewCampaign — відмова з назвою права, і запит огляду не йде', async () => {
    const server = mockServer(summaryOf([behind(1, 'P1')], 1), me([]));
    showGuarded([]);

    const denial = await screen.findByRole('alert');

    expect(denial.textContent).toContain('Report.ViewCampaign');
    expect(server.calls()).toBe(0);
    expect(laggingCodes()).toEqual([]);
  });

  it('з правом — огляд відкривається', async () => {
    const server = mockServer(summaryOf([behind(1, 'P1')], 1), me(['Report.ViewCampaign']));
    showGuarded(['Report.ViewCampaign']);

    await waitFor(() => expect(laggingCodes()).toEqual(['P1']));
    expect(server.calls()).toBe(1);
  });
});

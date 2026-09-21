import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { JSX } from 'react';
import type { CurrentUserDto } from '@/api/types';
import { RouteGuard } from '@/app/RouteGuard';
import { routes } from '@/app/routes';
import type { CampaignProgress, CampaignProject, CampaignSummary, CampaignTotals } from '@/features/campaign/api';
import { CampaignOverview } from '@/features/campaign/CampaignOverview';
import { CampaignOverviewPage } from '@/pages/admin/CampaignOverviewPage';
import { formatDate } from '@/shared/format';
import { MeQueryKey } from '@/shared/session/useSession';
import { testTheme } from '@/test/render';

/**
 * Огляд звітної кампанії (`BE-22`, екран `/admin/campaign`).
 *
 * ⛔ Твердження, заради яких цей файл існує:
 *   1. обрізаний стелею перелік НАЗВАНИЙ обрізаним («показано N із M») — мовчки
 *      показати 200 із 300 означає відповісти на «хто затримує кампанію»
 *      неправдою; і дзеркало — повна відповідь банера не має;
 *   2. підсумки й лічильники класів — з `totals` СЕРВЕРА, по всіх проєктах
 *      періоду, а не сума обрізаного переліку;
 *   3. «хто затримує» — за серверним `progress` (`Overdue`, `AtRisk`), а не за
 *      клієнтським правилом; останній день подання — доба перед
 *      `submissionDeadline`, у поясі ПРОЄКТУ;
 *   4. відмова сервера не виглядає ні як порожня кампанія, ні як старий
 *      перелік із кешу (`L10`: помилка → очікування → дані);
 *   5. без права `Report.ViewCampaign` екрана немає і запит не йде.
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

/**
 * Строк подання майданчика в UTC+5 — опівніч, з якої проєкт прострочений.
 *
 * ⚠ Навмисно біля півночі й зі зсувом на схід від будь-якого поясу, де
 * запускаються тести (UTC у CI, Київ локально): той самий момент там — ще
 * 15 червня ввечері, тож дата, порахована в поясі браузера, з'їхала б на
 * 14 червня.
 */
const DeadlineAt = '2026-06-16T00:00:00+05:00';

/** Останній день подання для `DeadlineAt` — доба перед ним, у поясі проєкту. */
const LastDay = '2026-06-15';

/** Проєкт заданого класу; лічильники узгоджені з класом. */
function project(id: number, code: string, progress: CampaignProgress, deadline: string | null = DeadlineAt): CampaignProject {
  const counts =
    progress === 'Done'
      ? { documents: 4, draft: 0, submitted: 0, approved: 4, rejected: 0, snapshots: 1 }
      : { documents: 6, draft: 2, submitted: 1, approved: 1, rejected: 2, snapshots: 0 };

  return {
    projectId: id,
    projectCode: code,
    nameL10n: { values: { en: `Project ${code}` } },
    ...counts,
    progress,
    submissionDeadline: deadline,
  };
}

/** Підсумки, які рахує сервер: по ВСІХ проєктах періоду. */
function totalsOf(all: readonly CampaignProject[]): CampaignTotals {
  const sum = (pick: (p: CampaignProject) => number): number => all.reduce((acc, p) => acc + pick(p), 0);
  const count = (progress: CampaignProgress): number => all.filter((p) => p.progress === progress).length;

  return {
    projects: all.length,
    documents: sum((p) => p.documents),
    draft: sum((p) => p.draft),
    submitted: sum((p) => p.submitted),
    approved: sum((p) => p.approved),
    rejected: sum((p) => p.rejected),
    snapshots: sum((p) => p.snapshots),
    done: count('Done'),
    overdue: count('Overdue'),
    atRisk: count('AtRisk'),
    inProgress: count('InProgress'),
  };
}

/**
 * Відповідь сервера: `listed` — у переліку, `hidden` — за стелею.
 *
 * ⚠ `totals` і `totalProjects` рахуються по ОБОХ, як на сервері; саме тому за
 * непорожнього `hidden` вони відрізняються від суми переліку.
 */
function summaryOf(listed: CampaignProject[], hidden: CampaignProject[] = []): CampaignSummary {
  return {
    periodKey: Period,
    totalProjects: listed.length + hidden.length,
    projects: listed,
    totals: totalsOf([...listed, ...hidden]),
  };
}

/** Перелік потрібної довжини: кожен другий проєкт прострочений. */
function manyProjects(count: number, from = 1): CampaignProject[] {
  return Array.from({ length: count }, (_, i) => {
    const id = from + i;

    return project(id, `P${String(id)}`, i % 2 === 0 ? 'Overdue' : 'Done');
  });
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

/** Рядок таблиці за кодом проєкту. */
function rowOf(code: string): Element {
  const row = [...document.querySelectorAll('tbody tr[data-row-key]')].find(
    (candidate) => candidate.querySelector('td')?.textContent === code,
  );

  if (row === undefined) throw new Error(`рядка ${code} немає`);

  return row;
}

/** Число показника смуги; чекає, доки смуга з'явиться. */
async function stat(id: string): Promise<string | null | undefined> {
  return waitFor(() => {
    const node = document.querySelector(`[data-stat="${id}"] [data-stat-value]`);
    expect(node).not.toBeNull();
    return node?.textContent;
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('огляд кампанії: усічення', () => {
  it('сервер віддав 200 із 300 — екран каже «показано 200 із 300» і що підсумки повні', async () => {
    mockServer(summaryOf(manyProjects(200), manyProjects(100, 201)));
    show(<CampaignOverview periodKey={Period} />);

    const banner = await screen.findByTestId('campaign-truncated');

    expect(banner.textContent).toContain('campaign.truncatedTitle');
    expect(banner.textContent).toContain('200');
    expect(banner.textContent).toContain('300');
    // Обрізано лише перелік: підказка — про повні підсумки, не «лише показані».
    expect(banner.textContent).toContain('campaign.truncatedListHint');
    expect(banner.textContent).not.toContain('campaign.truncatedHint');
  });

  it('дзеркало: сервер віддав усе — банера усічення немає', async () => {
    mockServer(summaryOf([project(1, 'P1', 'Overdue'), project(2, 'P2', 'Done')]));
    show(<CampaignOverview periodKey={Period} />);

    await waitFor(() => expect(laggingCodes()).toEqual(['P1']));

    expect(screen.queryByTestId('campaign-truncated')).toBeNull();
  });
});

describe('огляд кампанії: підсумки з сервера', () => {
  /*
   * ⛔ Перелік обрізано: у ньому 2 проєкти з 6. Суми переліку (approved 1+4=5,
   * overdue 1) відрізняються від `totals` сервера (approved 15, overdue 2), тож
   * екран, що складає підсумки сам, показав би інші числа.
   */
  const listed = [project(1, 'P1', 'Overdue'), project(2, 'P2', 'Done')];
  const hidden = [
    project(3, 'P3', 'Done'),
    project(4, 'P4', 'Done'),
    project(5, 'P5', 'AtRisk'),
    project(6, 'P6', 'Overdue'),
  ];

  it('лічильники станів документів — з totals, а не сума обрізаного переліку', async () => {
    mockServer(summaryOf(listed, hidden));
    show(<CampaignOverview periodKey={Period} />);

    // 3 × Done по 4 затверджених + 3 × (Overdue/AtRisk) по 1 = 15; сума
    // переліку дала б 5.
    expect(await stat('approved')).toBe('15');
    expect(await stat('draft')).toBe('6');
    expect(await stat('rejected')).toBe('6');
    expect(document.querySelector('[data-stat="rejected"]')?.getAttribute('data-stat-tone')).toBe('danger');
  });

  it('лічильники класів — з totals: прострочені, під загрозою, в роботі, завершені', async () => {
    mockServer(summaryOf(listed, hidden));
    show(<CampaignOverview periodKey={Period} />);

    expect(await stat('overdue')).toBe('2');
    expect(await stat('atRisk')).toBe('1');
    expect(await stat('inProgress')).toBe('0');
    expect(await stat('done')).toBe('3');
    expect(document.querySelector('[data-stat="overdue"]')?.getAttribute('data-stat-tone')).toBe('danger');
    expect(document.querySelector('[data-stat="atRisk"]')?.getAttribute('data-stat-tone')).toBe('warning');
  });
});

describe('огляд кампанії: хто затримує', () => {
  it('у переліку лише Overdue і AtRisk, прострочені вгорі; код — рядком, без роздільників', async () => {
    mockServer(
      summaryOf([
        project(1, 'P-01', 'Done'),
        project(2, 'P-02', 'InProgress'),
        project(3, 'P-03', 'AtRisk'),
        project(4, '10000', 'Overdue'),
        project(5, 'P-05', 'Overdue'),
      ]),
    );
    show(<CampaignOverview periodKey={Period} />);

    await waitFor(() => expect(laggingCodes()).toEqual(['10000', 'P-05', 'P-03']));

    expect(rowOf('10000').querySelector('[data-campaign-progress]')?.getAttribute('data-campaign-progress')).toBe('Overdue');
    expect(rowOf('10000').querySelector('[data-campaign-progress]')?.getAttribute('data-status-tone')).toBe('danger');
    expect(rowOf('P-03').querySelector('[data-campaign-progress]')?.getAttribute('data-campaign-progress')).toBe('AtRisk');
    expect(rowOf('P-03').querySelector('[data-campaign-progress]')?.getAttribute('data-status-tone')).toBe('warning');
  });

  it('клас рахує сервер: InProgress із незатвердженими документами без зрізу — не затримує', async () => {
    // Колишнє клієнтське правило («затверджено не все або зрізу немає») взяло
    // б цей проєкт у перелік; серверне — ні, бо строк ще далеко.
    mockServer(summaryOf([project(1, 'P1', 'InProgress'), project(2, 'P2', 'Done')]));
    show(<CampaignOverview periodKey={Period} />);

    expect(await screen.findByText('⟦campaign.nobodyLagging⟧')).toBeTruthy();
    expect(laggingCodes()).toEqual([]);
  });

  it('дзеркало: усі Done — «ніхто не затримує»', async () => {
    mockServer(summaryOf([project(1, 'P1', 'Done'), project(2, 'P2', 'Done')]));
    show(<CampaignOverview periodKey={Period} />);

    expect(await screen.findByText('⟦campaign.nobodyLagging⟧')).toBeTruthy();
    expect(laggingCodes()).toEqual([]);
    expect(await stat('done')).toBe('2');
  });

  it('останній день подання — доба перед строком, у поясі проєкту, а не браузера', async () => {
    mockServer(summaryOf([project(1, 'P1', 'Overdue', DeadlineAt)]));
    show(<CampaignOverview periodKey={Period} />);

    await waitFor(() => expect(laggingCodes()).toEqual(['P1']));

    const day = rowOf('P1').querySelector('[data-last-day]');

    expect(day?.getAttribute('data-last-day')).toBe(LastDay);
    expect(day?.textContent).toBe(formatDate(LastDay));
  });

  it('строк не пораховано (null) — «строк не визначено», а не вигадана дата', async () => {
    mockServer(summaryOf([project(1, 'P1', 'AtRisk', null)]));
    show(<CampaignOverview periodKey={Period} />);

    await waitFor(() => expect(laggingCodes()).toEqual(['P1']));

    const day = rowOf('P1').querySelector('[data-last-day]');

    expect(day?.getAttribute('data-last-day')).toBe('');
    expect(day?.textContent).toBe('⟦campaign.deadlineUnknown⟧');
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
    show(<CampaignOverview periodKey={Period} />, clientWith(summaryOf([project(1, 'STALE-1', 'Overdue')])));

    const alert = await screen.findByRole('alert');

    expect(alert.textContent).toContain('ECR-SYS-0500');
    expect(laggingCodes()).toEqual([]);
    expect(screen.queryByText('STALE-1')).toBeNull();
  });

  it('кампанія без проєктів — пояснення, а не таблиця з нулями', async () => {
    mockServer(summaryOf([]));
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
    const server = mockServer(summaryOf([project(1, 'P1', 'Overdue')]), me([]));
    showGuarded([]);

    const denial = await screen.findByRole('alert');

    expect(denial.textContent).toContain('Report.ViewCampaign');
    expect(server.calls()).toBe(0);
    expect(laggingCodes()).toEqual([]);
  });

  it('з правом — огляд відкривається', async () => {
    const server = mockServer(summaryOf([project(1, 'P1', 'Overdue')]), me(['Report.ViewCampaign']));
    showGuarded(['Report.ViewCampaign']);

    await waitFor(() => expect(laggingCodes()).toEqual(['P1']));
    expect(server.calls()).toBe(1);
  });
});

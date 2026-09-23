import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest';
import { act, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX } from 'react';
import { loadCatalog } from '@/shared/i18n';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { testTheme } from '@/test/render';

/**
 * `U-10` — єдиний доступний варіант має бути обраний.
 *
 * ⛔ `/admin/periods` відкривалася на «Pick a project», хоча проєкт у системі
 * ОДИН: сторінка вимагала вибору там, де вибору немає, і до кліку лишалася
 * порожньою.
 *
 * ⛔ Межа автовибору названа й перевірена: обирається ЛИШЕ коли варіант рівно
 * один. Проєкти рівноправні — «поточного» серед них немає (на відміну від
 * періоду з `isCurrent`), тож іншого чесного критерію, ніж «він один», не
 * існує, а автовибір першого-ліпшого мовчки показав би чужий календар.
 *
 * ⛔ Мутаційні докази:
 *   1. прибрати `useEffect` автовибору → червоніє перший випадок
 *      («Pick a project» лишається, календаря немає);
 *   2. замінити `items.length === 1 ? items[0] : undefined` на `items[0]` →
 *      червоніє другий випадок (проєктів два, а вибір уже зроблено).
 */

const project = { id: 1, code: 'AUDIT_SMOKE_PRJ', status: 'Active' as const };
const otherProject = { id: 2, code: 'SECOND_PRJ', status: 'Active' as const };

const calendar = {
  timeZoneId: 'Asia/Aqtau',
  policy: { code: 'ECR-Standard', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45 },
  periods: [
    {
      id: 11,
      periodKey: 202609,
      sequence: 9,
      state: 'Open',
      isCurrent: true,
      startsAt: '2026-09-01T00:00:00Z',
      endsAt: '2026-10-15T00:00:00Z',
      graceEndsAt: null,
      reopenedUntil: null,
    },
  ],
};

const SeededStrings: Record<string, string> = {
  'periods.title': 'Periods',
  'periods.project': 'Project',
  'periods.pickProject': 'Pick a project',
  'periods.noProjects': 'No projects yet',
  'periods.noProjectsHint': 'A project defines the reporting calendar.',
  'periods.noPeriods': 'This project has no periods',
  'periods.noPeriodsHint': 'Periods are generated from the project calendar.',
  'periods.key': 'Period',
  'periods.sequence': 'No.',
  'periods.range': 'Range',
  'periods.rangeHint': 'Range of {code}: +{open}/+{hardClose}.',
  'periods.state': 'State',
  'periods.grace': 'Grace until',
  'periods.graceHint': 'Grace of {code}: +{grace}/+{hardClose}.',
  'periods.timeZone': 'Site time zone (IANA)',
  'periods.current': 'Current',
  'status.period.Open': 'Open',
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

/** Адреси, куди сторінка сходила, — у порядку викликів. */
let requested: string[] = [];

/** Чи вже віддано перелік проєктів — саме з нього починається автовибір. */
let projectsServed = false;

function serve(projects: readonly unknown[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      requested.push(url);

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      }
      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: [],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      // ⚠ Календар — РАНІШЕ за перелік: його адреса містить адресу переліку
      // як префікс.
      if (/\/api\/v1\/projects\/\d+\/periods/.test(url)) return json(calendar);
      if (url.includes('/api/v1/projects')) {
        // ⚠ Позначка ставиться перед ВІДДАЧЕЮ, а не при виклику: `requested`
        // наповнюється в момент запиту, тобто ЩЕ ДО того, як дані дійшли до
        // компонента, і чекати на неї означало б стверджувати про стан, якого
        // сторінка ще не бачила.
        projectsServed = true;

        return json({ items: projects, nextCursor: null, totalCount: projects.length });
      }

      return json(null);
    }),
  );
}

let search = '';

function LocationProbe(): JSX.Element {
  search = useLocation().search;

  return <span data-search={search} />;
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/periods']}>
        <QueryClientProvider client={client}>
          <PeriodsPage />
          <LocationProbe />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

beforeEach(() => {
  search = '';
  requested = [];
  projectsServed = false;
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('PeriodsPage: автовибір єдиного проєкту (U-10)', () => {
  it('проєкт один — обраний сам, календар видно, «Pick a project» зникло', async () => {
    serve([project]);
    await loadCatalog('en', 'private');

    show();

    /*
     * ⛔ Вибір лягає в АДРЕСУ (`?projectId=`), а не в локальний стан: поле
     * показує його, і посилання на сторінку лишається робочим.
     */
    await waitFor(() => {
      expect(new URLSearchParams(search).get('projectId')).toBe('1');
    });

    // Календар на місці — сторінка більше не відкривається порожньою.
    await screen.findByText('202609');

    /*
     * ⛔ Порожнього стану «Pick a project» на екрані немає взагалі: перешкоду
     * знято, отже й пояснювати нічого (те саме правило, що в `U-08`).
     *
     * ⚠ Плейсхолдер `Select` із тим самим рядком тут не заважає — він
     * атрибут, а не текстовий вузол.
     */
    expect(screen.queryByRole('heading', { level: 4, name: 'Pick a project' })).toBeNull();
  });

  it('проєктів два — вибір лишається людині', async () => {
    serve([project, otherProject]);
    await loadCatalog('en', 'private');

    show();

    /*
     * ⛔ Межа автовибору. Узяти перший із двох означало б мовчки показати
     * календар ЧУЖОГО проєкту, і ніщо на екрані не сказало б, що вибір
     * зроблено за людину.
     */
    /*
     * ⛔ Спершу дочекатися, поки перелік проєктів ПРИЇХАВ, і лише тоді
     * стверджувати. Інакше твердження виконалось би на першому рендері — до
     * відповіді сервера, коли «Pick a project» стоїть на екрані ще й без
     * жодного автовибору: мутація «бери першого-ліпшого» проходила б зеленою,
     * тобто твердження не доводило б нічого. (Перевірено: саме так воно й
     * поводилось до цієї гілки чекання.)
     */
    await waitFor(() => {
      expect(projectsServed).toBe(true);
    });

    /*
     * ⚠ І ще проміжок ПІСЛЯ цього: ефект автовибору спрацював би вже на
     * наступному коміті, і твердження без цієї паузи випередило б його.
     * Перевірено мутацією — без паузи випадок лишався зеленим і з автовибором
     * «першого-ліпшого».
     */
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));
    });

    expect(screen.getByRole('heading', { level: 4, name: 'Pick a project' })).toBeDefined();
    expect(new URLSearchParams(search).get('projectId')).toBeNull();

    /*
     * ⛔ Найпряміший доказ: календаря ніхто не питав. Автовибір «першого з
     * двох» одразу пішов би по `/api/v1/projects/1/periods` — тобто показав би
     * дані проєкту, якого людина не обирала.
     */
    expect(requested.some((url) => /\/api\/v1\/projects\/\d+\/periods/.test(url))).toBe(false);
    expect(screen.queryByText('202609')).toBeNull();
  });
});

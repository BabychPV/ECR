import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { JSX } from 'react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';

/**
 * Кнопка «My tasks» у шапці: бюджет, `L2`, межа доступу, позначка (`UI-07`).
 *
 * ⛔ Чим ловиться лінивість. Фабрика `vi.mock` виконується тоді, коли модуль
 * ІМПОРТУЮТЬ уперше. Статичний `import { MyTasksDrawer }` у
 * `MyTasksLauncher.tsx` обчислив би її разом із самим лаунчером — ще до
 * рендера, — і перша перевірка нижче почервоніла б. Той самий прийом і та
 * сама причина, що в `features/search/__tests__/SearchLauncher.lazy.test.tsx`.
 *
 * ⛔ Порядок тестів у файлі значущий: модуль обчислюється один раз на файл,
 * тож перевірки «ще не обчислено» стоять перед першим відкриттям.
 */
const probe = vi.hoisted(() => ({ evaluated: false }));

vi.mock('@/features/jobs/MyTasksDrawer', () => {
  probe.evaluated = true;

  return {
    MyTasksDrawer: ({
      opened,
      jobs,
    }: {
      opened: boolean;
      jobs: readonly { jobId: string }[] | undefined;
    }): JSX.Element | null =>
      opened ? (
        <div role="dialog" aria-label="my-tasks-stub">
          {(jobs ?? []).map((job) => (
            <span key={job.jobId} data-stub-job={job.jobId} />
          ))}
        </div>
      ) : null,
    myTaskDocumentHref: (id: number): string => `/documents/${String(id)}`,
  };
});

import { MyTasksLauncher } from '@/features/jobs/MyTasksLauncher';

/** Власна задача користувача — рівно те, що віддав би сервер на `mine=true`. */
const mine = [
  {
    jobCode: 'Ecr.Application.Ports.IExcelExportJob',
    jobId: 'IExcelExportJob#7',
    percent: 40,
    startedAt: '2026-09-22T09:58:00Z',
    state: 'Running',
    updatedAt: '2026-09-22T10:00:00Z',
  },
  {
    jobCode: 'Ecr.Application.Ports.IRecalculationJob',
    jobId: 'IRecalculationJob#3',
    percent: 100,
    startedAt: '2026-09-22T08:00:00Z',
    state: 'Succeeded',
    updatedAt: '2026-09-22T08:30:00Z',
  },
];

/**
 * Чужа задача. Сервер віддав би її ЛИШЕ на запит без `mine`, і лише тому, хто
 * має `System.ViewHealth`.
 */
const everyones = [
  ...mine,
  {
    jobCode: 'Ecr.Application.Ports.IExcelImportJob',
    jobId: 'IExcelImportJob#alien',
    percent: 10,
    startedAt: '2026-09-22T09:00:00Z',
    state: 'Running',
    updatedAt: '2026-09-22T09:30:00Z',
  },
];

const requested: string[] = [];

function mockFetch(): void {
  requested.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/jobs')) {
        requested.push(url);

        return Promise.resolve(
          new Response(JSON.stringify(url.includes('mine=true') ? mine : everyones), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
        );
      }

      return Promise.resolve(new Response(JSON.stringify(null), { status: 404 }));
    }),
  );
}

function mount(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MyTasksLauncher />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  requested.length = 0;
  cleanup();
});

describe('кнопка «My tasks» у шапці', () => {
  it('є в шапці без жодної перевірки права і НЕ відкриває шухляду сама (L2)', async () => {
    mockFetch();
    mount();

    /*
     * ⛔ Дзеркало вимоги «усі ролі» (директива №15, бекенд, §BE-08: «шухляда
     * «My tasks» у шапці (усі ролі)»). Компонент навмисно не звертається ані
     * до `useSession`, ані до `can(...)`: додайте сюди умову права — і кнопка
     * зникне для того, у кого профілю в тесті взагалі немає, тобто цей рядок
     * почервоніє.
     */
    const button = await screen.findByRole('button', { name: '⟦jobs.myTasks⟧' });
    expect(button.getAttribute('aria-haspopup')).toBe('dialog');

    // `L2`: після рендера шухляди немає в дереві зовсім.
    expect(screen.queryByRole('dialog')).toBeNull();

    // І її модуль навіть не обчислено — бюджет `D-132`.
    expect(probe.evaluated).toBe(false);
  });

  it('позначка рахує лише активні ВЛАСНІ задачі, а запит іде з mine=true', async () => {
    mockFetch();
    mount();

    /*
     * ⛔ Мутаційний доказ «шухляда показує чужі задачі». Приберіть `mine=true`
     * (перемкніть `useMyTasks` на `recentJobsUrl(false)` або зберіть адресу
     * тут самостійно) — і заглушка віддасть `everyones`, де активних задач
     * ДВІ, а не одна. Обидва твердження нижче падають.
     */
    await waitFor(() => expect(requested.length).toBeGreaterThan(0));
    expect(requested.every((url) => url.includes('mine=true'))).toBe(true);

    // ⚠ Ключа ще немає в каталозі (`09-seed.sql` — сусідній PR), і `t()`
    // навмисно НЕ губить параметрів: позначка відсутнього рядка несе `n=1`.
    const badge = await screen.findByLabelText('⟦jobs.myTasksActive (n=1)⟧');
    expect(badge.getAttribute('data-my-tasks-active')).toBe('1');
    expect(badge.textContent).toBe('1');
  });

  it('клік відкриває шухляду й лише тоді довантажує її модуль', async () => {
    mockFetch();
    mount();

    const button = await screen.findByRole('button', { name: '⟦jobs.myTasks⟧' });
    expect(probe.evaluated).toBe(false);

    /*
     * ⚠ `fireEvent`, а не `userEvent` — і це не стиль, а вимушено. Під
     * `userEvent.setup()` у цьому наборі підміна модуля (`vi.mock`) до
     * динамічного `import()` усередині `MyTasksLauncher` НЕ доїжджає: у дереві
     * опиняється справжня шухляда замість заглушки, і перевірка бюджету
     * перевіряє не те, що написано. Перевірено А/Б на ОДНОМУ файлі: той самий
     * клік через `fireEvent` дає заглушку, через `userEvent` — справжній
     * модуль. Той самий прийом і з тієї ж причини вживає сусідній сторож
     * `features/search/__tests__/SearchLauncher.lazy.test.tsx`.
     */
    fireEvent.click(button);

    expect(await screen.findByRole('dialog', { name: 'my-tasks-stub' })).toBeTruthy();
    expect(probe.evaluated).toBe(true);

    // У шухляду доїхали рівно власні задачі — чужої серед них немає.
    await waitFor(() => expect(document.querySelectorAll('[data-stub-job]')).toHaveLength(2));
    expect(document.querySelector('[data-stub-job="IExcelImportJob#alien"]')).toBeNull();
  });
});

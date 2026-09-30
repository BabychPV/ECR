import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CreateProjectModal } from '@/features/projects/CreateProjectModal';
import { testTheme } from '@/test/render';

/**
 * Відмова джерел форми робила обов'язкові переліки порожніми — і мовчала.
 *
 * ⛔ Версія шаблону й політика періодів збиралися через `?? []`. При відмові
 * сервера обидва `Select` ставали порожніми, а підказка внизу
 * (`StillNeeded`, ключ `common.stillNeeded`) сумлінно перелічувала їх як «ще не заповнено».
 *
 * ⚠ Наслідок не «людина не зрозуміла». Конфігуратор читає порожній перелік як
 * «опублікованих версій шаблону ще немає» або «політик не заведено» — і йде
 * створювати ЩЕ ОДНУ версію шаблону чи ЩЕ ОДНУ політику. Відмова запиту
 * штовхає його робити зайву, а потім дублюючу конфігурацію.
 */

const Refusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'перелік політик періодів прочитати не вдалося',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-policies-1',
  // ⚠ Без ознаки подробиця до екрана не доходить (рішення людини про мову).
  messageKey: 'err.ECR-SYS-0500.unexpected',
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json',
    },
  });
}

function mockServer(policiesFail: boolean): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      /*
       * ⚠ `/api/v1/me` звіряється ТОЧНО: `includes` тут збігся б і з
       * `/api/v1/methodologies`, і заглушка тихо віддавала б профіль у
       * відповідь на інший запит. Я на цьому вже спіткнувся в сусідньому
       * наборі — тест червонів не з тієї причини, яку перевіряє.
       */
      if (path.endsWith('/api/v1/me')) {
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

      if (path.endsWith('/api/v1/languages')) {
        return json([{ code: 'en', nameNative: 'English', isDefault: true }]);
      }

      if (path.endsWith('/api/v1/projects/period-policies')) {
        return policiesFail
          ? json(Refusal, 500)
          : json([{ id: 1, code: 'ECR-Standard', graceOffsetDays: 15, hardCloseOffsetDays: 45 }]);
      }

      if (/\/api\/v1\/templates\/\d+\/versions$/.test(path)) {
        return json({
          items: [{ id: 10, version: '1.0', status: 'Published', presentationRevision: 0 }],
          nextCursor: null,
          totalCount: 1,
        });
      }

      if (path.endsWith('/api/v1/templates')) {
        return json({
          items: [{ id: 3, code: 'AIR', nameL10n: { values: {} } }],
          nextCursor: null,
          totalCount: 1,
        });
      }

      return json(null);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <CreateProjectModal opened onClose={() => {}} onCreated={() => {}} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CreateProjectModal: відмова джерел не виглядає як «нічого не заведено»', () => {
  it('політики не приїхали — на екрані причина з кодом, а не мовчазний порожній перелік', async () => {
    mockServer(true);
    show();

    const alert = await waitFor(() => screen.getByRole('alert'));

    expect(alert.textContent ?? '').toContain('перелік політик періодів прочитати не вдалося');
    expect(alert.textContent ?? '').toContain('ECR-SYS-0500');
  });

  it('решта форми ЛИШАЄТЬСЯ робочою — відмова одного джерела не забирає діалог', async () => {
    /*
     * ⚠ Порядок тверджень — половина доказу (урок із сусіднього набору): спершу
     * чекаємо на банер, тобто на момент, коли відмова вже в стані, і лише
     * ПІСЛЯ цього питаємо про поля. Інакше випадок лишався б зеленим і тоді,
     * коли код ховає весь діалог.
     */
    mockServer(true);
    show();

    await waitFor(() => screen.getByRole('alert'));

    expect(screen.getByLabelText(/periods\.code/)).toBeDefined();
    expect(screen.getByRole('button', { name: /common\.save/ })).toBeDefined();
  });

  it('усе приїхало — банера немає', async () => {
    mockServer(false);
    show();

    // ⚠ Дочекатися саме заповненого переліку, а не просто монтування: запит
    // ще в дорозі зробив би це твердження зеленим на будь-якому коді.
    await waitFor(() => {
      expect(screen.getByLabelText(/periods\.policy/)).toBeDefined();
    });

    expect(screen.queryByRole('alert')).toBeNull();
  });
});

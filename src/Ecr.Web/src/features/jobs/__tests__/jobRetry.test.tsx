import type { ReactElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { canRestartJob, JobResultLink, JobRetry } from '@/features/jobs/JobFacts';

/**
 * «Повторити» і посилання на результат — юніт-рівень (`UX-09`, директива
 * №11, T10 #40).
 *
 * ⛔ `canRestartJob` перевіряється ОКРЕМО від дерева: саме вона несе рішення
 * «чужа задача без ViewHealth» і «не-Failed стан», і чиста функція ловить
 * мутацію в цій умові дешевше й точніше, ніж рендер з мокнутою мережею.
 *
 * ⚠ `JobRetry` кличе `useRestartJob` (тобто `useMutation`) БЕЗУМОВНО, до
 * власної перевірки видимості — тому навіть тест «кнопки немає» потребує
 * `QueryClientProvider`.
 */

function show(ui: ReactElement): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>{ui}</QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('canRestartJob — правило показу (чиста функція)', () => {
  it('Failed + власна задача — так', () => {
    expect(canRestartJob('Failed', true, false)).toBe(true);
  });

  it('Failed + ViewHealth без власності — так', () => {
    expect(canRestartJob('Failed', false, true)).toBe(true);
  });

  it('мутація: Failed, чужа задача, без ViewHealth — ні', () => {
    expect(canRestartJob('Failed', false, false)).toBe(false);
  });

  it('мутація: не-Failed стан, навіть власна задача з ViewHealth — ні', () => {
    expect(canRestartJob('Running', true, true)).toBe(false);
    expect(canRestartJob('Succeeded', true, true)).toBe(false);
    expect(canRestartJob('Queued', true, true)).toBe(false);
  });
});

describe('JobRetry — кнопка «Повторити»', () => {
  it('провалена власна задача — кнопка є', () => {
    show(
      <JobRetry jobId="j-1" state="Failed" isOwnJob hasViewHealth={false} />,
    );

    expect(screen.getByRole('button', { name: '⟦jobs.restart⟧' })).toBeTruthy();
  });

  it('провалена чужа задача з ViewHealth — кнопка є', () => {
    show(
      <JobRetry jobId="j-2" state="Failed" isOwnJob={false} hasViewHealth />,
    );

    expect(screen.getByRole('button', { name: '⟦jobs.restart⟧' })).toBeTruthy();
  });

  it('мутація: провалена чужа задача БЕЗ ViewHealth — кнопки немає', () => {
    show(
      <JobRetry jobId="j-3" state="Failed" isOwnJob={false} hasViewHealth={false} />,
    );

    expect(screen.queryByRole('button', { name: '⟦jobs.restart⟧' })).toBeNull();
    expect(document.querySelector('[data-job-retry]')).toBeNull();
  });

  it('мутація: власна задача, не Failed (дзеркало) — кнопки немає', () => {
    show(
      <JobRetry jobId="j-4" state="Running" isOwnJob hasViewHealth={false} />,
    );

    expect(screen.queryByRole('button', { name: '⟦jobs.restart⟧' })).toBeNull();
  });
});

describe('JobResultLink — посилання на файл результату', () => {
  it('resultUrl не null — посилання веде саме на нього', () => {
    show(<JobResultLink resultUrl="/api/v1/documents/7/export/e-1" />);

    const link = screen.getByRole('link', { name: '⟦jobs.resultDownload⟧' });
    expect(link.getAttribute('href')).toBe('/api/v1/documents/7/export/e-1');
  });

  it('мутація: resultUrl = null — посилання ігнорується', () => {
    show(<JobResultLink resultUrl={null} />);
    expect(document.querySelector('[data-job-result]')).toBeNull();
  });

  it('resultUrl не задано (undefined) — те саме, що null', () => {
    show(<JobResultLink resultUrl={undefined} />);
    expect(document.querySelector('[data-job-result]')).toBeNull();
  });
});

import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { StaleResultsBanner } from '@/features/documents/StaleResultsBanner';
import { testTheme } from '@/test/render';

/**
 * «Результати застаріли»: банер із кнопкою «Recalculate» (рішення людини: автоперерахунку немає).
 *
 * ⛔ Кнопка — та сама дія, що в «More» і F9 (передається ззовні); без права дії банер лише повідомляє.
 */

function show(props: Parameters<typeof StaleResultsBanner>[0]): void {
  render(
    <MantineProvider theme={testTheme}>
      <StaleResultsBanner {...props} />
    </MantineProvider>,
  );
}

describe('StaleResultsBanner', () => {
  it('з датою називає її, без дати - загальний текст; роль із правом бачить кнопку і запускає перерахунок', () => {
    const run = vi.fn();
    show({ since: '2026-10-07T09:30:00Z', recalculate: { loading: false, running: false, run } });

    const banner = screen.getByTestId('document-stale-results');
    expect(banner.textContent).toContain('⟦document.staleResults.title⟧');
    expect(screen.getByTestId('document-stale-results-hint').textContent).toContain('document.staleResults.hintSince');

    fireEvent.click(screen.getByTestId('document-stale-results-recalculate'));
    expect(run).toHaveBeenCalledTimes(1);
  });

  it('без дати від сервера - рядок без дати', () => {
    show({ since: null, recalculate: { loading: false, running: false, run: vi.fn() } });

    const hint = screen.getByTestId('document-stale-results-hint').textContent ?? '';
    expect(hint).toContain('document.staleResults.hint');
    expect(hint).not.toContain('hintSince');
  });

  it('перерахунок іде - кнопка вимкнена і каже про це', () => {
    show({ since: null, recalculate: { loading: false, running: true, run: vi.fn() } });

    const button = screen.getByTestId('document-stale-results-recalculate');
    expect(button.hasAttribute('disabled')).toBe(true);
    expect(button.textContent).toContain('workflow.recalcRunning');
  });

  it('ролі без дії перерахунку кнопки немає, банер лишається', () => {
    show({ since: null, recalculate: null });

    expect(screen.getByTestId('document-stale-results')).toBeTruthy();
    expect(screen.queryByTestId('document-stale-results-recalculate')).toBeNull();
  });
});

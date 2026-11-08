import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { issue, table } from '@/features/calculationIssues/__tests__/fixtures';
import {
  AllowedValuesShown,
  RowLinksPerTable,
  UnmatchedCollapsedKey,
  UnmatchedRowsBanner,
} from '@/features/documents/UnmatchedRowsBanner';
import type { UnmatchedRows } from '@/features/documents/useUnmatchedRows';
import { testTheme } from '@/test/render';

/**
 * UAT 08.11 b1–b4: банер «Some rows are not included in the calculation».
 *
 * b1 — групи за таблицею з аркушем і кількістю, без повторів; b2 — кожен рядок - кнопка, що відкриває його в сітці;
 * b3 — одне пояснення «чому і як» (ключ `document.unmatchedRows.hint`) рівно один раз; b4 — згортання пам'ятається
 * після перемонтування (= перезавантаження), кількість видно й згорнутим.
 *
 * Мутації (перевірено вручну, див. звіт lane): не писати `localStorage` у `toggle` — червоніє b4; `onOpenRow` з
 * `group.key` замість екземпляра — червоніє b2; прибрати `slice(0, RowLinksPerTable)` — червоніє «і ще N».
 */

function rows(issues: UnmatchedRows['issues'], finishedAt: string | null = null): UnmatchedRows {
  return { issues, hints: [], finishedAt };
}

function show(value: UnmatchedRows, onOpenRow = vi.fn(), userId: number | undefined = 7) {
  const tables = [table(1, '9.01 Unit 400'), table(2, '9.03 Unit 430')];
  const view = render(
    <MantineProvider theme={testTheme}>
      <UnmatchedRowsBanner userId={userId} rows={value} tables={tables} onOpenRow={onOpenRow} />
    </MantineProvider>,
  );

  return { ...view, onOpenRow };
}

const sixty = rows([
  issue(1, 1, { rowKey: 'S01' }),
  issue(1, 2, { rowKey: 'S02' }),
  issue(2, 1, { rowKey: 'S01' }),
  issue(2, 2, { rowKey: 'S02' }),
]);

describe('UnmatchedRowsBanner', () => {
  beforeEach(() => window.localStorage.clear());
  afterEach(() => window.localStorage.clear());

  it('b1: групи «аркуш · таблиця — кількість», без повторів ключів у одному рядку', () => {
    show(sixty);

    const groups = screen.getAllByTestId('document-unmatched-rows-group').map((g) => g.textContent ?? '');
    expect(groups).toHaveLength(2);
    expect(groups[0]).toContain('⟦document.unmatchedRows.group (table=Sheet · 9.01 Unit 400, count=2)⟧');
    expect(groups[1]).toContain('⟦document.unmatchedRows.group (table=Sheet · 9.03 Unit 430, count=2)⟧');
    expect(screen.getByTestId('document-unmatched-rows-summary').textContent).toContain(
      '⟦document.unmatchedRows.summary (count=4, tables=2)⟧',
    );
    // Старий текст «Row: 1 (S01), 2 (S02), 1 (S01)…» - більше немає.
    expect(screen.getByTestId('document-unmatched-rows').textContent).not.toContain('(S01)');
  });

  it('b2: клік по рядку відкриває САМЕ цей рядок цієї таблиці', async () => {
    const { onOpenRow } = show(sixty);

    const links = screen.getAllByTestId('document-unmatched-row-link');
    expect(links).toHaveLength(4);
    await userEvent.click(links[3]!);

    expect(onOpenRow).toHaveBeenCalledExactlyOnceWith(102, 'S02');
  });

  it('b3: пояснення «чому і як» - один раз, тон інформації (status), не помилки', () => {
    show(sixty);

    const text = screen.getByTestId('document-unmatched-rows').textContent ?? '';
    expect(text.split('document.unmatchedRows.hint').length - 1).toBe(1);
    expect(screen.getByTestId('document-unmatched-rows-summary').getAttribute('role')).toBe('status');
  });

  it('b4: згорнутий банер лишається згорнутим після перемонтування; кількість видно', async () => {
    const first = show(sixty);
    const toggle = screen.getByTestId('document-unmatched-rows-toggle');
    expect(toggle.getAttribute('aria-expanded')).toBe('true');

    await userEvent.click(toggle);
    expect(window.localStorage.getItem(`${UnmatchedCollapsedKey}:7`)).toBe('1');
    first.unmount();

    show(sixty);
    expect(screen.getByTestId('document-unmatched-rows-toggle').getAttribute('aria-expanded')).toBe('false');
    expect(screen.getByTestId('document-unmatched-rows-summary').textContent).toContain('count=4');
  });

  it('fg6: згортання пам\'ятається для КОЖНОГО користувача - інший користувач бачить банер розгорнутим', async () => {
    const first = show(sixty, vi.fn(), 7);
    await userEvent.click(screen.getByTestId('document-unmatched-rows-toggle'));
    first.unmount();

    show(sixty, vi.fn(), 8);
    expect(screen.getByTestId('document-unmatched-rows-toggle').getAttribute('aria-expanded')).toBe('true');
  });

  it('fg6: згорнутий - лише рядок підсумку (назва · кількість), підказки й списку не видно', async () => {
    show(sixty);
    await userEvent.click(screen.getByTestId('document-unmatched-rows-toggle'));

    const title = screen.getByTestId('document-unmatched-rows-title');
    const summary = screen.getByTestId('document-unmatched-rows-summary');
    // Назва й кількість - в ОДНОМУ абзаці (один рядок), а не двома блоками.
    expect(title.parentElement).toBe(summary.parentElement);
    const body = document.getElementById('document-unmatched-rows-body');
    await waitFor(() => {
      const hidden =
        body?.getAttribute('aria-hidden') === 'true' || body?.style.display === 'none' || body?.style.height === '0px';
      expect(hidden).toBe(true);
    });
  });

  it('b4: недоступне сховище не ламає банер - розгорнутий за замовчуванням', () => {
    const spy = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied');
    });
    try {
      show(sixty);
      expect(screen.getByTestId('document-unmatched-rows-toggle').getAttribute('aria-expanded')).toBe('true');
    } finally {
      spy.mockRestore();
    }
  });

  it('понад межу посилань у таблиці - «і ще N», а не сотні кнопок', () => {
    const many = Array.from({ length: RowLinksPerTable + 5 }, (_, i) => issue(1, i + 1));
    show(rows(many));

    expect(screen.getAllByTestId('document-unmatched-row-link')).toHaveLength(RowLinksPerTable);
    expect(screen.getByTestId('document-unmatched-rows').textContent).toContain('⟦document.unmatchedRows.more (count=5)⟧');
  });

  it('час прогону - поруч із кількістю, коли відомий', () => {
    show(rows([issue(1, 1)], '2026-11-08T10:00:00Z'));

    expect(screen.getByTestId('document-unmatched-rows-summary').textContent).toContain('document.unmatchedRows.lastRun');
  });

  it('немає рядків - нічого не малюється', () => {
    show(rows([]));

    expect(screen.queryByTestId('document-unmatched-rows')).toBeNull();
  });

  it('b3: допустимі значення правил - перші 8 у тексті, решта «+N» у тултіпі; рядок - з поточним значенням', async () => {
    const codes = Array.from({ length: AllowedValuesShown + 3 }, (_, i) => `09${String(i).padStart(2, '0')}`);
    show({
      issues: [issue(1, 1, { rowKey: 'S01', currentValues: ['Sample A-0'] }), issue(1, 2, { rowKey: 'S02', currentValues: [''] })],
      hints: [
        {
          tableCode: 'T1',
          sheetCopyId: null,
          methodologyCodes: ['ECW_C11_11_02'],
          columns: [{ code: 'IDCodeNAEProject', nameL10n: { values: { en: 'ID Code - NAE Project' } } }],
          options: codes.map((code) => [code]),
        },
      ],
      finishedAt: null,
    });

    const allowed = screen.getByTestId('document-unmatched-rows-allowed').textContent ?? '';
    expect(allowed).toContain(
      `⟦document.unmatchedRows.allowed (column=ID Code - NAE Project, values=${codes.slice(0, AllowedValuesShown).join(', ')})⟧`,
    );
    expect(allowed).toContain('ECW_C11_11_02');
    const more = screen.getByTestId('document-unmatched-rows-allowed-more');
    expect(more.textContent).toBe('⟦document.unmatchedRows.allowedMore (count=3)⟧');
    await userEvent.hover(more);
    expect(await screen.findByText(codes.slice(AllowedValuesShown).join(', '))).toBeDefined();

    const links = screen.getAllByTestId('document-unmatched-row-link').map((l) => l.textContent);
    expect(links).toEqual([
      '⟦document.unmatchedRows.rowValue (row=1, value=Sample A-0)⟧',
      '⟦document.unmatchedRows.rowValue (row=2, value=⟦document.unmatchedRows.emptyValue⟧)⟧',
    ]);
  });
});

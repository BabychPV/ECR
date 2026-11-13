import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { issue, table } from '@/features/calculationIssues/__tests__/fixtures';
import {
  AllowedValuesShown,
  GroupsShown,
  plural,
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
  return { issues, hints: [], finishedAt, formulaErrors: [] };
}

function show(value: UnmatchedRows, onOpenRow = vi.fn(), userId: number | undefined = 7, narrow = false) {
  const tables = [table(1, '9.01 Unit 400'), table(2, '9.03 Unit 430')];
  const view = render(
    <MantineProvider theme={testTheme}>
      <UnmatchedRowsBanner userId={userId} rows={value} tables={tables} onOpenRow={onOpenRow} narrow={narrow} />
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
    expect(groups[0]).toContain('⟦document.unmatchedRows.group (table=Sheet · 9.01 Unit 400, rows=⟦document.unmatchedRows.rows.other (count=2)⟧)⟧');
    expect(groups[1]).toContain('⟦document.unmatchedRows.group (table=Sheet · 9.03 Unit 430, rows=⟦document.unmatchedRows.rows.other (count=2)⟧)⟧');
    expect(screen.getByTestId('document-unmatched-rows-summary').textContent).toContain(
      '⟦document.unmatchedRows.summary (rows=⟦document.unmatchedRows.rows.other (count=4)⟧, tables=⟦document.unmatchedRows.tables.other (count=2)⟧)⟧',
    );
    // Старий текст «Row: 1 (S01), 2 (S02), 1 (S01)…» - більше немає.
    expect(screen.getByTestId('document-unmatched-rows').textContent).not.toContain('(S01)');
  });

  it('b2: клік по рядку відкриває САМЕ цей рядок цієї таблиці', async () => {
    const { onOpenRow } = show(sixty);

    const links = screen.getAllByTestId('document-unmatched-row-link');
    expect(links).toHaveLength(4);
    await userEvent.click(links[3]!);

    expect(onOpenRow).toHaveBeenCalledExactlyOnceWith(102, 'S02', null);
  });

  // ✎ 10.11 (doc 95): рядок, відхилений правилом категорії, раніше давав лише «Calculation results · none». Тепер окрема
  // група з причиною — правило й колонка, яку треба заповнити, — і перехід одразу на цю комірку.
  // Мутація: передавати `null` замість `missingColumns[0].code` — червоніє перехід; прибрати гілку `rejected` у
  // `rowLabel` — червоніє підпис.
  it('10.11: відхилений правилом категорії рядок називає правило й колонку і відкриває саме цю комірку', async () => {
    const { onOpenRow } = show(
      rows([
        issue(1, 1, {
          rowKey: 'R1',
          kind: 'CategoryRuleRejected',
          methodologyCode: 'ECW_C05_01',
          ruleCode: 'R_C05_01_01',
          columns: [{ code: 'Land_OverhauledEnginesRef', nameL10n: { values: { en: 'Overhauled engines' } } }],
        }),
      ]),
    );
    await userEvent.click(screen.getByTestId('document-unmatched-rows-toggle'));

    expect(screen.getByTestId('document-unmatched-rows-group').textContent).toContain(
      '⟦document.rejectedRows.group (table=Sheet · 9.01 Unit 400, rows=⟦document.unmatchedRows.rows.one (count=1)⟧)⟧',
    );
    expect(screen.getByTestId('document-rejected-rows-hint').textContent).toBe('⟦document.rejectedRows.hint⟧');
    // Підказка «без правила» (змінити код) тут неправдива — її немає.
    expect(screen.queryByTestId('document-unmatched-rows-hint')).toBeNull();

    const link = screen.getByTestId('document-rejected-row-link');
    expect(link.textContent).toBe('⟦document.rejectedRows.rowFill (row=1, rule=R_C05_01_01, columns=«Overhauled engines»)⟧');
    await userEvent.click(link);

    expect(onOpenRow).toHaveBeenCalledExactlyOnceWith(101, 'R1', 'Land_OverhauledEnginesRef');
  });

  // AN-164 (doc 96, LTD-A41): рядок закрито правилом, але формулам бракує входів — раніше тихий нуль без проблеми. Тепер
  // своя група «бракує вихідних даних» з правилом і колонками, які треба заповнити, і перехід на першу з них.
  // Мутація: не розпізнавати `MissingInputs` у `groupUnmatchedRows` — рядок іде як «без правила», тест червоний.
  it('AN-164: рядок без входів формул — своя група з правилом і колонками до заповнення', async () => {
    const { onOpenRow } = show(
      rows([
        issue(1, 2, {
          rowKey: 'R2',
          kind: 'MissingInputs',
          methodologyCode: 'ECW_C12_02',
          ruleCode: 'R_C12_02_01',
          columns: [{ code: 'MaximumConsumption_M09', nameL10n: { values: { en: 'Max consumption' } } }],
        }),
      ]),
    );
    await userEvent.click(screen.getByTestId('document-unmatched-rows-toggle'));

    expect(screen.getByTestId('document-unmatched-rows-group').textContent).toContain(
      '⟦document.missingInputRows.group (table=Sheet · 9.01 Unit 400, rows=⟦document.unmatchedRows.rows.one (count=1)⟧)⟧',
    );
    expect(screen.getByTestId('document-missing-input-rows-hint').textContent).toBe('⟦document.missingInputRows.hint⟧');
    expect(screen.queryByTestId('document-rejected-rows-hint')).toBeNull();
    expect(screen.queryByTestId('document-unmatched-rows-hint')).toBeNull();

    const link = screen.getByTestId('document-rejected-row-link');
    expect(link.textContent).toBe('⟦document.rejectedRows.rowFill (row=2, rule=R_C12_02_01, columns=«Max consumption»)⟧');
    await userEvent.click(link);

    expect(onOpenRow).toHaveBeenCalledExactlyOnceWith(101, 'R2', 'MaximumConsumption_M09');
  });

  // AN-164b (аудит RC #825, doc 96 @202610): «Running hours per month (hour)» ×12 без місяця. Сервер дає колонку місяця
  // періоду з назвою місяця; банер показує кожну колонку один раз, вхід поза колонками — підписом каталогу, а перехід
  // веде на першу СПРАВЖНЮ колонку. Мутації: прибрати відсів повторів у `missingColumnsOf` — червоніє «один раз»;
  // брати `missingColumns[0]` для переходу — червоніє перехід (вхід `Land_Density` не колонка).
  it('AN-164b: кожна колонка один раз, місяць у назві, вхід поза колонками — підписом', async () => {
    const { onOpenRow } = show(
      rows([
        issue(1, 6, {
          rowKey: 'S1_6',
          kind: 'MissingInputs',
          methodologyCode: 'ECW_C09_02_01',
          ruleCode: 'R_C09_02_01_01',
          columns: [
            { code: 'Land_Density', nameL10n: { values: {} } },
            { code: 'Hours_M10', nameL10n: { values: { en: 'Running hours per month (hour) · Oct' } } },
            { code: 'hours_m10', nameL10n: { values: { en: 'Running hours per month (hour) · Oct' } } },
          ],
        }),
      ]),
    );
    await userEvent.click(screen.getByTestId('document-unmatched-rows-toggle'));

    const link = screen.getByTestId('document-rejected-row-link');
    expect(link.textContent).toBe(
      '⟦document.rejectedRows.rowFill (row=6, rule=R_C09_02_01_01, columns=⟦document.missingInputRows.argument (code=Land_Density)⟧, «Running hours per month (hour) · Oct»)⟧',
    );
    await userEvent.click(link);

    expect(onOpenRow).toHaveBeenCalledExactlyOnceWith(101, 'S1_6', 'Hours_M10');
  });

  it('b3: пояснення «чому і як» - один раз, тон інформації (status), не помилки', () => {
    show(sixty);

    const text = screen.getByTestId('document-unmatched-rows').textContent ?? '';
    expect(text.split('document.unmatchedRows.hint').length - 1).toBe(1);
    expect(screen.getByTestId('document-unmatched-rows-summary').getAttribute('role')).toBe('status');
  });

  it('fg6 F13: за замовчуванням (чистий профіль) банер ЗГОРНУТИЙ - лише рядок підсумку', () => {
    show(sixty);

    expect(screen.getByTestId('document-unmatched-rows-toggle').getAttribute('aria-expanded')).toBe('false');
    expect(screen.getByTestId('document-unmatched-rows-summary').textContent).toContain('⟦document.unmatchedRows.rows.other (count=4)⟧');
  });

  it('fg6 F13: розгорнутий - видно 3 таблиці, решта за «Show all N tables»', async () => {
    const tables5 = Array.from({ length: 5 }, (_, i) => issue(i + 1, 1));
    render(
      <MantineProvider theme={testTheme}>
        <UnmatchedRowsBanner
          userId={7}
          rows={rows(tables5)}
          tables={Array.from({ length: 5 }, (_, i) => table(i + 1, `T${String(i + 1)} name`))}
          onOpenRow={vi.fn()}
        />
      </MantineProvider>,
    );
    await userEvent.click(screen.getByTestId('document-unmatched-rows-toggle'));

    expect(screen.getAllByTestId('document-unmatched-rows-group')).toHaveLength(GroupsShown);
    const all = screen.getByTestId('document-unmatched-rows-all-tables');
    expect(all.textContent).toBe('⟦document.unmatchedRows.showAllTables (count=5)⟧');
    await userEvent.click(all);
    expect(screen.getAllByTestId('document-unmatched-rows-group')).toHaveLength(5);
    expect(screen.queryByTestId('document-unmatched-rows-all-tables')).toBeNull();
  });

  it('b4: розгорнутий і знову згорнутий банер лишається згорнутим після перемонтування; кількість видно', async () => {
    const first = show(sixty);
    const toggle = screen.getByTestId('document-unmatched-rows-toggle');
    await userEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(window.localStorage.getItem(`${UnmatchedCollapsedKey}:7`)).toBe('0');

    await userEvent.click(toggle);
    expect(window.localStorage.getItem(`${UnmatchedCollapsedKey}:7`)).toBe('1');
    first.unmount();

    show(sixty);
    expect(screen.getByTestId('document-unmatched-rows-toggle').getAttribute('aria-expanded')).toBe('false');
    expect(screen.getByTestId('document-unmatched-rows-summary').textContent).toContain('⟦document.unmatchedRows.rows.other (count=4)⟧');
  });

  it('fg6: вибір пам\'ятається для КОЖНОГО користувача - розгорнув один, інший бачить згорнутим', async () => {
    const first = show(sixty, vi.fn(), 7);
    await userEvent.click(screen.getByTestId('document-unmatched-rows-toggle'));
    first.unmount();

    show(sixty, vi.fn(), 7);
    expect(screen.getByTestId('document-unmatched-rows-toggle').getAttribute('aria-expanded')).toBe('true');
    cleanup();

    show(sixty, vi.fn(), 8);
    expect(screen.getByTestId('document-unmatched-rows-toggle').getAttribute('aria-expanded')).toBe('false');
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

  it('b4: недоступне сховище не ламає банер - згорнутий за замовчуванням (fg6 F13)', () => {
    const spy = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied');
    });
    try {
      show(sixty);
      expect(screen.getByTestId('document-unmatched-rows-toggle').getAttribute('aria-expanded')).toBe('false');
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

  it('вузький екран: перемикач не стискається (кнопка «Show rows» не обрізається до «Shov»)', () => {
    show(sixty);

    const toggle = screen.getByTestId('document-unmatched-rows-toggle');
    expect(getComputedStyle(toggle).flexShrink).toBe('0');
    // ⚠ З `preventGrowOverflow` (типово) Group Mantine дає дітям max-width частки рядка — саме це й різало кнопку.
    expect(toggle.parentElement?.getAttribute('style') ?? '').not.toContain('--group-child-width');
  });

  it('свіжий погляд 08.11: вузький екран (лише читання) - підказка без F9, «відкрийте ширше»', () => {
    show(sixty, vi.fn(), 7, true);

    expect(screen.getByTestId('document-unmatched-rows-hint').textContent).toBe('⟦document.unmatchedRows.hintNarrow⟧');
  });

  it('свіжий погляд 08.11: широкий екран - підказка з дією F9 (ключ hint)', () => {
    show(sixty);

    expect(screen.getByTestId('document-unmatched-rows-hint').textContent).toBe('⟦document.unmatchedRows.hint⟧');
  });

  it('свіжий погляд 08.11: посилання рядка називає таблицю для читалки («Sheet · 9.03 Unit 430 · Row 2»)', () => {
    show(sixty);

    const links = screen.getAllByTestId('document-unmatched-row-link');
    expect(links[3]!.getAttribute('aria-label')).toBe('Sheet · 9.03 Unit 430 · ⟦document.unmatchedRows.row (row=2)⟧');
    // ⚠ Видимий текст — частина доступної назви (WCAG 2.5.3).
    expect(links[3]!.getAttribute('aria-label')).toContain(links[3]!.textContent ?? '');
  });

  it('свіжий погляд 08.11: кількість словом - one для 1, other для решти', () => {
    expect(plural('document.unmatchedRows.rows', 1)).toBe('⟦document.unmatchedRows.rows.one (count=1)⟧');
    expect(plural('document.unmatchedRows.rows', 3)).toBe('⟦document.unmatchedRows.rows.other (count=3)⟧');
  });

  it('немає рядків - нічого не малюється', () => {
    show(rows([]));

    expect(screen.queryByTestId('document-unmatched-rows')).toBeNull();
  });

  it('b3: допустимі значення правил - перші 8 у тексті, решта за кнопкою «+N»; рядок - з поточним значенням', async () => {
    const codes = Array.from({ length: AllowedValuesShown + 3 }, (_, i) => `09${String(i).padStart(2, '0')}`);
    show({
      formulaErrors: [],
      issues: [issue(1, 1, { rowKey: 'S01', currentValues: ['Sample A-0'] }), issue(1, 2, { rowKey: 'S02', currentValues: [''] })],
      hints: [
        {
          tableCode: 'T1',
          sheetCopyId: null,
          methodologies: [{ code: 'ECW_C11_11_02', nameL10n: { values: { en: 'Cooling medium' } } }],
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
    // Свіжий погляд 08.11: методологія підписана («Methodology: …»), а посилання рядків розділені видимою « · ».
    expect(allowed).toContain('⟦document.unmatchedRows.methodology (name=Cooling medium)⟧');
    const group = screen.getByTestId('document-unmatched-rows-group');
    expect(group.textContent).toContain('value=Sample A-0)⟧·⟦document.unmatchedRows.rowValue (row=2');
    expect(allowed).not.toContain('ECW_C11_11_02');
    const more = screen.getByTestId('document-unmatched-rows-allowed-more');
    expect(more.textContent).toBe('⟦document.unmatchedRows.allowedMore (count=3)⟧');
    await userEvent.click(more);
    expect(screen.getByTestId('document-unmatched-rows-allowed').textContent).toContain(`values=${codes.join(', ')})⟧`);
    expect(screen.queryByTestId('document-unmatched-rows-allowed-more')).toBeNull();

    const links = screen.getAllByTestId('document-unmatched-row-link').map((l) => l.textContent);
    expect(links).toEqual([
      '⟦document.unmatchedRows.rowValue (row=1, value=Sample A-0)⟧',
      '⟦document.unmatchedRows.rowValue (row=2, value=⟦document.unmatchedRows.emptyValue⟧)⟧',
    ]);
  });
});

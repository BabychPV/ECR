import axe from 'axe-core';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { theme } from '@/shared/theme/theme';
import { SourceImpactModal } from '../SourceImpactModal';
import type { SourceImpact } from '../sourceImpact';

/**
 * B80 N5 (D-554): діалог впливу дії над джерелом на залежні документи — за політикою зв'язки: `Warn` (підтвердження),
 * `ConfirmWithReason` (обов'язкова причина), `Block` (пояснення без кнопки підтвердження).
 *
 * ⚠ Каталогу в тесті немає: підписи — ключі в маркерах (`⟦sourceImpact.confirm.reopen⟧`).
 */

afterEach(cleanup);

function impact(overrides: Partial<SourceImpact> = {}): SourceImpact {
  return {
    action: 'reopen',
    policy: 'Warn',
    required: true,
    blocked: false,
    reasonRequired: false,
    impactHash: 'abc123',
    sourceMonths: [202603],
    dependentCount: 2,
    approvedCount: 1,
    canRecalculateApproved: false,
    wholeYearReason: null,
    consumers: [
      { bindingCode: 'HSE400_LAND', templateCode: 'LAND', documentId: 5, projectCode: 'LAND-2026', documentKey: 'DOC-LAND', periodKey: 202603, approved: true, willRecalculate: false, skipReason: 'approved', sheets: [], reason: 'sourceMonth', readsPeriodKey: null },
      { bindingCode: 'HSE400_LAND', templateCode: 'LAND', documentId: 6, projectCode: 'LAND-2026', documentKey: 'DOC-LAND-2', periodKey: 202603, approved: false, willRecalculate: true, skipReason: null, sheets: [], reason: 'sourceMonth', readsPeriodKey: null },
    ],
    ...overrides,
  };
}

function show(value: SourceImpact | null, onConfirm = vi.fn(), onClose = vi.fn(), action: 'approve' | 'reopen' | 'edit' = 'reopen') {
  render(
    <MantineProvider theme={theme}>
      <SourceImpactModal opened action={action} impact={value} onConfirm={onConfirm} onClose={onClose} />
    </MantineProvider>,
  );

  return { onConfirm, onClose };
}

describe('SourceImpactModal', () => {
  it('✎ огляд L2: закритий період — «закрито»; незатверджений аркуш періоду, що НЕ перераховується, — причина періоду, а не «затверджено»', async () => {
    const base = impact().consumers[1]!;
    show(
      impact({
        consumers: [
          { ...base, periodKey: 202601, approved: true, willRecalculate: false, skipReason: 'closed' },
          {
            ...base,
            periodKey: 202602,
            willRecalculate: false,
            skipReason: 'dependentsOff',
            sheets: [
              { sheetDefId: 1, sheetCode: 'SH_APPROVED', approved: true, willRecalculate: false },
              { sheetDefId: 2, sheetCode: 'SH_DRAFT', approved: false, willRecalculate: false },
            ],
          },
        ],
      }),
    );

    await screen.findByRole('dialog');
    expect(screen.getAllByText('⟦sourceImpact.status.closed⟧')).toHaveLength(1);
    const sheets = screen.getByTestId('source-impact-sheets').textContent ?? '';
    expect(sheets).toContain('SH_APPROVED · ⟦sourceImpact.status.approved⟧');
    expect(sheets).toContain('SH_DRAFT · ⟦sourceImpact.status.dependentsOff⟧');
  });

  it('✎ кожен період каже, ЧОМУ він у переліку: власник зміненого місяця, міжмісячна формула (який місяць читає), увесь рік — з причиною', async () => {
    const base = impact().consumers[1]!;
    show(
      impact({
        wholeYearReason: 'sourceSpreadsMonths',
        consumers: [
          { ...base, periodKey: 202601 },
          { ...base, periodKey: 202602, reason: 'crossMonth', readsPeriodKey: 202601 },
          { ...base, periodKey: 202603, reason: 'sourceYear', willRecalculate: false, skipReason: 'dependentsOff' },
        ],
      }),
      vi.fn(),
      vi.fn(),
      'edit',
    );

    await screen.findByRole('dialog');
    const why = screen.getAllByTestId('source-impact-why').map((e) => e.textContent);
    expect(why[0]).toBe('⟦sourceImpact.why.sourceMonth⟧');
    expect(why[1]).toContain('sourceImpact.why.crossMonth (period=');
    expect(why[2]).toBe('⟦sourceImpact.why.sourceYear⟧');
    expect(screen.getByTestId('source-impact-whole-year').textContent).toBe('⟦sourceImpact.wholeYear.sourceSpreadsMonths⟧');
    expect(screen.getAllByText('⟦sourceImpact.status.dependentsOff⟧')).toHaveLength(1);
  });

  it('Warn: перелік залежних зі статусом, підтвердження одним кліком несе відбиток без причини', async () => {
    const { onConfirm } = show(impact());

    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('DOC-LAND');
    expect(dialog.textContent).toContain('DOC-LAND-2');
    expect(screen.getAllByText('⟦sourceImpact.status.approved⟧')).toHaveLength(1);
    expect(screen.getAllByText('⟦sourceImpact.status.pending⟧')).toHaveLength(1);
    expect(screen.queryByTestId('source-impact-reason')).toBeNull();

    fireEvent.click(screen.getByTestId('source-impact-confirm'));

    expect(onConfirm).toHaveBeenCalledWith({ hash: 'abc123', reason: null }, { suppress: false });
  });

  it('D-1: період із кількома аркушами показує, які аркуші затверджені (не перераховуються), а які будуть перераховані', async () => {
    const base = impact().consumers[1]!;
    show(
      impact({
        consumers: [
          {
            ...base,
            sheets: [
              { sheetDefId: 1, sheetCode: 'SH_APPROVED', approved: true, willRecalculate: false },
              { sheetDefId: 2, sheetCode: 'SH_DRAFT', approved: false, willRecalculate: true },
            ],
          },
        ],
      }),
    );

    const sheets = await screen.findByTestId('source-impact-sheets');
    expect(sheets.textContent).toContain('SH_APPROVED');
    expect(sheets.textContent).toContain('SH_DRAFT');
    // Стан аркуша — не лише кольором (a11y): текстом поруч із кодом.
    expect(sheets.textContent).toContain('SH_APPROVED · ⟦sourceImpact.status.approved⟧');
    expect(sheets.textContent).toContain('SH_DRAFT · ⟦sourceImpact.status.pending⟧');
  });

  it('D-7: частково затверджений період має бейдж «partial», а не «буде перерахований»; повністю затверджений і повністю відкритий — як раніше', async () => {
    const base = impact().consumers[1]!;
    const sheet = (id: number, code: string, ok: boolean) => ({ sheetDefId: id, sheetCode: code, approved: !ok, willRecalculate: ok });
    show(
      impact({
        consumers: [
          { ...base, documentId: 11, sheets: [sheet(1, 'A', false), sheet(2, 'B', true)] },
          { ...base, documentId: 12, willRecalculate: false, approved: true, sheets: [sheet(3, 'C', false), sheet(4, 'D', false)] },
          { ...base, documentId: 13, sheets: [sheet(5, 'E', true), sheet(6, 'F', true)] },
        ],
      }),
    );

    await screen.findByRole('dialog');
    expect(screen.getAllByText('⟦sourceImpact.status.partial⟧')).toHaveLength(1);
    // Період-рівневі бейджі: 1 partial, 1 approved, 1 pending (бейджі аркушів мають суфікс « · ... » у складі Group, тож шукаємо точний текст).
    expect(screen.getAllByText('⟦sourceImpact.status.approved⟧')).toHaveLength(1);
    expect(screen.getAllByText('⟦sourceImpact.status.pending⟧')).toHaveLength(1);
  });

  it('D-1: період з одним аркушем не показує бейджів аркушів (вони лише для кількох)', async () => {
    const base = impact().consumers[1]!;
    show(
      impact({
        consumers: [{ ...base, sheets: [{ sheetDefId: 1, sheetCode: 'SH_ONLY', approved: false, willRecalculate: true }] }],
      }),
    );

    await screen.findByRole('dialog');
    expect(screen.queryByTestId('source-impact-sheets')).toBeNull();
    expect(screen.queryByText(/SH_ONLY/)).toBeNull();
  });

  it('D-1: понад 20 аркушів — перші 20 бейджів і «+N» для решти', async () => {
    const base = impact().consumers[1]!;
    const sheets = Array.from({ length: 23 }, (_, i) => ({
      sheetDefId: i + 1,
      sheetCode: `SH_${String(i + 1).padStart(2, '0')}`,
      approved: false,
      willRecalculate: true,
    }));
    show(impact({ consumers: [{ ...base, sheets }] }));

    const list = await screen.findByTestId('source-impact-sheets');
    expect(list.textContent).toContain('SH_20');
    expect(list.textContent).not.toContain('SH_21');
    expect(screen.getByTestId('source-impact-sheets-more').textContent).toContain('sourceImpact.sheets.more');
  });

  it('ConfirmWithReason: кнопка вимкнена, доки причина порожня; причина йде обрізаною', async () => {
    const { onConfirm } = show(impact({ policy: 'ConfirmWithReason', reasonRequired: true }));

    const confirm = await screen.findByTestId('source-impact-confirm');
    expect((confirm as HTMLButtonElement).disabled).toBe(true);

    const reason = screen.getByLabelText(/sourceImpact\.reason\.label/);
    fireEvent.change(reason, { target: { value: '   ' } });
    expect((screen.getByTestId('source-impact-confirm') as HTMLButtonElement).disabled).toBe(true);

    fireEvent.change(reason, { target: { value: '  Акт 12  ' } });
    fireEvent.click(screen.getByTestId('source-impact-confirm'));

    expect(onConfirm).toHaveBeenCalledWith({ hash: 'abc123', reason: 'Акт 12' }, { suppress: false });
  });

  it('Block: пояснення відмови, кнопки підтвердження немає', async () => {
    const { onConfirm } = show(impact({ policy: 'Block', blocked: true }));

    expect(await screen.findByTestId('source-impact-blocked')).toBeTruthy();
    expect(screen.queryByTestId('source-impact-confirm')).toBeNull();
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it('required = false (політика None чи немає затверджених): лише відомості, кнопка — дія, без причини', async () => {
    const { onConfirm } = show(impact({ policy: 'None', required: false, approvedCount: 0 }), vi.fn(), vi.fn(), 'approve');

    expect(await screen.findByTestId('source-impact-policy-info')).toBeTruthy();
    expect(screen.queryByTestId('source-impact-reason')).toBeNull();
    expect(screen.getByTestId('source-impact-confirm').textContent).toContain('workflow.approve');

    fireEvent.click(screen.getByTestId('source-impact-confirm'));

    expect(onConfirm).toHaveBeenCalledWith({ hash: 'abc123', reason: null }, { suppress: false });
  });

  it('Cancel закриває діалог і не підтверджує', async () => {
    const { onConfirm, onClose } = show(impact());

    fireEvent.click(await screen.findByTestId('source-impact-cancel'));

    expect(onClose).toHaveBeenCalled();
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it('a11y: діалог названий, поле причини підписане, фокус на Cancel (L6), axe без порушень', async () => {
    show(impact({ policy: 'ConfirmWithReason', reasonRequired: true }), vi.fn(), vi.fn(), 'approve');

    const dialog = await screen.findByRole('dialog');
    expect(dialog.getAttribute('aria-labelledby')).toBeTruthy();
    expect(screen.getByLabelText(/sourceImpact\.reason\.label/)).toBeTruthy();
    expect(document.activeElement).toBe(screen.getByTestId('source-impact-cancel'));

    const result = await axe.run(dialog, { rules: { 'color-contrast': { enabled: false } } });
    expect(result.violations.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
  });

  it('impact = null — нічого не малює', () => {
    show(null);

    expect(screen.queryByRole('dialog')).toBeNull();
  });
});

describe('SourceImpactModal: перекриття «й затверджені» і правка (рішення 19.11)', () => {
  it('галочка «й затверджені» — лише з правом і коли є затверджені залежні; з нею причина обов’язкова', async () => {
    const { onConfirm } = show(impact({ canRecalculateApproved: true }));

    expect(await screen.findByTestId('source-impact-approved')).toBeTruthy();
    expect(screen.queryByTestId('source-impact-reason')).toBeNull();
    fireEvent.click(screen.getByTestId('source-impact-approved'));
    expect((screen.getByTestId('source-impact-confirm') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(screen.getByLabelText(/sourceImpact\.reason\.label/), { target: { value: ' Акт 12 ' } });
    fireEvent.click(screen.getByTestId('source-impact-confirm'));

    expect(onConfirm).toHaveBeenCalledWith(
      { hash: 'abc123', reason: 'Акт 12', recalculateApproved: true },
      { suppress: false },
    );
  });

  it('без права або без затверджених залежних галочки немає; за Block — теж', async () => {
    show(impact({ canRecalculateApproved: false }));
    expect(await screen.findByRole('dialog')).toBeTruthy();
    expect(screen.queryByTestId('source-impact-approved')).toBeNull();
    cleanup();

    show(impact({ canRecalculateApproved: true, approvedCount: 0 }));
    expect(await screen.findByRole('dialog')).toBeTruthy();
    expect(screen.queryByTestId('source-impact-approved')).toBeNull();
    cleanup();

    show(impact({ canRecalculateApproved: true, policy: 'Block', blocked: true }));
    expect(await screen.findByTestId('source-impact-blocked')).toBeTruthy();
    expect(screen.queryByTestId('source-impact-approved')).toBeNull();
  });

  it('F13: при повторному відкритті причина й галочка «й затверджені» очищені — причина попередньої дії не підписує наступну', async () => {
    const value = impact({ canRecalculateApproved: true, policy: 'ConfirmWithReason', reasonRequired: true });
    const view = (opened: boolean) => (
      <MantineProvider theme={theme}>
        <SourceImpactModal opened={opened} action="reopen" impact={value} onConfirm={vi.fn()} onClose={vi.fn()} />
      </MantineProvider>
    );
    const { rerender } = render(view(true));

    fireEvent.click(await screen.findByTestId('source-impact-approved'));
    fireEvent.change(screen.getByLabelText(/sourceImpact\.reason\.label/), { target: { value: 'стара причина' } });
    expect((screen.getByLabelText(/sourceImpact\.reason\.label/) as HTMLTextAreaElement).value).toBe('стара причина');

    rerender(view(false));
    rerender(view(true));

    await waitFor(() => expect((screen.getByLabelText(/sourceImpact\.reason\.label/) as HTMLTextAreaElement).value).toBe(''));
    expect((screen.getByTestId('source-impact-approved') as HTMLInputElement).checked).toBe(false);
  });

  it('правка: «Continue editing» і «не питати до кінця сесії» передаються в extras', async () => {
    const { onConfirm } = show(impact({ action: 'edit', required: false, policy: 'None' }), vi.fn(), vi.fn(), 'edit');

    fireEvent.click(await screen.findByTestId('source-impact-suppress'));
    expect(screen.getByTestId('source-impact-confirm').textContent).toContain('sourceImpact.confirm.edit');
    fireEvent.click(screen.getByTestId('source-impact-confirm'));

    expect(onConfirm).toHaveBeenCalledWith({ hash: 'abc123', reason: null }, { suppress: true });
  });

  it('Поки дія виконується (isPending), повторне натискання не підтверджує вдруге', async () => {
    // F13 (мутація: прибрати `&& isPending !== true` з обробника кнопки): до порогу usePendingLoading Mantine не
    // блокує кнопку, тож без цього захисту швидкий подвійний клік слав би підтвердження двічі.
    const onConfirm = vi.fn();
    render(
      <MantineProvider theme={theme}>
        <SourceImpactModal opened action="reopen" impact={impact()} isPending onConfirm={onConfirm} onClose={vi.fn()} />
      </MantineProvider>,
    );

    fireEvent.click(await screen.findByTestId('source-impact-confirm'));
    fireEvent.click(screen.getByTestId('source-impact-confirm'));

    expect(onConfirm).not.toHaveBeenCalled();
  });
});

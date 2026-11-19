import axe from 'axe-core';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
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
    consumers: [
      { bindingCode: 'HSE400_LAND', templateCode: 'LAND', documentId: 5, projectCode: 'LAND-2026', documentKey: 'DOC-LAND', periodKey: 202603, approved: true, willRecalculate: false, skipReason: 'approved' },
      { bindingCode: 'HSE400_LAND', templateCode: 'LAND', documentId: 6, projectCode: 'LAND-2026', documentKey: 'DOC-LAND-2', periodKey: 202603, approved: false, willRecalculate: true, skipReason: null },
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
  it('Warn: перелік залежних зі статусом, підтвердження одним кліком несе відбиток без причини', async () => {
    const { onConfirm } = show(impact());

    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('DOC-LAND');
    expect(dialog.textContent).toContain('DOC-LAND-2');
    expect(screen.getAllByText('⟦sourceImpact.status.approved⟧')).toHaveLength(1);
    expect(screen.getAllByText('⟦sourceImpact.status.pending⟧')).toHaveLength(1);
    expect(screen.queryByTestId('source-impact-reason')).toBeNull();

    fireEvent.click(screen.getByTestId('source-impact-confirm'));

    expect(onConfirm).toHaveBeenCalledWith({ hash: 'abc123', reason: null });
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

    expect(onConfirm).toHaveBeenCalledWith({ hash: 'abc123', reason: 'Акт 12' });
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

    expect(onConfirm).toHaveBeenCalledWith({ hash: 'abc123', reason: null });
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

  it('правка: «Continue editing» і «не питати до кінця сесії» передаються в extras', async () => {
    const { onConfirm } = show(impact({ action: 'edit', required: false, policy: 'None' }), vi.fn(), vi.fn(), 'edit');

    fireEvent.click(await screen.findByTestId('source-impact-suppress'));
    expect(screen.getByTestId('source-impact-confirm').textContent).toContain('sourceImpact.confirm.edit');
    fireEvent.click(screen.getByTestId('source-impact-confirm'));

    expect(onConfirm).toHaveBeenCalledWith({ hash: 'abc123', reason: null }, { suppress: true });
  });
});

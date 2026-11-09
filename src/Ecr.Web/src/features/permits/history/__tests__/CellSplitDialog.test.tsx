import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { EcrApiError } from '@/api/client';
import { renderWithQuery } from '@/test/render';
import { CellSplitDialog } from '../CellSplitDialog';
import { permitGridMarksOf } from '../splitModel';
import { historyOf, seaWaterRow } from './historyFixture';

/**
 * Feature (j) j2dialog: part A pre-filled with the old single value, part B typed by the user, a live total A + B,
 * Save sends exactly the typed parts (never pro-rata) with the row version; a refusal stays inside the dialog.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    params === undefined ? key : `${key} ${JSON.stringify(params)}`,
}));

const saveCellSplits = vi.fn();
vi.mock('@/features/permits/history/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/features/permits/history/api')>()),
  saveCellSplits: (...args: unknown[]) => saveCellSplits(...args) as unknown,
}));

afterEach(() => {
  cleanup();
  saveCellSplits.mockReset();
});

const target = { documentId: 3, periodKey: 202602, tableInstanceId: 41, tableDefId: 701, sheetCopyId: null, rowKey: 'R1', columnCode: 'Feb' };

function open(onClose = vi.fn()): { onClose: typeof onClose; dialog: HTMLElement } {
  const mark = permitGridMarksOf(historyOf(seaWaterRow()), 701, null).cells.get('R1:Feb') ?? null;
  renderWithQuery(
    <CellSplitDialog
      target={target}
      mark={mark}
      historyPending={false}
      rowLabel="Sea Water Intake"
      unit="thousand m³"
      baseVersion="AAAB"
      onClose={onClose}
    />,
  );

  return { onClose, dialog: screen.getByRole('dialog') };
}

function parts(dialog: HTMLElement): HTMLInputElement[] {
  return within(dialog).getAllByRole('textbox') as HTMLInputElement[];
}

describe('CellSplitDialog (j2)', () => {
  it('names the row and month, explains why, labels each part with its permit and days', () => {
    const { dialog } = open();

    expect(screen.getByText(/permits\.split\.title .*"row":"Sea Water Intake"/)).toBeTruthy();
    expect(dialog.querySelector('[data-split-why]')?.textContent).toContain('"date":"15.02.2026"');
    const [a, b] = parts(dialog);
    expect(a?.getAttribute('aria-describedby')).toBeTruthy();
    expect(within(dialog).getByLabelText(/KZ29VCZ14622425/)).toBe(a);
    expect(within(dialog).getByLabelText(/KZ46VCZ14622410/)).toBe(b);
    expect(dialog.textContent).toContain('"days":"01.02–14.02.2026","count":14');
  });

  it('part A starts with the old value 12.4, B empty; the total waits for B, then shows A + B live', () => {
    const { dialog } = open();
    const [a, b] = parts(dialog);

    expect(a?.value).toBe('12.4');
    expect(b?.value).toBe('');
    expect(dialog.querySelector('[data-split-total]')?.textContent).toContain('permits.split.totalPending');

    fireEvent.change(a!, { target: { value: '5.8' } });
    fireEvent.change(b!, { target: { value: '6.6' } });

    const total = dialog.querySelector('[data-split-total]')?.textContent ?? '';
    expect(total).toContain('"parts":"5.8 + 6.6"');
    expect(total).toContain('"total":"12.4"');
  });

  it('Save sends exactly the typed parts and the row version; closes on success', async () => {
    saveCellSplits.mockResolvedValue(historyOf());
    const { dialog, onClose } = open();
    const [a, b] = parts(dialog);
    fireEvent.change(a!, { target: { value: '5.8' } });
    fireEvent.change(b!, { target: { value: '6.6' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'permits.split.save' }));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(saveCellSplits).toHaveBeenCalledWith(3, {
      periodKey: 202602,
      items: [{ tableInstanceId: 41, rowKey: 'R1', columnCode: 'Feb', baseVersion: 'AAAB', parts: ['5.8', '6.6'] }],
    });
  });

  it('a non-number blocks Save with a field error', () => {
    const { dialog } = open();
    fireEvent.change(parts(dialog)[1]!, { target: { value: 'abc' } });

    expect(dialog.textContent).toContain('permits.split.notNumber');
    expect((within(dialog).getByRole('button', { name: 'permits.split.save' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('a refusal stays in the dialog (no close)', async () => {
    saveCellSplits.mockRejectedValue(
      new EcrApiError({ title: 'Bad', status: 422, errorCode: 'ECR-REQ-0422', correlationId: 'c', extensions2: { messageKey: 'err.ECR-REQ-0422.permitSplitInvalid' } }),
    );
    const { dialog, onClose } = open();
    fireEvent.change(parts(dialog)[1]!, { target: { value: '1' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'permits.split.save' }));

    await waitFor(() => expect(within(dialog).getByRole('alert')).toBeTruthy());
    expect(onClose).not.toHaveBeenCalled();
  });
});

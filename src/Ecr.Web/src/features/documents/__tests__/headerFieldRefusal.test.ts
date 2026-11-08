import { describe, expect, it } from 'vitest';
import { EcrApiError, type EcrProblem } from '@/api/client';
import { headerFieldRefusal } from '../headerFieldRefusal';
import { rejectionMarksOf } from '@/features/grid/saveErrors';
import type { PendingEdit } from '@/features/grid/useCellPatch';

const permitDetail =
  'Not saved: permit KZ35VTE00340032 in header field "Permit" is valid 01.03.2026 – 30.11.2026 and does not cover Jan 2026, ' +
  'where the document has data. Pick a permit valid in those months, clear the values in those months, or extend the permit\'s validity.';

function problem(overrides: Partial<EcrProblem> = {}): EcrProblem {
  return {
    title: 'The chosen registry entry cannot be used',
    status: 422,
    detail: permitDetail,
    errorCode: 'ECR-HDR-4223',
    correlationId: 'c1',
    extensions2: { messageKey: 'err.ECR-HDR-4223.permitNotCoveringMonths', headerFieldCode: 'Permit' },
    ...overrides,
  };
}

describe('Permit validity: header refusal under the named field (h1)', () => {
  it('shows the localized reason under the field the server names', () => {
    expect(headerFieldRefusal(new EcrApiError(problem()), 'Permit')).toBe(permitDetail);
  });

  it('other fields stay clean', () => {
    expect(headerFieldRefusal(new EcrApiError(problem()), 'Contractor')).toBeNull();
  });

  it('a refusal that names no field is left to the general alert', () => {
    expect(
      headerFieldRefusal(new EcrApiError(problem({ extensions2: { messageKey: 'err.ECR-HDR-4223.permitNotCoveringMonths' } })), 'Permit'),
    ).toBeNull();
  });

  it('a raw developer sentence (no messageKey) is never shown — the catalog title is', () => {
    const raw = new EcrApiError(problem({ detail: 'Дозвіл не покриває місяців', extensions2: { headerFieldCode: 'Permit' } }));

    expect(headerFieldRefusal(raw, 'Permit')).toBe('The chosen registry entry cannot be used');
  });

  it('not our error — nothing', () => {
    expect(headerFieldRefusal(new TypeError('Failed to fetch'), 'Permit')).toBeNull();
  });
});

describe('Permit validity: grid refusal marks the named cells with the reason (h2/h3)', () => {
  it('a value in an uncovered month: the month cell keeps the edit and the full reason', () => {
    const detail = 'Not saved: permit KZ35VTE00340032 is valid 01.03.2026 – 30.11.2026 and does not cover Feb 2026, where this row has data.';
    const error = new EcrApiError(
      problem({
        detail,
        errorCode: 'ECR-CELL-4223',
        extensions2: {
          messageKey: 'err.ECR-CELL-4223.permitNotCoveringMonths',
          cells: [{ rowKey: 'R7001000', columnCode: 'February', entryId: 7407 }],
        },
      }),
    );
    const attempted = [
      { rowKey: 'R7001000', columnCode: 'February', value: '7' },
      { rowKey: 'R7001001', columnCode: 'March', value: '3' },
    ] as unknown as PendingEdit[];

    const marks = rejectionMarksOf(error, attempted);

    expect(marks).toHaveLength(1);
    expect(marks[0]?.edit.columnCode).toBe('February');
    expect(marks[0]?.message).toBe(detail);
  });
});

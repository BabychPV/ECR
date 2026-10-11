import { describe, it, expect } from 'vitest';
import { returnBlock, returnableRecipients, validateBlock } from '../ownerActions';
import type { AssignmentRecipient, OwnerAssignmentTarget, RecipientStatus } from '../types';

const recipient = (userId: number, status: RecipientStatus, isActive = true): AssignmentRecipient => ({
  userId,
  displayName: `user${String(userId)}`,
  status,
  isActive,
});

const target = (
  recipients: AssignmentRecipient[],
  validation: OwnerAssignmentTarget['validation'] = 'NotValidated',
): OwnerAssignmentTarget => ({ id: 7, sliceLabel: 'January', validation, version: 3, recipients });

describe('validateBlock', () => {
  it('дозволяє, коли всі активні виконавці Filled', () => {
    expect(validateBlock(target([recipient(1, 'Filled'), recipient(2, 'Filled')]))).toBeNull();
  });

  it('відмовляє, поки хтось Assigned або Returned', () => {
    expect(validateBlock(target([recipient(1, 'Filled'), recipient(2, 'Assigned')]))).toBe('notAllFilled');
    expect(validateBlock(target([recipient(1, 'Returned')]))).toBe('notAllFilled');
  });

  it('вимкнений виконавець не блокує', () => {
    expect(validateBlock(target([recipient(1, 'Filled'), recipient(2, 'Assigned', false)]))).toBeNull();
  });

  it('без активних виконавців - відмова noRecipients', () => {
    expect(validateBlock(target([]))).toBe('noRecipients');
    expect(validateBlock(target([recipient(1, 'Filled', false)]))).toBe('noRecipients');
  });

  it('уже Validated - відмова (кнопка не обіцяє нічого нового)', () => {
    expect(validateBlock(target([recipient(1, 'Filled')], 'Validated'))).toBe('alreadyValidated');
  });
});

describe('returnBlock / returnableRecipients', () => {
  it('повертаються лише активні Filled', () => {
    const row = target([recipient(1, 'Filled'), recipient(2, 'Assigned'), recipient(3, 'Filled', false)]);

    expect(returnableRecipients(row).map((r) => r.userId)).toEqual([1]);
    expect(returnBlock(row)).toBeNull();
  });

  it('Validated із Filled-виконавцями повернути можна', () => {
    expect(returnBlock(target([recipient(1, 'Filled')], 'Validated'))).toBeNull();
  });

  it('нікого повертати - відмова', () => {
    expect(returnBlock(target([recipient(1, 'Assigned'), recipient(2, 'Returned')]))).toBe('nothingToReturn');
  });
});

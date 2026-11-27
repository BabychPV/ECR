import { describe, expect, it } from 'vitest';
import { recalcBlockOf, serverEligibility } from '../recalcAvailability';

/**
 * ✎ Q3 координатора: рішення «можна перерахувати» — одна функція; поле сервера (контракт L3) має першість над правилом
 * клієнта, а до нього — правило сервера: блок лише над ЗАТВЕРДЖЕНИМ, річна розкладка — без якоря.
 */
describe('recalcBlockOf', () => {
  it('без поля сервера — правило: Submitted можна, Approved ні, усі Approved ні, рік — завжди можна', () => {
    expect(recalcBlockOf({ yearLayout: false, state: 'Submitted', sheetStates: { A: 'Submitted' } })).toBeNull();
    expect(recalcBlockOf({ yearLayout: false, state: 'Approved', sheetStates: { A: 'Approved', B: 'Draft' } })).toBe('sheetApproved');
    expect(recalcBlockOf({ yearLayout: false, state: '', sheetStates: { A: 'Approved', B: 'Approved' } })).toBe('allApproved');
    expect(recalcBlockOf({ yearLayout: true, state: 'Approved', sheetStates: { A: 'Approved' } })).toBeNull();
  });

  it('поле сервера є — рішення за ним, хоч би що казав стан аркуша', () => {
    expect(recalcBlockOf({ yearLayout: false, state: 'Approved', sheetStates: { A: 'Approved' }, serverCanRecalculate: true })).toBeNull();
    expect(recalcBlockOf({ yearLayout: true, state: 'Draft', sheetStates: {}, serverCanRecalculate: false })).toBe('sheetApproved');
    expect(recalcBlockOf({ yearLayout: false, state: 'Approved', sheetStates: {}, serverCanRecalculate: null })).toBe('sheetApproved');
  });

  it('серверна відповідь calc-status: є місяць, який перерахується, — «можна»; лише закриті — «ні» з причиною; застарілих немає — правило', () => {
    expect(serverEligibility([{ recalculable: false, lockedReason: 'closed' }, { recalculable: true, lockedReason: null }])).toEqual({ canRecalculate: true, lockedReason: null });
    const closed = serverEligibility([{ recalculable: false, lockedReason: 'closed' }]);
    expect(closed).toEqual({ canRecalculate: false, lockedReason: 'closed' });
    expect(recalcBlockOf({ yearLayout: false, state: 'Draft', sheetStates: {}, serverCanRecalculate: closed.canRecalculate, serverLockedReason: closed.lockedReason })).toBe('locked');
    expect(recalcBlockOf({ yearLayout: false, state: 'Draft', sheetStates: {}, serverCanRecalculate: false, serverLockedReason: 'approved' })).toBe('sheetApproved');
    expect(serverEligibility([])).toEqual({ canRecalculate: null, lockedReason: null });
    expect(serverEligibility(undefined)).toEqual({ canRecalculate: null, lockedReason: null });
  });

  it('ST-3: затверджений оригінал із копією-чернеткою — можна (сервер рахує копію); затверджено все — ні', () => {
    expect(recalcBlockOf({ yearLayout: false, state: 'Approved', sheetCode: 'S1', sheetStates: { S1: 'Approved', 'S1#5': 'Draft', S2: 'Approved' } })).toBeNull();
    expect(recalcBlockOf({ yearLayout: false, state: 'Draft', sheetCode: 'S1', sheetStates: { S1: 'Approved', 'S1#5': 'Approved', S2: 'Draft' } })).toBe('sheetApproved');
    expect(recalcBlockOf({ yearLayout: false, state: 'Submitted', sheetCode: 'S1', sheetStates: { S1: 'Submitted', 'S1#5': 'Approved' } })).toBeNull();
  });
});


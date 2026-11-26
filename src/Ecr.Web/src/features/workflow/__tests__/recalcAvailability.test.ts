import { describe, expect, it } from 'vitest';
import { recalcBlockOf } from '../recalcAvailability';

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
});

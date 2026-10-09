import { describe, it, expect } from 'vitest';
import type { CellConflictDto } from '@/api/types';
import { conflictAuthor } from '../ConflictPanel';

/**
 * AN-115: хто зробив чужу правку в діалозі конфлікту.
 *
 * ⛔ Імпорт книги Excel — правка людини: сервер тепер дає її ім'я, і панель показує його, а не
 * «system». Перерахунок, інтеграція, міграція — «Система» словом мови інтерфейсу, а не технічне
 * `system` із відповіді сервера.
 */

const Base: CellConflictDto = {
  rowKey: 'R1',
  columnCode: 'C2',
  yourValue: 9,
  theirValue: 12.4,
  theirUser: 'A. Serikbayev',
  theirOrigin: 'UserEdit',
  theirChangedAt: '2026-02-01T09:15:00.0000000Z',
  currentVersion: '0xFF',
};

describe('conflictAuthor (AN-115)', () => {
  it.each(['UserEdit', 'Import', 'ImportOverwrite'])('%s — ім’я з відповіді сервера', (origin) => {
    expect(conflictAuthor({ ...Base, theirOrigin: origin })).toBe('A. Serikbayev');
  });

  it.each(['Recalculation', 'Integration', 'Migration'])('%s — «Система», а не сире `system`', (origin) => {
    expect(conflictAuthor({ ...Base, theirOrigin: origin, theirUser: 'system' })).toBe('⟦audit.systemAuthor⟧');
  });

  it('автор невідомий — «невідомо» словом', () => {
    expect(conflictAuthor({ ...Base, theirOrigin: null, theirUser: null })).toBe('⟦grid.conflictUnknownUser⟧');
  });
});

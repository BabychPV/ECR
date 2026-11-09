import { describe, it, expect } from 'vitest';
import { structureChangeReasonText } from '@/features/audit/structureChangeReason';
import { authorName } from '@/features/audit/authorOptions';
import { formatDateTime } from '@/shared/format/datetime';

/**
 * F13-p213 (i5): журнал «Структура» для місяця, відкритого для одного документа.
 *
 * ⛔ Автозакриття задачею станів показувало «User #0 (no longer exists)» і сире «Expired»; запис відкриття не казав,
 * ДО КОЛИ. Тепер сервер пише конверти `document.reopen.audit.*`, строк — ISO, який клієнт показує в локалі читача;
 * автор `0` — умовний Id системи в задачах (`PeriodStateJob.SystemUserId`) — «Система».
 */
describe('журнал структури: місяць для одного документа', () => {
  it('відкриття — ключ каталогу зі строком у локалі читача і причиною', () => {
    const until = '2026-02-10T18:00:00Z';
    const shown = structureChangeReasonText(
      JSON.stringify({ k: 'document.reopen.audit.reopened', p: { reason: 'Лист №17', until } }),
    );

    expect(shown).toContain('document.reopen.audit.reopened');
    expect(shown).toContain(formatDateTime(until));
    expect(shown).not.toContain(until);
    expect(shown).toContain('Лист №17');
  });

  it.each(['document.reopen.audit.closedExpired', 'document.reopen.audit.closedReplaced', 'document.reopen.audit.closedManually'])(
    'закриття %s — ключ каталогу, не сирий JSON',
    (key) => {
      const shown = structureChangeReasonText(JSON.stringify({ k: key }));

      expect(shown).toContain(key);
      expect(shown).not.toContain('{"k"');
    },
  );

  it('автор 0 — «Система», а не «користувач #0 (більше не існує)»', () => {
    expect(authorName({ changedByUserId: 0, changedByDisplayName: null })).toContain('audit.systemActor');
    expect(authorName({ changedByUserId: 3, changedByDisplayName: null })).toContain('audit.userGone');
  });
});

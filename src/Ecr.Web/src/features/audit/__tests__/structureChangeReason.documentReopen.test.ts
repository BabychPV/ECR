import { describe, it, expect } from 'vitest';
import { structureChangeReasonText } from '@/features/audit/structureChangeReason';
import { authorName } from '@/features/audit/authorOptions';
import { formatDateTime } from '@/shared/format/datetime';
import { siteMomentText } from '@/shared/siteMoment';

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

  /*
   * ✎ debt-1109 i4-P3: строк — у часі МАЙДАНЧИКА, як рядок вкладки Periods (`siteMomentText`), а не в поясі читача.
   * ⛔ Червоне без фіксу: показ ішов `formatDateTime` у поясі браузера, і `zone` потрапляв у параметри як є.
   */
  it('відкриття з поясом майданчика — строк у часі сайту', () => {
    const until = '2026-11-09T19:00:00Z';
    const shown = structureChangeReasonText(
      JSON.stringify({ k: 'document.reopen.audit.reopened', p: { reason: 'R', until, zone: 'Asia/Tokyo' } }),
    );

    expect(shown).toContain(`until=${siteMomentText(until, 'Asia/Tokyo') ?? ''}`);
    // 19:00Z у Токіо — уже наступна доба: дата сайту, а не читача (середовище тестів — UTC).
    expect(shown).toMatch(/Nov 10, 2026/u);
    expect(shown).not.toContain('zone=');
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

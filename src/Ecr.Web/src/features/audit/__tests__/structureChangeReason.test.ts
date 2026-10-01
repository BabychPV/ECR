import { describe, it, expect } from 'vitest';
import { structureChangeReasonText } from '@/features/audit/structureChangeReason';

/**
 * P3 живого проходу екрана джерел: причина зміни налаштувань збору в журналі
 * структурних змін була українською фразою сервера на будь-якому інтерфейсі.
 * Тепер сервер пише конверт `integrationAudit.*`, клієнт розгортає його ключем каталогу.
 *
 * ⚠ У тестах каталог не завантажено — `t` повертає сам ключ; тому доказ —
 * «показано ключ каталогу, а не сирий JSON і не українську».
 */
describe('structureChangeReasonText', () => {
  it('конверт integrationAudit.* — ключ каталогу, не сирий JSON', () => {
    const raw = '{"k":"integrationAudit.sourceEntityCreated","p":{"entity":"FLD-1","connection":"PI-MAIN"}}';

    const shown = structureChangeReasonText(raw);

    expect(shown).toContain('integrationAudit.sourceEntityCreated');
    expect(shown).not.toContain('{"k"');
  });

  it.each([
    'integrationAudit.scheduleCreated',
    'integrationAudit.scheduleChanged',
    'integrationAudit.scheduleDeleted',
    'integrationAudit.fieldMapCreated',
    'integrationAudit.fieldMapPaused',
    'integrationAudit.fieldMapResumed',
    'integrationAudit.fieldMapUnitAccepted',
    'integrationAudit.fieldMapDeleted',
    'integrationAudit.rowWindowCreated',
    'integrationAudit.rowWindowChanged',
    'integrationAudit.rowWindowDeleted',
    'integrationAudit.sourceEntityCreated',
    'integrationAudit.sourceEntityBound',
    'integrationAudit.sourceEntityUnbound',
    'integrationAudit.eventMapCreated',
    'integrationAudit.eventMapChanged',
    'integrationAudit.eventMapDeleted',
    'integrationAudit.registryPolicyChanged',
  ])('%s — відомий ключ розгортається', (key) => {
    const raw = JSON.stringify({ k: key, p: { id: '1' } });

    expect(structureChangeReasonText(raw)).not.toBe(raw);
  });

  it('старий запис (готова фраза) і причина людини — як є', () => {
    expect(structureChangeReasonText('Сутність збору «FLD-1» заведено.')).toBe('Сутність збору «FLD-1» заведено.');
    expect(structureChangeReasonText('Typo fix')).toBe('Typo fix');
  });

  it('невідомий ключ і зламаний JSON — сирий рядок, а не вгаданий переклад', () => {
    expect(structureChangeReasonText('{"k":"other.key"}')).toBe('{"k":"other.key"}');
    expect(structureChangeReasonText('{not json')).toBe('{not json');
    expect(structureChangeReasonText('{"p":{}}')).toBe('{"p":{}}');
  });
});

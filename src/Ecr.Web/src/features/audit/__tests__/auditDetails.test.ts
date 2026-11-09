import { describe, expect, it } from 'vitest';
import { auditDetailsText } from '@/features/audit/auditDetails';

/**
 * ✎ debt-1109 x10: «Details» журналу безпеки — читабельні пари замість сирого JSON.
 * Мутація: повертати `raw` для об'єкта — червоніють перші два тести.
 */
describe('auditDetailsText', () => {
  it('об’єкт — пари «ключ: значення», масив через кому', () => {
    expect(auditDetailsText('{"roleId":7,"added":["Doc.Read","Doc.Write"],"removed":[]}')).toBe(
      'roleId: 7 · added: Doc.Read, Doc.Write · removed: —',
    );
  });

  it('вкладене — ключ через крапку, null — тире', () => {
    expect(auditDetailsText('{"user":{"id":3,"name":"ann"},"reason":null}')).toBe('user.id: 3 · user.name: ann · reason: —');
  });

  it('не об’єкт або зіпсований JSON — як є; порожньо — тире', () => {
    expect(auditDetailsText('plain text')).toBe('plain text');
    expect(auditDetailsText('[1,2]')).toBe('[1,2]');
    expect(auditDetailsText('{broken')).toBe('{broken');
    expect(auditDetailsText(null)).toBe('—');
    expect(auditDetailsText('{}')).toBe('—');
  });
});

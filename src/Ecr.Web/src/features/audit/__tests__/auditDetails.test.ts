import { describe, expect, it } from 'vitest';
import { auditDetails, auditDetailsText } from '@/features/audit/auditDetails';
import { jobKindLabel } from '@/features/workflow/jobLabel';

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

  // ✎ debt-1110: зміна значень запису довідника (`RegistryValueChanged`) — масив об'єктів, і вкладене глибше.
  // Мутація: повернути `JSON.stringify(item)` для об'єкта в `scalar` — червоне.
  it('масив об’єктів і вкладене глибше — читабельно, без сирого JSON', () => {
    const text = auditDetailsText(
      '{"registryDefId":5,"entryId":9,"changes":[{"field":"NAME","oldValue":"A","newValue":"B"},{"field":"QTY","oldValue":null,"newValue":"3"}]}',
    );
    expect(text).toBe(
      'registryDefId: 5 · entryId: 9 · changes: (field: NAME, oldValue: A, newValue: B); (field: QTY, oldValue: —, newValue: 3)',
    );
    expect(text).not.toMatch(/[{}"]/);
    expect(auditDetailsText('{"a":[{"b":{"c":[1,2]},"d":[]}]}')).toBe('a: (b: (c: 1, 2), d: —)');
  });

  // ✎ P-1124-4: живий екран — «jobId: IConsistencyCheckJob-9e3d… · reason: oji». Ідентифікатор задачі — назвою виду
  // задачі, GUID кореляції — геть із головного тексту; обидва лишаються в `technical`.
  // Мутація: прибрати гілку `JobIdPattern` — червоне (у тексті знову ім'я класу з GUID).
  it('jobId задачі — назва виду задачі, GUID — у technical, не в тексті', () => {
    const view = auditDetails(
      '{"jobId":"IConsistencyCheckJob-9e3d9be36c654521a03734c933b50263","reason":"oji","correlationId":"0f8fad5b-d9cb-469f-a165-70867728950e"}',
    );
    // Підпис виду задачі — з каталогу (`jobs.kind.consistencyCheck`, «Consistency check»); тест каталогу не вантажить.
    expect(view.text).toBe(`job: ${jobKindLabel('IConsistencyCheckJob')} · reason: oji`);
    expect(view.text).not.toMatch(/9e3d|IConsistency|0f8fad5b/u);
    expect(view.technical).toStrictEqual([
      'IConsistencyCheckJob-9e3d9be36c654521a03734c933b50263',
      '0f8fad5b-d9cb-469f-a165-70867728950e',
    ]);
    // Звичайне число-ідентифікатор лишається в тексті: воно коротке й осмислене (роль №7).
    expect(auditDetails('{"roleId":7}').text).toBe('roleId: 7');
  });

  it('не об’єкт або зіпсований JSON — як є; порожньо — тире', () => {
    expect(auditDetailsText('plain text')).toBe('plain text');
    expect(auditDetailsText('[1,2]')).toBe('[1,2]');
    expect(auditDetailsText('{broken')).toBe('{broken');
    expect(auditDetailsText(null)).toBe('—');
    expect(auditDetailsText('{}')).toBe('—');
  });
});

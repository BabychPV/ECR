import { describe, it, expect, vi, beforeAll, afterAll } from 'vitest';
import { EcrApiError } from '@/api/client';
import { loadCatalog } from '@/shared/i18n';
import { sheetDenialText } from '../sheetDenial';

/**
 * A2-08 (= A1-10, приймальна №2): відмова `403 ECR-ACCS-0403` про аркуш
 * називала його ЧИСЛОМ — «Sheet 2 cannot be approved…». Речення
 * перескладається з того самого рядка каталогу, але з назвою аркуша.
 *
 * ⚠ Каталог завантажується насправді (як у `a11yLabels.test.tsx`): без нього
 * `t()` віддає `⟦ключ⟧`, і «назва в тексті» перевірялася б на маркері.
 */

const Strings = {
  'err.ECR-ACCS-0403.approveDenied': 'Лист {sheetDefId} нельзя утвердить или вернуть в работу: {reason}.',
  'err.ECR-ACCS-0403.submitDenied': 'Лист {sheetDefId} нельзя подать: {reason}.',
  'err.ECR-ACCS-0403.reopenDenied': 'Лист {sheetDefId} нельзя вернуть в работу: {reason}.',
  'deny.InsufficientGrantLevel': 'Недостаточный уровень доступа.',
  'deny.PeriodClosed': 'Период закрыт.',
  'deny.NoGrant': 'У вас нет права редактировать эту ячейку.',
  'deny.approve.NoRight': 'нет права утверждать или отклонять листы в этом проекте',
  'deny.reopen.NoRight': 'нет права вернуть этот лист в работу',
  'deny.reopen.BusinessRule': 'лист не подан и не утверждён',
  'deny.submit.NoRight': 'нет права подать этот лист',
  'deny.reason.Unknown': 'действие сейчас недоступно',
  'deny.ProjectArchived': 'Проект архивирован.',
};

beforeAll(async () => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(JSON.stringify({ languageCode: 'ru', revision: 1, strings: Strings }), {
          status: 200,
          headers: { 'Content-Type': 'application/json', ETag: '"public-ru-1"' },
        }),
      ),
    ),
  );
  await loadCatalog('ru', 'public');
});

afterAll(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

function denial(messageKey: string, extra: Record<string, unknown> = {}, errorCode = 'ECR-ACCS-0403'): EcrApiError {
  return new EcrApiError({
    title: 'Forbidden',
    status: 403,
    detail: 'Лист 2 нельзя …',
    errorCode,
    correlationId: 'c-1',
    extensions2: { messageKey, sheetDefId: '2', ...extra },
  });
}

describe('sheetDenialText: відмова називає аркуш назвою (A2-08)', () => {
  it('submitDenied — назва замість id', () => {
    const text = sheetDenialText(
      denial('err.ECR-ACCS-0403.submitDenied', { reason: 'PeriodClosed', reasonKey: 'deny.PeriodClosed' }),
      'Выбросы',
    );

    expect(text).toBe('Лист «Выбросы» нельзя подать: Период закрыт.');
  });

  it('approveDenied — назва і причина з каталогу за reasonKey, без подвійної крапки', () => {
    const text = sheetDenialText(
      denial('err.ECR-ACCS-0403.approveDenied', {
        reason: 'PeriodClosed',
        reasonKey: 'deny.PeriodClosed',
      }),
      'Выбросы',
    );

    expect(text).toBe('Лист «Выбросы» нельзя утвердить или вернуть в работу: Период закрыт.');
  });

  it('D4: approveDenied без права — причина про затвердження, а не про правку комірки', () => {
    for (const reasonKey of ['deny.NoGrant', 'deny.InsufficientGrantLevel']) {
      const text = sheetDenialText(
        denial('err.ECR-ACCS-0403.approveDenied', { reason: 'NoGrant', reasonKey }),
        'Выбросы',
      );

      expect(text).toBe(
        'Лист «Выбросы» нельзя утвердить или вернуть в работу: нет права утверждать или отклонять листы в этом проекте.',
      );
      expect(text).not.toContain('ячейк');
    }
  });

  it('PM-PASS-A-P3 п.7: причина без рядка в каталозі - загальний текст, а не сирий reason', () => {
    const approve = sheetDenialText(
      denial('err.ECR-ACCS-0403.approveDenied', { reason: 'Locked', reasonKey: 'deny.Locked' }),
      'Выбросы',
    );
    const submit = sheetDenialText(
      denial('err.ECR-ACCS-0403.submitDenied', { reason: 'Locked', reasonKey: 'deny.Locked' }),
      'Отходы',
    );

    expect(approve).toBe('Лист «Выбросы» нельзя утвердить или вернуть в работу: действие сейчас недоступно.');
    expect(submit).toBe('Лист «Отходы» нельзя подать: действие сейчас недоступно.');
    expect(approve).not.toContain('Locked');
    expect(submit).not.toContain('Locked');
  });

  it('PM-PASS-A-P3 п.7: submitDenied без права - причина про подання, а не про комірку', () => {
    const text = sheetDenialText(
      denial('err.ECR-ACCS-0403.submitDenied', { reason: 'NoGrant', reasonKey: 'deny.NoGrant' }),
      'Отходы',
    );

    expect(text).toBe('Лист «Отходы» нельзя подать: нет права подать этот лист.');
    expect(text).not.toContain('ячейк');
  });

  it('PM-PASS-A-P3 п.7: reopenDenied без права - причина про повернення, а не про комірку', () => {
    for (const reason of ['NoGrant', 'InsufficientGrantLevel']) {
      const bare = sheetDenialText(denial('err.ECR-ACCS-0403.reopenDenied', { reason }), 'Вода');
      const keyed = sheetDenialText(
        denial('err.ECR-ACCS-0403.reopenDenied', { reason, reasonKey: `deny.${reason}` }),
        'Вода',
      );

      expect(bare).toBe('Лист «Вода» нельзя вернуть в работу: нет права вернуть этот лист в работу.');
      expect(keyed).toBe(bare);
      expect(bare).not.toContain('ячейк');
    }
  });

  it('PM-PASS-A-P3 п.7: reopenDenied з сирою назвою enum - текст каталогу, не ідентифікатор', () => {
    const bySpecific = sheetDenialText(denial('err.ECR-ACCS-0403.reopenDenied', { reason: 'BusinessRule' }), 'Вода');
    const byGeneric = sheetDenialText(denial('err.ECR-ACCS-0403.reopenDenied', { reason: 'ProjectArchived' }), 'Вода');

    expect(bySpecific).toBe('Лист «Вода» нельзя вернуть в работу: лист не подан и не утверждён.');
    expect(byGeneric).toBe('Лист «Вода» нельзя вернуть в работу: Проект архивирован.');
  });

  it('PM-PASS-A-P3 п.7: reopenDenied з невідомою причиною - загальний текст, ніколи сирий ідентифікатор', () => {
    for (const reason of ['SomethingNew', 'x', 'a.b c', '']) {
      const text = sheetDenialText(denial('err.ECR-ACCS-0403.reopenDenied', { reason }), 'Вода');

      expect(text).toBe('Лист «Вода» нельзя вернуть в работу: действие сейчас недоступно.');
    }

    expect(sheetDenialText(denial('err.ECR-ACCS-0403.reopenDenied'), 'Вода')).toBe(
      'Лист «Вода» нельзя вернуть в работу: действие сейчас недоступно.',
    );
  });

  it('PM-PASS-A-P3 п.7: без назви аркуша причина все одно про дію, лист названо id', () => {
    for (const sheetName of [null, undefined, '  ']) {
      const approve = sheetDenialText(
        denial('err.ECR-ACCS-0403.approveDenied', { reason: 'NoGrant', reasonKey: 'deny.NoGrant' }),
        sheetName,
      );
      const reopen = sheetDenialText(denial('err.ECR-ACCS-0403.reopenDenied', { reason: 'NoGrant' }), sheetName);

      expect(approve).toBe(
        'Лист 2 нельзя утвердить или вернуть в работу: нет права утверждать или отклонять листы в этом проекте.',
      );
      expect(reopen).toBe('Лист 2 нельзя вернуть в работу: нет права вернуть этот лист в работу.');
    }
  });

  it('інша відмова, інший код, не EcrApiError чи без назви — null (показує showApiError)', () => {
    expect(sheetDenialText(denial('err.ECR-ACCS-0403.addRowDenied'), 'Выбросы')).toBeNull();
    expect(sheetDenialText(denial('err.ECR-ACCS-0403.approveDenied', {}, 'ECR-DOC-0409'), 'Выбросы')).toBeNull();
    expect(sheetDenialText(new Error('boom'), 'Выбросы')).toBeNull();
    // ні назви, ні id в розширеннях - показати нічим, лишається showApiError
    expect(
      sheetDenialText(
        new EcrApiError({
          title: 'Forbidden',
          status: 403,
          detail: 'd',
          errorCode: 'ECR-ACCS-0403',
          correlationId: 'c-1',
          extensions2: { messageKey: 'err.ECR-ACCS-0403.approveDenied' },
        }),
        null,
      ),
    ).toBeNull();
  });
});

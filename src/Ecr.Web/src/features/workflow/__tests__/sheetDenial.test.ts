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
  'err.ECR-ACCS-0403.approveOwnSubmission':
    'Лист {sheetDefId} не может утвердить тот же человек, который его подал.',
  'err.ECR-ACCS-0403.submitDenied': 'Лист {sheetDefId} нельзя подать: {reason}.',
  'err.ECR-ACCS-0403.reopenDenied': 'Лист {sheetDefId} нельзя вернуть в работу: {reason}.',
  'deny.InsufficientGrantLevel': 'Недостаточный уровень доступа.',
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
  it('approveOwnSubmission — назва замість id', () => {
    const text = sheetDenialText(denial('err.ECR-ACCS-0403.approveOwnSubmission'), 'Выбросы');

    expect(text).toBe('Лист «Выбросы» не может утвердить тот же человек, который его подал.');
  });

  it('approveDenied — назва і причина з каталогу за reasonKey, без подвійної крапки', () => {
    const text = sheetDenialText(
      denial('err.ECR-ACCS-0403.approveDenied', {
        reason: 'InsufficientGrantLevel',
        reasonKey: 'deny.InsufficientGrantLevel',
      }),
      'Выбросы',
    );

    expect(text).toBe('Лист «Выбросы» нельзя утвердить или вернуть в работу: Недостаточный уровень доступа.');
  });

  it('submitDenied без рядка причини в каталозі — сирий reason, як у сервера', () => {
    const text = sheetDenialText(
      denial('err.ECR-ACCS-0403.submitDenied', { reason: 'Locked', reasonKey: 'deny.Locked' }),
      'Отходы',
    );

    expect(text).toBe('Лист «Отходы» нельзя подать: Locked.');
  });

  it('reopenDenied — теж про аркуш', () => {
    const text = sheetDenialText(denial('err.ECR-ACCS-0403.reopenDenied', { reason: 'x' }), 'Вода');

    expect(text).toBe('Лист «Вода» нельзя вернуть в работу: x.');
  });

  it('інша відмова, інший код, не EcrApiError чи без назви — null (показує showApiError)', () => {
    expect(sheetDenialText(denial('err.ECR-ACCS-0403.addRowDenied'), 'Выбросы')).toBeNull();
    expect(sheetDenialText(denial('err.ECR-ACCS-0403.approveDenied', {}, 'ECR-DOC-0409'), 'Выбросы')).toBeNull();
    expect(sheetDenialText(new Error('boom'), 'Выбросы')).toBeNull();
    expect(sheetDenialText(denial('err.ECR-ACCS-0403.approveDenied'), '  ')).toBeNull();
    expect(sheetDenialText(denial('err.ECR-ACCS-0403.approveDenied'), null)).toBeNull();
  });
});

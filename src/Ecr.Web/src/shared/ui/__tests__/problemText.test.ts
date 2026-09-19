import { describe, it, expect } from 'vitest';
import { EcrApiError } from '@/api/client';
import { problemText } from '@/shared/ui/problemText';

/**
 * Рішення людини (2026-09-20): «українську прибрати — має бути залежно від
 * обраної мови».
 *
 * ⛔ Що тут доводиться і чому саме так. Мов у продукті три (`D-95`: en, ru,
 * kz), української серед них немає — а серверні речення писалися нею. Спокуса
 * — розпізнавати мову тексту. Це неможливо зробити чесно: російський
 * каталожний текст теж кирилицею, казахський — тим паче, і перевірка «є
 * кирилиця → сховати» ховала б рівно те, що треба показати.
 *
 * ⚠ Тому правило спирається на ОЗНАКУ ВІДПОВІДІ, а не на текст: `messageKey`
 * означає, що сервер зібрав `detail` із каталогу мовою користувача. Тест
 * нижче перевіряє саме це розрізнення — на тому самому тексті, лише з
 * ознакою і без неї.
 */

function problem(extra: Partial<ConstructorParameters<typeof EcrApiError>[0]>): EcrApiError {
  return new EcrApiError({
    title: 'Conflict',
    status: 409,
    errorCode: 'ECR-PRD-4223',
    correlationId: 'cid-1',
    detail: 'Період закрито — спершу відкрийте період.',
    ...extra,
  });
}

describe('problemText: що можна показати людині', () => {
  it('є messageKey — подробиця показується', () => {
    const shown = problemText(problem({ extensions2: { messageKey: 'err.x' } }));

    expect(shown.detail).toBe('Період закрито — спершу відкрийте період.');
    expect(shown.suppressed).toBeNull();
  });

  it('немає messageKey — та сама подробиця НЕ показується', () => {
    /*
     * ⛔ Головне твердження. Текст той самий, що й вище — різниця лише в
     * ознаці. Якби правило спиралося на мову, обидва випадки поводилися б
     * однаково, і тест був би порожній.
     */
    const shown = problemText(problem({}));

    expect(shown.detail).toBeNull();
    expect(shown.suppressed).toBe('Період закрито — спершу відкрийте період.');
  });

  it('назва проблеми лишається завжди — користувач не лишається без пояснення', () => {
    expect(problemText(problem({})).title).toBe('Conflict');
    expect(problemText(problem({})).code).toBe('ECR-PRD-4223');
    expect(problemText(problem({})).correlationId).toBe('cid-1');
  });

  it('відмову склав сам КЛІЄНТ (status 0) — подробиця показується без messageKey', () => {
    /*
     * ⛔ Не виняток «аби проходило». `RenderErrorScreen` складає свої відмови
     * англійським ЛІТЕРАЛОМ навмисно: саме тоді, коли екран не намалювався,
     * каталог може бути недоступний, і звернення до нього дало б `⟦…⟧`
     * замість пояснення. `status === 0` — єдина властивість, якою клієнтська
     * відмова відрізняється від серверної: у неї немає коду відповіді, бо
     * немає й самої відповіді.
     */
    const shown = problemText(
      problem({ status: 0, detail: 'The application hit an internal error.' }),
    );

    expect(shown.detail).toBe('The application hit an internal error.');
    expect(shown.suppressed).toBeNull();
  });

  it('заголовок, що прийшов КЛЮЧЕМ, перетворюється на текст', () => {
    /*
     * ⚠ Один такий випадок є: `401` без тіла народжується на клієнті, а
     * `shared/i18n` імпортує `apiFetchIfChanged` із того ж файлу — виклик
     * `t()` там замкнув би цикл модулів. Тому клієнт кладе ключ, а розв'язує
     * його шар показу.
     */
    const shown = problemText(problem({ title: 'err.ECR-AUTH-0401.signInRequired' }));

    expect(shown.title).not.toBe('err.ECR-AUTH-0401.signInRequired');
    expect(shown.title).toContain('err.ECR-AUTH-0401.signInRequired');
  });

  it('людський заголовок ключем не вважається', () => {
    // ⛔ Інакше будь-яка назва проблеми перетворилася б на позначений ключ
    // `⟦…⟧` на екрані.
    expect(problemText(problem({ title: 'Conflict' })).title).toBe('Conflict');
  });

  it('не наша відмова — жодного тексту на екран', () => {
    /*
     * ⚠ Сюди потрапляє `TypeError`, помилка мережі, текст із чужої
     * бібліотеки. Жодне з цього не перекладене й не призначене людині, тому
     * на екран іде каталожна назва, а саме повідомлення — у діагностику.
     */
    const shown = problemText(new Error('Failed to fetch'));

    expect(shown.detail).toBeNull();
    expect(shown.suppressed).toBe('Error: Failed to fetch');
    expect(shown.code).toBeNull();
  });
});

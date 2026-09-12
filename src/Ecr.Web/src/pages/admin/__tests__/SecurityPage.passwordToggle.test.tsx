import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, it, expect } from 'vitest';

/**
 * Тумблер видимості пароля в діалозі створення локального користувача
 * (`Q-260`).
 *
 * ⛔ Mantine `PasswordInput` ставить на кнопку-тумблер `aria-hidden="true"` і
 * `tabIndex={-1}` за замовчуванням, доки `visibilityToggleButtonProps` цього
 * не перекриє (`node_modules/@mantine/core/esm/components/PasswordInput/PasswordInput.mjs`,
 * рядки 118-120: `"aria-hidden": !visibilityToggleButtonProps` рахується ДО
 * розгортання пропу, тож сама наявність об'єкта вже знімає приховування, а
 * явний `tabIndex: 0` повертає зупинку табом) — тобто кнопка існує лише для
 * миші: клавіатура до неї не доходить, а читалка не бачить її взагалі
 * (`aria-hidden` виключає елемент з дерева доступності повністю, тому
 * `axe`/`button-name` тут теж мовчить — не хибна тривога, а структурна
 * сліпа зона правила).
 *
 * ⚠ **Судження, не ескалація** (документується тут, а не в ASK): справжнє
 * DOM-монтування `SecurityPage` для цього конкретного поля вимагає відкрити
 * модалку створення користувача Й перемкнути `Select` "Тип" на "Local" —
 * а Mantine `Select`/`Combobox` у зв'язці з jsdom у цьому репозиторії висить
 * НАЗАВЖДИ (перевірено ізольованим відтворенням: `fireEvent.click` на
 * тумблер `Select` ніколи не завершується, тест падає лише на зовнішньому
 * таймауті `vitest` без жодної власної помилки — ознака нескінченного циклу
 * в позиціонуванні `floating-ui`/`Combobox`, а не в коді цієї картки; жоден
 * наявний тест у репозиторії досі не взаємодіяв із випадним списком
 * Mantine `Select`, тож це не регресія цієї зміни). Ганяти середовище
 * заради одного поля — поза межами картки Q-260 (яка забороняє чіпати щось,
 * чого немає в переліку файлів). Тому для ЦЬОГО файлу перевірка —
 * джерельна, тим самим прийомом, що вже використовує
 * `shared/theme/__tests__/motion.test.tsx` для `router.tsx`: читає реальний
 * `SecurityPage.tsx` і стверджує, що виклик `PasswordInput` для разового
 * пароля передає `visibilityToggleButtonProps`. Два інші файли картки
 * (`LoginPage.test.tsx`/`ChangePasswordPage.test.tsx`) уже доводять РАНТАЙМ
 * (`aria-hidden`/`tabIndex` на справжньому DOM) для того самого механізму
 * Mantine — він однаковий для всіх п'яти використань незалежно від сторінки,
 * тож джерельна перевірка тут закриває залишковий ризик «а чи справді
 * пропис є в САМЕ цьому файлі», не переоцінюючи те, що вже доведено рантаймом
 * деінде.
 */
const source = readFileSync(
  path.resolve(process.cwd(), 'src/pages/admin/SecurityPage.tsx'),
  'utf8',
);

describe('SecurityPage: тумблер видимості разового пароля (Q-260)', () => {
  it('оголошує аргументи тумблера з ненульовим tabIndex і aria-label', () => {
    const propsMatch = source.match(
      /const passwordToggleProps = \{[^}]*'aria-label':\s*'([^']+)'[^}]*tabIndex:\s*(\d+)[^}]*\}/,
    );

    expect(propsMatch).not.toBeNull();
    expect(propsMatch?.[1]).toBe('Toggle password visibility');
    expect(propsMatch?.[2]).toBe('0');
  });

  it('передає ці аргументи в PasswordInput разового пароля', () => {
    const fieldMatch = source.match(
      /<PasswordInput\s[^]*?label=\{t\('security\.oneTimePassword'\)\}[^]*?\/>/,
    );

    expect(fieldMatch).not.toBeNull();
    expect(fieldMatch?.[0]).toContain('visibilityToggleButtonProps={passwordToggleProps}');
  });

  it('МУТАЦІЯ (задокументовано, не в коміті): без рядка вище тест (2) падає', () => {
    // ⛔ Цей тест сам не мутує файл — мутаційний доказ виконано вручну під
    // час розробки (див. Q-260.md): тимчасове видалення рядка
    // `visibilityToggleButtonProps={passwordToggleProps}` з поля разового
    // пароля валило тест (2) вище (RED), відновлення рядка повертало GREEN.
    // Перевірка тут — сторож РЕГРЕСІЇ формату, яким той доказ був знятий:
    // рядок, що встановлює проп, синтаксично один-єдиний для цього поля.
    const occurrences = source.match(/visibilityToggleButtonProps={passwordToggleProps}/g) ?? [];
    expect(occurrences.length).toBe(1);
  });
});

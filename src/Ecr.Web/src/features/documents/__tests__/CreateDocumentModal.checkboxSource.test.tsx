import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, it, expect } from 'vitest';

/**
 * Крах застосунку на чекбоксі аркуша в «Новому документі», знайдений живим
 * першоособовим UX-проходом (не тестом): клік по чекбоксу на реальному
 * стенді (`http://localhost:5173`, справжній `dotnet run` + `npm run dev`)
 * розбивав ВЕСЬ застосунок до голого «Unexpected Application Error!» React
 * Router — `TypeError: Cannot read properties of null (reading 'checked')`.
 *
 * ⛔ Причина: `onChange` читав `event.currentTarget.checked` ЛІНИВО,
 * ВСЕРЕДИНІ функції-апдейтера `setSheets((current) => …)`. React обнуляє
 * `event.currentTarget` одразу після завершення обробника (react.dev: «After
 * the event handler has been called, event.currentTarget will be set to
 * null»); `main.tsx` рендерить застосунок усередині `<StrictMode>`, а той
 * навмисно кличе апдейтер ДВІЧІ — і на другому виклику `currentTarget` уже
 * `null`. Той самий клас дефекту виправлено (і доведено РАНТАЙМОМ, під
 * `<StrictMode>`, реальним кліком через `userEvent`) у
 * `features/registries/__tests__/RegistryEntryEditor.strictMode.test.tsx` і
 * `pages/admin/__tests__/SecurityPage.createRolePermission.strictMode.test.tsx`.
 *
 * ⚠ **Судження, не ескалація** (як і `SecurityPage.passwordToggle.test.tsx`
 * для тієї самої причини): дійти до цього чекбокса в jsdom можна лише через
 * ДВА Mantine `Select` («Project», «Template version») поспіль — а клік по
 * ОПЦІЇ випадного списку Mantine `Select` у зв'язці з jsdom у цьому
 * репозиторії висить НАЗАВЖДИ (задокументовано й перевірено ізольованим
 * відтворенням у `SecurityPage.passwordToggle.test.tsx`; жоден наявний тест
 * досі не взаємодіяв із опцією такого списку). Ганяти середовище заради
 * цього одного чекбокса, коли той самий механізм уже доведено рантаймом на
 * двох інших чекбоксах, — зайвий ризик за нульовий приріст певності. Тому
 * тут — джерельна перевірка, тим самим прийомом.
 */
const source = readFileSync(
  path.resolve(process.cwd(), 'src/features/documents/CreateDocumentModal.tsx'),
  'utf8',
);

describe('CreateDocumentModal: чекбокс аркуша не читає event.currentTarget лінивого всередині апдейтера', () => {
  it('onChange чекбокса аркуша читає event.currentTarget.checked ОДРАЗУ, до виклику setSheets', () => {
    const onChangeMatch = source.match(
      /<Checkbox\s[^]*?onChange=\{\(event\) => \{([^]*?)\}\}\s*\/>/,
    );

    expect(onChangeMatch).not.toBeNull();
    const body = onChangeMatch?.[1] ?? '';

    // ⛔ Це і є доказ фікса: `checked` присвоюється ЗМІННІЙ синхронно в тілі
    // обробника, а не читається лінивим доступом до `event.currentTarget`
    // усередині функції, переданої в `setSheets`.
    expect(body).toMatch(/const checked = event\.currentTarget\.checked;/);

    const setSheetsCallMatch = body.match(/setSheets\(\(current\) =>([^]*?)\);/);
    expect(setSheetsCallMatch).not.toBeNull();

    // ⛔ Мутаційний доказ (задокументовано, не в коміті): до фікса це саме
    // виглядало як `setSheets((current) => event.currentTarget.checked ? …)`
    // — апдейтер сам читав `event.currentTarget`. Живий крах на реальному
    // стенді відтворював рівно цей рядок.
    expect(setSheetsCallMatch?.[1]).not.toMatch(/event\.currentTarget/);
    expect(setSheetsCallMatch?.[1]).toMatch(/\bchecked\b/);
  });
});

import { StrictMode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RegistryDefDto } from '@/api/types';
import { RegistryEntryEditor } from '@/features/registries/RegistryEntryEditor';

/**
 * Живий дефект, знайдений першоособовим UX-проходом (створення документа,
 * `CreateDocumentModal.tsx`, той самий клас), тепер підтверджений і тут:
 * `onChange` поля значення довідникового запису читав `event.currentTarget`
 * ЛІНИВО, ВСЕРЕДИНІ функції-апдейтера `setValues`.
 *
 * ⛔ `event.currentTarget` — поле СИНТЕТИЧНОЇ події, і React обнуляє його
 * одразу після завершення обробника (react.dev: «After the event handler has
 * been called, event.currentTarget will be set to null»). `main.tsx` рендерить
 * увесь застосунок усередині `<StrictMode>`, а той навмисно кличе
 * функцію-апдейтер `setState` ДВІЧІ (щоб зловити нечисті апдейтери) — і на
 * другому виклику `event.currentTarget` уже `null`. Наслідок: `TypeError:
 * Cannot read properties of null (reading 'value')`, і без `ErrorBoundary` на
 * маршруті — увесь застосунок замінюється голим «Unexpected Application
 * Error!» React Router. Живим повторенням саме цей крах спіймано в
 * `CreateDocumentModal.tsx` (чекбокс аркуша); тут — той самий механізм на
 * ТЕКСТОВОМУ полі значення довідникового поля.
 *
 * ⚠ Тест обгортає рендер у `<StrictMode>` НАВМИСНО — без цього другого
 * виклику апдейтера не було б, і тест лишався б зеленим і до, і після фіксу.
 */
const Registry: RegistryDefDto = {
  id: 7,
  code: 'PLANT',
  nameL10n: { values: { en: 'Plants' } },
  isHierarchical: false,
  isTemporal: false,
  sourceKind: 'Local',
  fields: [
    {
      id: 1,
      code: 'CAPACITY',
      dataType: 'Decimal',
      isRequired: false,
      isScopeField: false,
      lookupRegistryDefId: null,
      nameL10n: { values: { en: 'Capacity' } },
      unitId: null,
    },
  ],
};

function show() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <StrictMode>
      <MantineProvider>
        <QueryClientProvider client={client}>
          <RegistryEntryEditor registry={Registry} entry={null} opened onClose={() => {}} />
        </QueryClientProvider>
      </MantineProvider>
    </StrictMode>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RegistryEntryEditor: значення поля довідника під StrictMode', () => {
  it('друкування у полі значення не розбиває застосунок і зберігає введений текст', async () => {
    const user = userEvent.setup();
    show();

    const field = await screen.findByLabelText(/CAPACITY/);

    // ⛔ ЧЕРВОНИЙ до фіксу: другий (StrictMode) виклик апдейтера
    // `setValues` кидав `TypeError`, і `userEvent.type` не завершувався б
    // штатно — React-помилка спливла б як необроблений виняток тесту.
    await user.type(field, '123');

    expect((field as HTMLInputElement).value).toBe('123');
  });
});

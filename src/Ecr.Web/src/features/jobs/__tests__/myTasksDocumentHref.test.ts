import { describe, expect, it } from 'vitest';
import { generatePath } from 'react-router-dom';
import { routes } from '@/app/routes';
import { myTaskDocumentHref } from '@/features/jobs/MyTasksDrawer';

/**
 * Посилання на документ із шухляди веде туди ж, куди реєстр маршрутів.
 *
 * ⛔ `features` не імпортують `app` (правило зафіксоване в `JobFacts.tsx`), тож
 * адреса в шухляді — літерал. Літерал, ніким не звірений із реєстром, — це
 * рівно той дефект, що вже стався в `AppLayout` до `navRoutes`: той самий шлях
 * набирався рядком у ДВОХ місцях і нічого не заважало їм розійтися.
 *
 * ⚠ Тому звірка живе в ТЕСТІ — місці, якому імпортувати `app` можна; той самий
 * прийом, що вже вживає `features/search/__tests__/searchRoute.test.ts`.
 *
 * Мутація: змініть `/documents/` на `/document/` у `MyTasksDrawer.tsx` — тест
 * червоний.
 */
describe('адреса документа в шухляді «My tasks»', () => {
  it('збігається з routes.documentDetail', () => {
    expect(myTaskDocumentHref(42)).toBe(
      generatePath(routes.documentDetail.path, { id: '42' }),
    );
  });
});

import type { JSX } from 'react';

/**
 * Місце для діалогів конструктора (PM-08c). Рендериться всередині `TypeEditorContext` і
 * `MemoryRouter/Router`, тож може читати `useTypeEditorApi()`, `readDialog(useSearchParams()[0])` і
 * закривати себе через `withoutDialog`. ⚠ Цей файл замінюється; PM-08b діалогів не показує, тож
 * кнопки редактора лише ставлять `?dialog=<id>`.
 */
export function DialogsHost(): JSX.Element | null {
  return null;
}

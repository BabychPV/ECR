import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, it, expect } from 'vitest';

/**
 * `X-08`: сирий `error.message` на екрані.
 *
 * ⛔ `EcrApiError.message` — це `detail ?? title` БЕЗ розбору мови: сире
 * речення сервера (українською, без `messageKey`), а для не-нашої відмови
 * `String(error)` — `TypeError: Failed to fetch`. Показувати можна лише те,
 * що пропустив `problemText` (`showApiError`, `ErrorAlert`).
 *
 * ⚠ Перелік файлів — ті, де дефект знайшли живцем (UX-прохід, четвертий
 * раунд). Сторож по тексту, а не рендер, бо відмова тут — у п'яти різних
 * діалогах і тостах, і кожен окремий рендер-тест перевіряв би один шлях із
 * п'яти. Поведінку самого розбору стережуть `problemText.test.ts` і
 * `transportTitle.test.ts`; рендер-доказ — `RoleActions.test.tsx`,
 * `UserAccessEditor.partialSave.test.tsx`.
 */

const Files = [
  'features/mapping/CreateMappingModal.tsx',
  'pages/admin/GrantsPanel.tsx',
  'pages/admin/SourcesPage.tsx',
  'features/security/RoleActions.tsx',
  'features/security/UserAccessEditor.tsx',
];

/** Код без коментарів: згадка дефекту в поясненні — не дефект. */
function codeOf(file: string): string {
  const text = readFileSync(path.resolve(process.cwd(), 'src', file), 'utf8');

  return text.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:'"`])\/\/.*$/gm, '$1');
}

describe('X-08: сирий текст відмови не йде на екран', () => {
  it.each(Files)('%s не показує error.message / String(error)', (file) => {
    const code = codeOf(file);

    // ⛔ Мутація «повернути `error instanceof EcrApiError ? error.message :
    // String(error)`» у будь-якому з файлів робить його рядок червоним.
    expect(code).not.toMatch(/\berror\.message\b/);
    expect(code).not.toMatch(/String\(error\)/);
  });
});

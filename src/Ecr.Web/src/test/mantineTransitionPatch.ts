import { readFileSync } from 'node:fs';
import path from 'node:path';
import { patchMantineUseTransition } from '../../scripts/patch-mantine-transition.mjs';

/**
 * Чи залатаний `@mantine/core` у `node_modules` ЦЬОГО дерева (`scripts/patch-mantine-transition.mjs`).
 *
 * ⛔ ЧОМУ ЦЕЙ ФАЙЛ ІСНУЄ (`B137`, 2026-11-16). Латку ставить `postinstall`, тобто вона живе в
 * `node_modules`, а не в репозиторії. Дерево, де `postinstall` не відпрацював (`npm ci
 * --ignore-scripts`, `ignore-scripts=true` у конфігу npm, `node_modules`, скопійований з
 * незалатаного дерева чи встановлений до появи латки), дає на чистому `main` рівно три червоні
 * тести: один у `app/__tests__/mantineTransitionPatch.test.ts` і обидва в
 * `app/__tests__/modalReopenRemount.test.tsx` (`expected 2 to be 1`). У CI їх немає (`npm ci`
 * запускає `postinstall`), тож падіння читалося як «плаває локально» — хоча воно детерміноване
 * і лікується однією командою. Відтворено: зняття латки в `node_modules` дає дослівно ці три
 * падіння, повернення — 8/8 зелених.
 *
 * Тому обидва файли беруть звідси підказку {@link MantinePatchMissingHint}: падіння саме називає
 * причину і команду. Поведінки тестів це не змінює — ті самі перевірки, ті самі пороги.
 */
export const InstalledMantineUseTransitionPath = path.resolve(
  process.cwd(),
  'node_modules/@mantine/core/esm/components/Transition/use-transition.mjs',
);

/** Текст модуля, як він лежить у `node_modules`. */
export const InstalledMantineUseTransition = readFileSync(InstalledMantineUseTransitionPath, 'utf8');

/**
 * `true`, якщо латці нема чого додати: модуль уже залатаний або має апстрімний фікс.
 * Незнайомий текст модуля (латка кидає) — «не залатаний»: про нього скаже сама латка.
 */
export function isMantineTransitionPatched(code: string = InstalledMantineUseTransition): boolean {
  try {
    return patchMantineUseTransition(code) === code;
  } catch {
    return false;
  }
}

/** Що сталося і що зробити — у повідомлення перевірки, яка падає лише без латки. */
export const MantinePatchMissingHint =
  '⚠ СЕРЕДОВИЩЕ, не дефект продукту: `@mantine/core` у node_modules НЕ залатаний — `postinstall` ' +
  'не відпрацював (`npm ci --ignore-scripts`, `ignore-scripts=true` або скопійований node_modules). ' +
  'Виправлення: `node scripts/patch-mantine-transition.mjs` у `src/Ecr.Web` (або `npm ci`)';

/** Підказка лише тоді, коли латки справді немає; із латкою падіння — справжня регресія. */
export function mantinePatchHint(): string | undefined {
  return isMantineTransitionPatched() ? undefined : MantinePatchMissingHint;
}

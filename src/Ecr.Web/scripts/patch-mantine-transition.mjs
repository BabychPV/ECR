/**
 * Латка `@mantine/core` 7.15.2, `Transition/use-transition.mjs`: повторне відкриття діалогу
 * протягом кадру-двох після закриття перемонтовувало його вміст посеред вводу.
 *
 * ⛔ Механізм. `handleStateChange` на кожну зміну `mounted` скасовує лише таймер переходу, але НЕ
 * запланований `requestAnimationFrame`. Закрили — пішов ланцюжок із двох кадрів, і лише в другому
 * ставиться таймер «exited». Відкрили знову до другого кадру — скасовувати ще нічого, а кадр
 * закриття лишається живим. Обидва ланцюжки доходять до кінця: «exited» розмонтовує вміст
 * (`Modal`, `Drawer`, `Popover`, `Menu` — усе на `Transition`), за кадр «entered» монтує його
 * знову — порожнім. Поле, у яке вже друкують, відʼєднується разом із набраним.
 *
 * Апстрім виправив рівно це в 7.17.8 (`clearAllTimeouts`: і таймери, і `cancelAnimationFrame`).
 * ⛔ Чому не оновлення до 7.17.8: воно додає +1.5–1.9 КБ gzip КОЖНОМУ маршруту, а найважчий
 * (`PipelinePage`) уже на 247.3 КБ із 250 (`D-132`). Латка — один вираз, 0 КБ.
 *
 * ⚠ Де діє. `postinstall` латає файл у `node_modules` — так латку бачать і `vite dev`
 * (попередня збірка залежностей esbuild іде повз плагіни), і vitest (бере `@mantine/core` з
 * `node_modules` напряму). Пропускати Mantine через конвеєр Vite у тестах (`server.deps.inline`)
 * міряли: повний набір не вклався у 25 хв проти 167 с. `vite build` застосовує ту саму латку ще
 * й у `transform` (`vite.config.ts`) — продукт залатаний навіть після `npm ci --ignore-scripts`.
 *
 * ⛔ Латка не мовчить: якщо текст модуля змінився (оновили Mantine) і в ньому немає ні старого
 * якоря, ні апстрімного фіксу — і `npm ci`, і збірка падають з назвою цього файла.
 * Із апстрімним фіксом (`clearAllTimeouts`) модуль лишається як є — латку тоді можна прибрати.
 */
import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/** Модуль, який латається (ESM-збірка Mantine — саме її беруть Vite і vitest). */
export const MantineUseTransitionModule =
  /[\\/]@mantine[\\/]core[\\/]esm[\\/]components[\\/]Transition[\\/]use-transition\.mjs$/;

const Anchor = 'window.clearTimeout(transitionTimeoutRef.current);';
const Patched = `${Anchor} cancelAnimationFrame(rafRef.current);`;
const UpstreamFix = 'clearAllTimeouts';

/** Повертає модуль із латкою; кидає, якщо не впізнав його. Повторне застосування — без змін. */
export function patchMantineUseTransition(code) {
  if (code.includes(UpstreamFix) || code.includes(Patched)) return code;

  const at = code.indexOf('const handleStateChange');
  const anchor = at < 0 ? -1 : code.indexOf(Anchor, at);
  if (anchor < 0) {
    throw new Error(
      '`@mantine/core` Transition/use-transition.mjs змінився: не знайдено місця латки ' +
        '(`scripts/patch-mantine-transition.mjs`). Перевір, чи нова версія сама скасовує ' +
        '`requestAnimationFrame` у `handleStateChange`, і онови або прибери латку.',
    );
  }

  return code.slice(0, anchor) + Patched + code.slice(anchor + Anchor.length);
}

if (process.argv[1] !== undefined && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const file = path.resolve(
    path.dirname(fileURLToPath(import.meta.url)),
    '../node_modules/@mantine/core/esm/components/Transition/use-transition.mjs',
  );
  const code = readFileSync(file, 'utf8');
  const patched = patchMantineUseTransition(code);
  if (patched !== code) writeFileSync(file, patched);
  console.log(`patch-mantine-transition: ${patched === code ? 'вже залатано' : 'залатано'} ${file}`);
}

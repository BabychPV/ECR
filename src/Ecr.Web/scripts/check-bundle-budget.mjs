/**
 * Гейт бюджету клієнта (`D-132`): чанк маршруту ≤ 250 КБ gzip; динамічні чанки — власні стелі (PSS-20).
 *
 * ⛔ Скрипт існує тому, що бюджет був записаний у двох документах і не
 * перевірявся НІДЕ. `07-checkpoints.md` §361 стверджував, що його стереже
 * `npm run build`; насправді `build` — це `tsc -b && vite build`, який друкує
 * розміри і виходить із нулем незалежно від них. Обґрунтування самого `D-132`
 * — «бюджет, який не перевіряють, не існує» — до появи цього файлу описувало
 * власний стан.
 *
 * ⚠ Що саме зважується. Не окремий файл, а **все, що браузер мусить
 * завантажити, щоб показати маршрут**: чанк сторінки, спільний вхідний чанк і
 * весь транзитивний граф СТАТИЧНИХ імпортів разом із їхнім CSS. Читати бюджет
 * як «кожен файл окремо ≤ 250 КБ» означало б завжди його проходити: сторінки
 * важать одиниці кілобайт, а спільний вхідний чанк — 173 КБ, і жоден із них
 * поодинці межі не сягає.
 *
 * ⛔ Динамічні імпорти НЕ входять у вагу МАРШРУТУ, і це не поблажка. `import()` —
 * це те, чого браузер на цьому маршруті може й не завантажити взагалі; порахувати
 * його означало б, що Monaco (818 КБ gzip) ламає бюджет п'ятнадцяти сторінок,
 * які про нього не знають. Саме заради цієї межі він і винесений.
 *
 * ⛔ Але «не входить у маршрут» не означало «без ліміту» (PSS-20): Monaco міг би
 * вирости з 0.8 до 1.1 МБ (`editor.main` замість поіменних внесків, див.
 * `04-environment.md`) — і жоден гейт цього б не помітив. Тому окремо стережеться
 * ДОДАТКОВА вага кожного динамічного чанка, який НЕ є маршрутом: усе, що він
 * тягне понад вхідний чанк і його статичні залежності (те, що вже в браузері).
 *   - загальна стеля `DynamicCeiling` — 190 КБ gzip (найважчий нині не-Monaco —
 *     `RowLockDialog` / внутрішні входи RevoGrid, ≈ 170 КБ, +12 % запасу);
 *   - поіменні стелі `DynamicCeilings` — для того, що свідомо важчий. Нині лише
 *     `monaco`: виміряно 795.5 КБ → стеля 820 КБ (+3 %). Піднімати стелю можна
 *     тільки разом із записаним рішенням, чому чанк став важчим.
 * Стеля стоїть на виміряному значенні з малим запасом, а не «з запасом на
 * майбутнє»: запас на майбутнє — це і є відсутність ліміту.
 */
import { gzipSync } from 'node:zlib';
import { readFileSync, existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const dist = path.resolve(here, '..', 'dist');
const manifestPath = path.join(dist, '.vite', 'manifest.json');

/** Межа з `D-132`, у байтах gzip. */
const Budget = 250 * 1024;

/**
 * Маршрути, які перевищували бюджет ДО появи цього гейту.
 *
 * ⛔ Це не звільнення. Кожен рядок тут — визнане порушення `D-132` зі СВОЄЮ
 * стелею, поставленою на виміряному значенні: маршрут не має права рости далі
 * ані на кілобайт, а прибрати рядок можна лише опустивши маршрут під 250.
 *
 * ⚠ Звільнення виглядало б інакше — «цей маршрут не перевіряємо», — і саме
 * воно гірше за відсутню перевірку: порушення зникло б з очей, лишившись у
 * збірці. Тут воно щоразу друкується і щоразу назване порушенням.
 */
const Grandfathered = {
  // ⛔ Порожньо — і це стан, а не заготовка. `DocumentPage` стояв тут із
  // 2026-09-06 зі стелею 259 КБ і строком «до UAT». Ліки, названі в тому ж
  // записі, застосовані 2026-09-07: `DocumentGrid` винесений у `lazy()`,
  // ядро `RevoGrid` пішло власним чанком, маршрут — **216.8 КБ**.
  //
  // ⚠ Рядок прибрано за єдиною законною підставою: маршрут опустився під
  // межу. Прибрати його інакше означало б зробити зі стелі звільнення.
};

/** Загальна стеля додаткової ваги динамічного чанка, що не є маршрутом, КБ gzip. */
const DynamicCeiling = 190 * 1024;

/**
 * Поіменні стелі (за іменем вихідного файла без розширення). ⛔ Кожен рядок —
 * визнано важкий чанк зі СВОЄЮ стелею на виміряному значенні, не звільнення.
 */
const DynamicCeilings = {
  // Monaco, виміряно 795.5 КБ gzip (6 файлів із CSS). `editor.main` — 1 121 КБ.
  monaco: 820 * 1024,
};

if (!existsSync(manifestPath)) {
  console.error(
    `Маніфесту немає: ${manifestPath}\n` +
      'Спочатку `npm run build` — гейт зважує зібране, а не вихідний код.',
  );
  process.exit(1);
}

/** @type {Record<string, {file: string, css?: string[], imports?: string[], isEntry?: boolean, isDynamicEntry?: boolean, src?: string}>} */
const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));

const gzipCache = new Map();

/** Розмір файла зі збірки в байтах після gzip. */
function gzipSize(file) {
  const cached = gzipCache.get(file);
  if (cached !== undefined) return cached;

  const size = gzipSync(readFileSync(path.join(dist, file)), { level: 9 }).length;
  gzipCache.set(file, size);

  return size;
}

/**
 * Транзитивне замикання СТАТИЧНИХ імпортів чанка разом із його CSS.
 *
 * ⚠ Через `seen`, а не рекурсією наосліп: граф чанків має цикли (сторінка
 * імпортує вхідний чанк, який колись імпортує спільний код сторінки), і
 * простий обхід не завершився б.
 */
function closureOf(key, seen = new Set()) {
  if (seen.has(key)) return seen;
  seen.add(key);

  const chunk = manifest[key];
  if (chunk === undefined) return seen;

  for (const next of chunk.imports ?? []) {
    closureOf(next, seen);
  }

  return seen;
}

/** Сумарна вага маршруту: усі чанки замикання плюс їхній CSS. */
function weigh(key) {
  const files = new Set();

  for (const chunkKey of closureOf(key)) {
    const chunk = manifest[chunkKey];
    if (chunk === undefined) continue;

    files.add(chunk.file);
    for (const css of chunk.css ?? []) files.add(css);
  }

  let total = 0;
  for (const file of files) total += gzipSize(file);

  return { total, files: files.size };
}

// ── Маршрути — це те, що вантажить РОУТЕР ───────────────────────────────────
// ⛔ Не всі `isDynamicEntry`, а саме динамічні імпорти вхідного чанка. Різниця
// не косметична: під `isDynamicEntry` підпадають і внутрішні входи RevoGrid, і
// сам Monaco, тобто речі, які вантажаться ВЖЕ ПІСЛЯ показу маршруту. Порахувати
// їх означало б вимагати від бюджету відповідальності за те, чого людина на
// цьому маршруті може взагалі не дочекатися.
//
// ⚠ Перелік бере себе сам: додали маршрут у `router.tsx` — він з'явився тут,
// без жодної правки цього файлу. Список маршрутів, який треба доповнювати
// руками, одного дня відстав би від роутера і мовчки перестав би щось стерегти.
const entryKey = Object.keys(manifest).find((key) => manifest[key].isEntry === true);
const routes = entryKey === undefined ? [] : (manifest[entryKey].dynamicImports ?? []);

if (routes.length === 0) {
  console.error('Вхідний чанк не тягне жодного маршруту — перевіряти нічого.');
  process.exit(1);
}

/**
 * Стійке ім'я маршруту.
 *
 * ⚠ Хеш вмісту з імені файла ЗРІЗАЄТЬСЯ: він міняється на кожній правці, і
 * будь-який перелік, що спирався б на нього, застарів би з першою ж збіркою.
 */
function nameOf(key) {
  const source = manifest[key].src;
  if (source !== undefined) return path.basename(source).replace(/\.tsx?$/, '');

  return path.basename(key).replace(/^_/, '').replace(/-[A-Za-z0-9_-]{7,8}\.js$/, '');
}

const rows = routes
  .map((key) => ({ key, name: nameOf(key), ...weigh(key) }))
  .sort((a, b) => b.total - a.total);

const kb = (bytes) => (bytes / 1024).toFixed(1).padStart(7);
const failures = [];

console.log(`Бюджет маршруту (D-132): ${(Budget / 1024).toFixed(0)} КБ gzip\n`);

for (const row of rows) {
  const held = Grandfathered[row.name];
  const limit = held?.limit ?? Budget;
  const failed = row.total > limit;

  if (failed) failures.push(row);

  // ⛔ Визнане порушення друкується як порушення — «!», а не «✓». Позначити
  // його галочкою означало б, що через рік ніхто не згадає, що воно є.
  const mark = failed ? '✗' : held === undefined ? '✓' : '!';
  const note =
    held === undefined ? '' : `  ← порушення з ${held.since}, строк: ${held.until}`;

  console.log(
    `${mark} ${kb(row.total)} КБ  ${row.name}  (${String(row.files)} файлів)${note}`,
  );
}

for (const [name, held] of Object.entries(Grandfathered)) {
  if (!rows.some((row) => row.name === name)) continue;
  console.log(`\n! ${name}: ${held.why}`);
}

if (failures.length > 0) {
  console.error(`
Перевищено межу: ${String(failures.length)}.`);
  console.error('Винести важке в `import()` — так, як винесений Monaco.');
}

// ── Динамічні чанки, що не є маршрутами (PSS-20) ────────────────────────────
// Додаткова вага = файли замикання чанка МІНУС файли, які вже завантажені
// вхідним чанком. Інакше кожен динамічний чанок «важив» би ще й вхідні 173 КБ.
const routeKeys = new Set(routes);
const loadedUpFront = new Set();
for (const chunkKey of closureOf(entryKey)) {
  const chunk = manifest[chunkKey];
  if (chunk === undefined) continue;
  loadedUpFront.add(chunk.file);
  for (const css of chunk.css ?? []) loadedUpFront.add(css);
}

function weighExtra(key) {
  const files = new Set();
  for (const chunkKey of closureOf(key)) {
    const chunk = manifest[chunkKey];
    if (chunk === undefined) continue;
    for (const file of [chunk.file, ...(chunk.css ?? [])]) {
      if (!loadedUpFront.has(file)) files.add(file);
    }
  }
  let total = 0;
  for (const file of files) total += gzipSize(file);

  return { total, files: files.size };
}

// ⚠ Чанк, який статично входить у замикання іншого динамічного чанка з поіменною
// стелею (`_editor.api` усередині Monaco), вже зважений там: рахувати його вдруге
// проти загальної стелі означало б хибне порушення на частині того самого Monaco.
const coveredByNamed = new Set();
for (const key of Object.keys(manifest)) {
  if (manifest[key].isDynamicEntry !== true || routeKeys.has(key)) continue;
  if (DynamicCeilings[nameOf(key)] === undefined) continue;
  for (const inner of closureOf(key)) {
    if (inner !== key) coveredByNamed.add(inner);
  }
}

const dynamicRows = Object.keys(manifest)
  .filter(
    (key) =>
      manifest[key].isDynamicEntry === true && !routeKeys.has(key) && !coveredByNamed.has(key),
  )
  .map((key) => ({ key, name: nameOf(key), ...weighExtra(key) }))
  .sort((a, b) => b.total - a.total);

const dynamicFailures = [];
console.log(
  `
Динамічні чанки поза маршрутами (PSS-20): стеля ${(DynamicCeiling / 1024).toFixed(0)} КБ gzip, ` +
    `поіменні: ${Object.entries(DynamicCeilings).map(([n, v]) => `${n} ${(v / 1024).toFixed(0)}`).join(', ')} КБ
`,
);

const heaviestUnnamed = dynamicRows.findIndex((row) => DynamicCeilings[row.name] === undefined);

// Друкуємо найважчий без поіменної стелі, усі з поіменною стелею й усі порушення: сотні дрібних рядків — шум.
for (const [index, row] of dynamicRows.entries()) {
  const limit = DynamicCeilings[row.name] ?? DynamicCeiling;
  const failed = row.total > limit;
  if (failed) dynamicFailures.push(row);
  if (failed || index === heaviestUnnamed || DynamicCeilings[row.name] !== undefined) {
    console.log(
      `${failed ? '✗' : '✓'} ${kb(row.total)} КБ  ${row.name}  (${String(row.files)} файлів, стеля ${(limit / 1024).toFixed(0)})`,
    );
  }
}

// ⚠ Поіменна стеля на чанк, якого вже немає, — мертвий рядок, який мовчки
// перестав щось стерегти (Monaco перейменували, а стеля лишилась).
for (const name of Object.keys(DynamicCeilings)) {
  if (!dynamicRows.some((row) => row.name === name)) {
    console.error(`
✗ Поіменна стеля «${name}» не відповідає жодному динамічному чанку.`);
    dynamicFailures.push({ name, total: 0 });
  }
}

if (dynamicFailures.length > 0) {
  console.error(`
Динамічних чанків над стелею: ${String(dynamicFailures.length)}.`);
}

if (failures.length > 0 || dynamicFailures.length > 0) process.exit(1);

console.log(
  `
Усі ${String(rows.length)} маршрутів і ${String(dynamicRows.length)} динамічних чанків у своїх межах.`,
);

import { Profiler, type JSX, type ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { AdminLayout } from '@/app/AdminLayout';
import { AppLayout } from '@/app/AppLayout';
import { RouteGuard } from '@/app/RouteGuard';
import { TemplateVersionLayout } from '@/app/TemplateVersionLayout';
import { childPath, relativePath, routes, type RouteEntry } from '@/app/routes';
import { DocumentPage } from '@/pages/DocumentPage';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { MethodologyVersionsPage } from '@/pages/admin/MethodologyVersionsPage';
import { TemplateVersionPage } from '@/pages/admin/TemplateVersionPage';
import { UnitsPage } from '@/pages/admin/UnitsPage';
import { TableSlotAttribute } from '@/features/grid/SheetTables';
import { DocumentTableFixture, emptyBodyFor, registerGridLayout } from './a11yFixtures';
import { testTheme } from '@/test/render';

/**
 * Сторож КЛАСУ «зворотний зв'язок під час рендера» на рівні МАРШРУТУ.
 *
 * ⛔ ЧОМУ ЦЕЙ ФАЙЛ ІСНУЄ. За один день знайдено три дефекти одного класу
 * (#295, #299, #305), і жодного з них не побачив ані `tsc`, ані `npm run
 * lint`, ані 1800+ модульних тестів, ані сім гейтів CI. Усіх трьох спіймала
 * лише перевірка «як у користувача» — по одному, поштучно, на живому стенді.
 * Спільна форма всіх трьох: щось у піддереві маршруту породжує РОБОТУ
 * РЕНДЕРА як побічний ефект самого рендера (конструктор `QueryObserver`
 * усередині `useState(() => …)`, межа `<Suspense>` над великою кількістю
 * компонентів, ефект, що перестворює спостерігача на власний же стан), і цикл
 * розривається лише випадково.
 *
 * ⚠ Що тут НЕ перевіряється і чому — окремим розділом нижче
 * («Чого цей сторож НЕ ловить»). Сторож, про межі якого мовчать, гірший за
 * його відсутність: наступного разу на нього спираються там, де він сліпий.
 *
 * ### Три перевірки, і лише дві з них щось ловлять
 *
 * ⛔ Порядок нижче — це порядок ЧЕСНОСТІ, а не важливості: спершу те, що
 * доведено мутацією, потім те, що НЕ доведено. Перевірка (б) була вихідною
 * пропозицією до цієї картки, і вона НЕ ПРАЦЮЄ так, як від неї очікували;
 * замовчати це означало б лишити по собі сторожа, на якого спираються там,
 * де він сліпий.
 *
 * **(а) Подія кешу, що нічого не змінює, не коштує жодного коміту. ПРАЦЮЄ.**
 * Узагальнення того, що `app/__tests__/Breadcrumbs.test.tsx` уже доводить для
 * САМИХ крихт, на ПОВНЕ дерево застосунку (`AppLayout` + крихти + піддерево
 * маршруту). Причина дефекту #305 була саме така: підписник `QueryCache`
 * замовляв рендер на КОЖНУ подію кешу, а кожен `useQuery`, що вперше
 * монтується, синхронно кличе `queryCache.notify()` ПІД ЧАС рендера. Подія
 * «з'явився спостерігач чужого запиту» не змінює на екрані нічого — отже й
 * коштувати вона має НУЛЬ.
 *
 * **(в) Змонтовано рівно те, що видно. ПРАЦЮЄ.**
 * Не лічильник, а спостережуваний наслідок: нуль сіток, доки спостерігач
 * мовчить, і рівно стільки сіток, скільки слотів він назвав. Саме цю
 * обіцянку ламали #295 і #299 — «сітка не з'являлася НІКОЛИ» і «91 запит
 * зрізу заради двох видимих таблиць». Ця перевірка — єдина з трьох, що
 * почервоніла на мутації #295.
 *
 * **(б) Стеля комітів на монтування маршруту. НЕ ЛОВИТЬ ЖОДНОЇ З ТРЬОХ
 * ПРИЧИН — лишена як запобіжник від нескінченного циклу, не як сторож цього
 * класу.**
 * ⚠ Сам livelock #305 у jsdom НЕ відтворюється, і це не недогляд: під `act()`
 * React рендерить синхронно до кінця — немає ані конкурентних зрізів по 5 мс
 * (`renderRootConcurrent` → `shouldYield`), ані паузи між ними, у яку
 * прилітала мікрозадача. Надія була на те, що необмежений зворотний зв'язок
 * усе одно дасть коміти на порядок понад здоровий маршрут. ЗАМІР ЦЕ
 * СПРОСТУВАВ: на мутації #305 коміти виросли з 10 до 11 (тобто на одиницю),
 * а на мутації #295 — ВПАЛИ, з 18 до 16 і з 21 до 14. Тобто хворе значення
 * буває МЕНШИМ за здорове, і жодна стеля не розділить їх у принципі.
 * ⚠ Перевірка лишається рівно з однією метою: цикл, що комітить БЕЗ КІНЦЯ,
 * інакше висить до таймауту тесту (30 с мовчазного очікування замість
 * повідомлення з числом). Ціни вона не має — коміти й так рахуються для (а).
 * ⛔ Не читати її як доказ відсутності дефекту цього класу.
 *
 * ### Чому ці маршрути, а не всі
 *
 * ⛔ Повний обхід реєстру вже роблять перевірки доступності — 24 маршрути × 2
 * схеми, ~26 хв. Другого такого набору тут не буде. Критерій вибірки
 * (механічний, перевіряється грепом, а не смаком):
 *
 *   1. **Піддерево кожного вже знайденого дефекту цього класу**:
 *      `/documents/:id` (#295 і #299) і `/admin/units` (#305 — саме цей
 *      маршрут не рендерився зовсім на живому стенді).
 *   2. **Два маршрути з найбільшою кількістю спостерігачів, створюваних ПІД
 *      ЧАС рендера** (`useQuery` + `useMutation` у файлі сторінки — кожен з
 *      них це синхронний `queryCache.notify()` з тіла рендера, тобто сам
 *      спусковий гачок класу): `TemplateVersionPage` — 19,
 *      `PeriodsPage` — 10. Далі йдуть `SecurityPage` (6),
 *      `RegistryConstructorPage`/`SnapshotsPage` (5) — вони не взяті
 *      навмисно, щоб набір лишався чотирма маршрутами, а не двадцятьма
 *      чотирма.
 *
 * ### Зміряні числа (ця машина, Node 24.19.0, jsdom, три однакові прогони)
 *
 *   маршрут                                 коміти дерева   коміти маршруту
 *   /admin/units                                  10               4
 *   /admin/periods                                10               4
 *   /admin/templates/1/versions/7                 11               5
 *   /documents/1, 91 таблиця, нічого не видно     18               7
 *   /documents/1, 91 таблиця, видно 2 слоти       21              12
 *
 * Найбільше здорове значення — 21; стеля {@link CommitCeiling} = 40 (запас
 * ×1.9). ⚠ Це запас від здорового значення, а НЕ середина розриву між
 * здоровим і хворим: розриву немає (див. (б) вище).
 *
 * ### 2026-09-18, `G-04`: чому цей файл плавав — і чому це був дефект ТЕСТА
 *
 * ⚠ Гейт `client` падав тут зрідка (`PR #321` змінював лише `CLAUDE.md` і
 * `.ps1`) з `{ tree: 1, route: +0 }`. Причина — не в застосунку: останній
 * коміт монтування завжди приходив від таймера `AppShell` на 200 мс
 * ({@link SettleMs} — увесь розбір і числа), а бюджет осідання рахувався в
 * ТАКТАХ, які реального часу не обмежують. Коли 30 тактів укладалися менш ніж
 * у 200 мс, цей коміт падав уже у вікно виміру події кешу.
 *
 * ⛔ Що це означає про продукт: НІЧОГО. `data-resizing` — власна розмітка
 * `AppShell` на час CSS-переходу; вона знімається один раз і піддерева
 * маршруту не торкається. Перевірка (а) досі правдива — вона просто міряла
 * не те вікно.
 *
 * ### Доказ: сторож червоніє на СПРАВЖНІХ причинах
 *
 * Кожну причину повертали в дерево ОКРЕМО, міряли, відкочували
 * (`git checkout --`). Дослівні числа:
 *
 * **#305 — ЗЛОВЛЕНО.** У `Breadcrumbs.useCacheVersion` прибрано порівняння
 * відбитків (`if (next === painted.current) return;`), тобто повернуто
 * безумовний `setVersion`. Перевірка (а) червоніє на ВСІХ П'ЯТИ тестах:
 * `expected { tree: 1, route: +0 } to deeply equal { tree: +0, route: +0 }`.
 * (б) при цьому зелена — коміти монтування 10 → 11.
 *
 * **#295 — ЗЛОВЛЕНО.** У `DocumentPage` повернуто `React.lazy` +
 * `<Suspense>` навколо `active.tables.map(...)` з лінивим `DocumentGrid`.
 * Червоніє перевірка (в), обидва тести `/documents/1`:
 * `expected … to have a length of +0 but got 91` і
 * `expected … to have a length of 2 but got 91`. Тобто механізм лінивого
 * монтування обійдено цілком: 91 сітка і 91 запит зрізу там, де видно дві
 * таблиці. (а) і (б) при цьому зелені.
 *
 * ⚠ ЧЕСНО ПРО МЕХАНІЗМ #295 у jsdom: він НЕ той самий, що на стенді. На
 * стенді 91 сітка не вкладалася в конкурентний зріз і не з'являлася ніколи;
 * тут вони всі спокійно монтуються. Сторож ловить не livelock, а ПОРУШЕНУ
 * ОБІЦЯНКУ, яку та сама правка ламає й у браузері («монтуємо лише те, що
 * видно»). Це доказ того, що сторож червоніє на цій правці, а не доказ того,
 * що jsdom уміє livelock.
 *
 * ### Чого цей сторож НЕ ловить
 *
 * ⛔ **Жодної з двох задокументованих причин #299.** Обидві повертали в
 * дерево й міряли — числа ЗБІГЛИСЯ зі здоровими до одиниці (18/7/0 і
 * 21/12/2):
 *
 *   • `SheetTables`: `return next.size === previous.size ? previous : next`
 *     → `return next` (завжди новий `Set`). Зміни немає, і причина не в
 *     сторожі: щойно слот змонтовано, його БІЛЬШЕ НЕ СПОСТЕРІГАЮТЬ
 *     (`if (mounted.has(table.tableInstanceId)) continue`), тож справжній
 *     браузер не може повідомити про нього вдруге. Умова спрацьовує лише
 *     тоді, коли ТОЙ САМИЙ живий спостерігач повідомляє про слот двічі до
 *     того, як React перемалював, — і рівно цей випадок уже стереже
 *     `features/grid/__tests__/SheetTables.lazy.test.tsx` («повторна поява
 *     вже змонтованої таблиці…»), подаючи подію поіменно. Дублювати його тут
 *     означало б другу копію тієї самої перевірки, а не ширше покриття.
 *
 *   • `DocumentPage`: `useMemo(() => groupBySheet(…), [tables.data])` →
 *     прямий виклик (нестабільний проп `tables`). Теж без різниці: після
 *     осідання сторінка більше не перемальовується, тож ефект зі
 *     спостерігачем не перезапускається, і черга «створили → знищили →
 *     створили» не утворюється. Щоб це проявилося, потрібен потік
 *     ПЕРЕКРИВНИХ у часі відповідей (три запити сторінки приходять із
 *     затримкою один за одним) — у заглушці мережі вони приходять майже
 *     одночасно. Це обмеження ЗАГЛУШКИ, а не React: щоб закрити цю причину,
 *     потрібна перевірка з керованими затримками відповідей, і це окрема
 *     картка, а не рядок у цьому файлі.
 *
 * ⛔ **Livelock як такий** — див. (б) вище: у jsdom під `act()` його немає з
 * чого зробити. Єдине, що тут лишається його слідом, — числа комітів.
 *
 * ### Ціна
 *
 * `npm test`: 33.2 с без цього файлу → 40.1 і 41.1 с із ним (два прогони
 * підряд), тобто **+7-8 с**, ~+21 %. Сам файл — 7.5 с на п'ять тестів.
 * ⚠ Це свідома межа набору: ще двадцять маршрутів коштували б хвилини й
 * зробили б з цього другий набір доступності (~26 хв), заради якого його
 * перестали б ганяти — а перевірка, яку не ганяють, тихо гниє.
 *
 * ⚠ {@link SettleMs} (2026-09-18) додав щонайбільше 400 мс на маршрут, і то
 * лише коли {@link EventLoopTurns} тактів устигли пройти швидше. Зміряно на
 * цій машині: файл сам по собі 7.0/7.7/7.8 с до правки і 6.8/8.8/10.7 с
 * після — тобто різниця тоне в розкиді самої машини.
 */

/**
 * Скільки тактів циклу подій дати маршрутові осісти після монтування.
 *
 * ⛔ Фіксоване число, а не «крутити, доки не стихне». Адаптивне осідання
 * пробували першим, і воно ЗАМАСКУВАЛО дефект: на мутації #295 воно
 * оголошувало маршрут осілим на 4-му такті, а 55 комітів прилітали далі —
 * тобто сторож зеленів рівно на тому, заради чого написаний. Фіксований
 * бюджет рахує все, що встигло статися, і не залежить від того, чи вгадав
 * евристичний детектор тиші.
 */
const EventLoopTurns = 30;

/**
 * Скільки РЕАЛЬНОГО часу маршрут має осідати, окремо від {@link EventLoopTurns}.
 *
 * ⛔ Це не «піднятий поріг», а виправлення причини плаваючого падіння (`G-04`,
 * директива №14 §4.5). Причина — в самому тесті, не в застосунку, і вона
 * зміряна, а не припущена.
 *
 * `AppShell` (Mantine) заводить у власному layout-ефекті на монтуванні
 * `window.setTimeout(() => startTransition(() => setResizing(false)),
 * transitionDuration)`, а `transitionDuration` за замовчуванням — **200 мс**
 * (`@mantine/core/…/AppShell/use-resizing/use-resizing.mjs`,
 * `AppShell.mjs: defaultProps`). Спрацювавши, таймер знімає з кореня атрибут
 * `data-resizing="true"` — рівно ОДИН коміт дерева, який НЕ перемальовує
 * піддерево маршруту (елемент `<Outlet/>` той самий за посиланням, React
 * виходить із піддерева), тобто `{ tree: 1, route: 0 }` — дослівно те число,
 * яким падав `client` на PR #321.
 *
 * ⛔ Чому цього не бачив фіксований бюджет тактів: {@link EventLoopTurns} —
 * бюджет ТАКТІВ, а не часу. Один такт (`act` + `setTimeout(0)`) коштує від
 * ~1 мс на тихій машині до ~10 мс під навантаженням, тож 30 тактів — це від
 * ~40 мс до ~350 мс. Коли вони вкладаються менш ніж у 200 мс, таймер
 * `AppShell` на момент знімка ЩЕ НЕ спрацював і спрацьовує вже всередині
 * вікна виміру `foreignCacheEvent` — і коміт, що не має до кешу жодного
 * стосунку, зараховується події кешу. Зміряно прямо: у послідовних прогонах
 * того самого файлу зняття `data-resizing` припадало на такт #1, #10, #11,
 * #12, #19, #21 і #24 — тобто розкид у двадцять тактів на незмінному коді.
 *
 * ⚠ 400 мс — подвійний запас від 200 мс, і це запас від ЄДИНОГО відомого
 * таймера монтування, а не від «здорового значення». Якщо колись доведеться
 * його підняти — спершу з'ясувати, який ще таймер оселився в каркасі.
 */
const SettleMs = 400;

/**
 * Стеля комітів дерева на одне монтування маршруту — ЗАПОБІЖНИК, не сторож.
 *
 * ⛔ Не читати як «менше за 40 — значить здорово». Жодна з трьох відтворених
 * причин цієї стелі не пробила (розбір — у коментарі файлу, розділ (б)):
 * #305 дав 11 замість 10, #295 — 16 і 14 замість 18 і 21, тобто МЕНШЕ за
 * здорове. Розділити цим числом здорове й хворе неможливо, і це зміряно, а
 * не припущено.
 *
 * ⚠ Навіщо тоді число. Рівно щоб цикл, який комітить без кінця, впав із
 * повідомленням і числом, а не завис до таймауту тесту. 40 — це ×1.9 від
 * найважчого здорового значення вибірки (21).
 *
 * ⛔ Якщо колись доведеться це число ПІДНЯТИ — це не обслуговування сторожа,
 * а знахідка: маршрут почав коштувати вдвічі більше комітів, ніж коштував
 * увесь застосунок разом. Спершу з'ясувати чому, і лише тоді чіпати число.
 */
const CommitCeiling = 40;

/** Скільки таблиць на аркуші має документ чинного розміру (`DistributionProfile.cs`). */
const TablesPerSheet = 91;

/**
 * Слоти, про появу яких повідомляє спостерігач.
 *
 * ⚠ Рівно два — стільки таблиць по 70vh поміщається в екран. Заглушка
 * `src/test/setup.ts` інертна за побудовою (у jsdom немає розкладки, тож «що
 * видно» там не визначене), і це правильно для НЕЇ; тут подію задає сам
 * тест поіменно — тобто не вигадує видимість, а подає зовнішню подію, як це
 * зробив би браузер.
 */
const VisibleSlots = [1, 2];

const bigSheet = Array.from({ length: TablesPerSheet }, (_, index) => ({
  ...DocumentTableFixture,
  tableCode: `T${String(index + 1)}`,
  tableDefId: index + 1,
  tableInstanceId: index + 1,
  tableNameL10n: { values: { en: `Table ${String(index + 1)}` } },
  tableOrdinal: index,
}));

registerGridLayout();

beforeEach(() => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      // ⛔ Документ чинного розміру, а не одна таблиця: #295 не відтворюється
      // на одній — на стенді сітка з'являлася за 3 с аж до п'яти таблиць і не
      // з'являлася ніколи від шести (`SheetTables.tsx`). Перевірка на одній
      // таблиці була б перевіркою іншого документа.
      const body = /\/documents\/[^/?]+\/tables\?/.test(url) ? bigSheet : emptyBodyFor(url);

      return Promise.resolve(
        new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

/** Лічильники комітів: усього дерева застосунку і окремо піддерева маршруту. */
interface Ledger {
  tree: number;
  route: number;
}

function RouteProfiler({ ledger, children }: { ledger: Ledger; children: ReactNode }): JSX.Element {
  return (
    <Profiler
      id="route"
      onRender={() => {
        ledger.route += 1;
      }}
    >
      {children}
    </Profiler>
  );
}

/**
 * Дерево маршрутів для одного запису реєстру — тієї самої форми вкладеності,
 * що й `app/router.tsx`.
 *
 * ⚠ Шлях і `handle` беруться з реєстру (`routes.ts`) тими самими
 * `childPath()`/`relativePath()`, що й у продукті: другого рядкового літерала
 * маршруту тут не набирається — інакше сторож стеріг би адресу, якої в
 * застосунку немає.
 *
 * ⛔ Каркас справжній (`AppLayout`), не спрощений, і саме тому, що підписник
 * `QueryCache`, з якого почався #305, живе САМЕ в ньому (`<Breadcrumbs/>`).
 * Піддерево маршруту без каркаса не мало б чим зламатися.
 */
function routerFor(entry: RouteEntry, page: JSX.Element, url: string, ledger: Ledger) {
  const element = (
    <RouteProfiler ledger={ledger}>
      <RouteGuard handle={entry.handle}>{page}</RouteGuard>
    </RouteProfiler>
  );

  if (entry.path.startsWith('/admin/templates/:id/')) {
    return createMemoryRouter(
      [
        {
          path: '/',
          element: <AppLayout />,
          children: [
            {
              path: 'admin',
              element: <AdminLayout />,
              children: [
                {
                  path: 'templates/:id',
                  element: <TemplateVersionLayout />,
                  handle: routes.adminTemplateSection.handle,
                  children: [
                    {
                      path: relativePath(entry, 'admin/templates/:id'),
                      element,
                      handle: entry.handle,
                    },
                  ],
                },
              ],
            },
          ],
        },
      ],
      { initialEntries: [url] },
    );
  }

  const leaf = entry.path.startsWith('/admin/')
    ? {
        path: 'admin',
        element: <AdminLayout />,
        children: [{ path: relativePath(entry, 'admin'), element, handle: entry.handle }],
      }
    : { path: childPath(entry), element, handle: entry.handle };

  return createMemoryRouter([{ path: '/', element: <AppLayout />, children: [leaf] }], {
    initialEntries: [url],
  });
}

async function turns(count: number): Promise<void> {
  for (let index = 0; index < count; index += 1) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
  }
}

interface Mounted {
  readonly ledger: Ledger;
  readonly queryClient: QueryClient;
}

/**
 * Осідання маршруту: {@link EventLoopTurns} тактів, потім — доки від моменту
 * `scheduledAt` не мине {@link SettleMs} реального часу, і ще раз
 * {@link EventLoopTurns} тактів.
 *
 * ⛔ Умова лише ДОДАЄ очікування й ніколи не скорочує його: бюджет тактів
 * лишився той самий, і жоден коміт, який рахувався раніше, не перестав
 * рахуватися. Це не адаптивне осідання, від якого файл відмовився вище
 * (воно зупинялося РАНІШЕ за фіксований бюджет і тим замаскувало #295), —
 * тут немає жодної евристики тиші, лише два фіксовані числа.
 */
async function settle(scheduledAt: number): Promise<void> {
  await turns(EventLoopTurns);

  // ⚠ Одна пауза, а не {@link SettleMs} тактів по одному: такт для найважчого
  // маршруту вибірки коштує помітно більше за мілісекунду, і «крутити такти,
  // доки не мине час» коштувало б удвічі дорожче за сам файл. Пауза всередині
  // `act()` рівноцінна: коміти, що припадуть на неї, React зведе на виході.
  const remaining = SettleMs - (Date.now() - scheduledAt);
  if (remaining > 0) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, remaining));
    });
  }

  await turns(EventLoopTurns);
}

async function mountRoute(entry: RouteEntry, page: JSX.Element, url: string): Promise<Mounted> {
  const ledger: Ledger = { tree: 0, route: 0 };
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  await act(async () => {
    render(
      <Profiler
        id="tree"
        onRender={() => {
          ledger.tree += 1;
        }}
      >
        <MantineProvider theme={testTheme}>
          <QueryClientProvider client={queryClient}>
            <RouterProvider router={routerFor(entry, page, url, ledger)} />
          </QueryClientProvider>
        </MantineProvider>
      </Profiler>,
    );
  });

  // ⚠ Відлік {@link SettleMs} починається САМЕ ТУТ, а не до `render()`:
  // таймер `AppShell` заводиться в layout-ефекті, тобто всередині виклику
  // вище, і для `/documents/1` сам цей виклик коштує 200-300 мс. Відлік від
  // початку монтування з'їв би весь запас на найважчому маршруті вибірки.
  await settle(Date.now());

  return { ledger, queryClient };
}

/**
 * Подія `QueryCache`, що не змінює нічого видимого.
 *
 * ⚠ Той самий шлях сповіщення (`build()` → `add()` → синхронний `notify()`),
 * яким будить підписників будь-який `useQuery` чужої сторінки, що вперше
 * монтується для нового ключа — тобто КОЖЕН перехід між сторінками. Ключ
 * навмисно такий, якого не читає жодна крихта й жодна сторінка вибірки.
 */
async function foreignCacheEvent(queryClient: QueryClient): Promise<void> {
  await act(async () => {
    queryClient.setQueryData(['чужий-ключ-якого-ніхто-не-показує'], { items: [] });
  });
  await turns(2);
}

/**
 * Одне монтування на маршрут, обидві перевірки з нього.
 *
 * ⚠ Не два окремі `it` на маршрут: одне монтування коштує ~0.8 с, а обидві
 * перевірки читають РІЗНІ лічильники ОДНОГО й того самого монтування —
 * другий прогін не дав би жодної нової інформації, лише подвоїв би ціну.
 */
async function guardRoute(entry: RouteEntry, page: JSX.Element, url: string): Promise<void> {
  const { ledger, queryClient } = await mountRoute(entry, page, url);

  expect(
    ledger.tree,
    `(б) ${url}: ${String(ledger.tree)} комітів дерева на монтування при стелі ${String(
      CommitCeiling,
    )} (піддерево маршруту — ${String(ledger.route)}). Здорові значення вибірки — 10-21.`,
  ).toBeLessThanOrEqual(CommitCeiling);

  const tree = ledger.tree;
  const route = ledger.route;

  await foreignCacheEvent(queryClient);

  expect(
    { tree: ledger.tree - tree, route: ledger.route - route },
    `(а) ${url}: подія кешу, що не змінює нічого видимого, коштувала коміти.`,
  ).toEqual({ tree: 0, route: 0 });
}

describe("Зворотний зв'язок під час рендера — стеля комітів і тиша на чужу подію кешу", () => {
  it('/admin/units — маршрут, який не рендерився зовсім (#305)', async () => {
    await guardRoute(routes.adminUnits, <UnitsPage />, '/admin/units');
  }, 60_000);

  it('/admin/periods — 10 спостерігачів, створюваних під час рендера', async () => {
    await guardRoute(routes.adminPeriods, <PeriodsPage />, '/admin/periods');
  }, 60_000);

  it('/admin/templates/:id/versions/:versionId — 19 спостерігачів, максимум застосунку', async () => {
    await guardRoute(
      routes.adminTemplateVersion,
      <TemplateVersionPage />,
      '/admin/templates/1/versions/7',
    );
  }, 60_000);

  /*
   * ✎ 2026-09-19, п'ятий маршрут і НОВИЙ критерій вибірки.
   *
   * ⛔ Два критерії вище («піддерево знайденого дефекту» і «найбільше
   * спостерігачів, створюваних під час рендера») цього маршруту не давали —
   * і саме тому його тут не було. Додано за третім, дописаним разом із ним:
   * **маршрут, у якому щойно з'явилася межа `<Suspense>`**.
   * `MethodologyVersionsPage` винесла сім панелей змісту за `import()` заради
   * запасу бюджету (248.4 → 232.0 КБ), тобто дістала рівно ту конструкцію,
   * якою був #295: `React.lazy` плюс `<Suspense>` над групою компонентів.
   *
   * ⛔ ЧЕСНО ПРО ТЕ, ЩО ЦЕ ДАЄ, і не більше. Розділ (б) вище вже зміряв, що
   * стеля комітів #295 НЕ ловить: на тій мутації коміти не зросли, а ВПАЛИ.
   * Отже цей випадок додає не захист від #295, а рівно дві речі:
   *   • маршрут із новою межею потрапляє під перевірку (а) — чужа подія кешу
   *     не сміє коштувати комітів (це клас #305);
   *   • і під запобіжник (б) — межа, що ввела нескінченний цикл, упаде з
   *     числом, а не повисне до таймауту.
   *
   * ⚠ Що ПАНЕЛІ СПРАВДІ З'ЯВЛЯЮТЬСЯ, цей випадок НЕ доводить: під заглушкою
   * мережі версія не обирається, тож до `selected !== undefined` справа не
   * доходить. Це перевіряється прогоном стенда (`e2e-stand.ps1`) — і саме там
   * свого часу спіймали #295, якого не побачив жоден із 1800 тестів.
   */
  it('/admin/methodologies/:id/versions — маршрут із новою межею <Suspense>', async () => {
    await guardRoute(
      routes.adminMethodologyVersions,
      <MethodologyVersionsPage />,
      '/admin/methodologies/1/versions',
    );
  }, 60_000);

  it('/documents/:id — 91 таблиця, жоден слот не у видимій області (#295)', async () => {
    await guardRoute(routes.documentDetail, <DocumentPage />, '/documents/1');

    // ⛔ Нуль сіток — не «нічого не сталося», а сама суть лінивого монтування
    // (#299): доки спостерігач не сказав, що щось видно, жодного запиту зрізу
    // (найважчий регулярний запит системи) бути не повинно.
    expect(
      document.querySelectorAll('revo-grid'),
      '(в) /documents/1: спостерігач мовчав, а сітки змонтувалися — механізм лінивого монтування обійдено.',
    ).toHaveLength(0);
  }, 60_000);
});

describe("Зворотний зв'язок під час рендера — /documents/:id, коли слоти таки з'явилися", () => {
  /**
   * Спостерігач, що повідомляє про слоти {@link VisibleSlots} у наступному
   * такті після `observe` — так, як справжній браузер віддає ПЕРШИЙ запис
   * кожному новоствореному спостерігачеві.
   *
   * ⚠ Асинхронно (`setTimeout`), а не синхронно з `observe`: синхронне
   * повідомлення — це поведінка, якої в браузері немає, і саме на ній
   * трималася б хибна впевненість, що черга «створили → знищили → створили»
   * безпечна.
   */
  class ViewportObserver {
    private readonly targets = new Set<Element>();

    constructor(private readonly callback: IntersectionObserverCallback) {}

    observe(target: Element): void {
      this.targets.add(target);

      if (!VisibleSlots.includes(Number(target.getAttribute(TableSlotAttribute)))) return;

      setTimeout(() => {
        if (!this.targets.has(target)) return;

        this.callback(
          [{ target, isIntersecting: true } as unknown as IntersectionObserverEntry],
          this as unknown as IntersectionObserver,
        );
      }, 0);
    }

    unobserve(target: Element): void {
      this.targets.delete(target);
    }

    disconnect(): void {
      this.targets.clear();
    }

    takeRecords(): IntersectionObserverEntry[] {
      return [];
    }
  }

  it('монтує рівно видимі слоти і осідає', async () => {
    vi.stubGlobal('IntersectionObserver', ViewportObserver);

    await guardRoute(routes.documentDetail, <DocumentPage />, '/documents/1');

    // ⛔ РІВНО два, а не «хоч одна» і не «менше за 91». Менше — механізм
    // мовчки перестав монтувати те, що видно (порожній екран під заглушками,
    // тобто симптом #295/#299); більше — механізм обійдено, і документ знову
    // робить 91 запит зрізу заради двох видимих таблиць. Мутація #295 дає
    // тут рівно 91.
    expect(
      document.querySelectorAll('revo-grid'),
      `(в) /documents/1: видимих слотів ${String(VisibleSlots.length)}, а сіток у DOM інша кількість.`,
    ).toHaveLength(VisibleSlots.length);
  }, 60_000);
});

import { useEffect, useRef, type RefObject } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useLocation, useMatches } from 'react-router-dom';
import { buildCrumbChain, type CrumbEntry } from './Breadcrumbs';
import {
  routeMotionDuration,
  routeMotionDurationProperty,
  routeMotionEasing,
  routeMotionEasingProperty,
  routeTransitionEnterClassName,
} from './motionTokens';

/** Роздільник частин заголовка вкладки (`«Версія 3.2 · Шаблони · ECR»`). */
const TitleSeparator = ' · ';

/** Суфікс, спільний для кожного маршруту — назва застосунку в заголовку вкладки. */
const AppTitleSuffix = 'ECR';

/**
 * `document.title` із того самого ланцюжка breadcrumbs, що й `<Breadcrumbs
 * />` (`PR nav-arch #3`, `buildCrumbChain`) — НЕ друга система резолву назви
 * маршруту, як прямо вимагає директива цієї картки.
 *
 * ⚠ Порядок — від найглибшого до кореня (`«Версія 3.2 · Шаблони · ECR»`, не
 * навпаки): це той порядок, у якому людина читає заголовок вкладки в списку
 * відкритих вкладок браузера — найважливіше (де я саме зараз) першим, а не
 * останнім за довгим спільним префіксом «ECR».
 *
 * ⚠ Крихти, що ще резолвяться (`text: null`, `Skeleton` у видимих
 * breadcrumbs), пропускаються, а не показуються як `null`/порожній рядок:
 * заголовок вкладки не має проміжного стану гірше за відсутність цієї
 * частини зовсім.
 *
 * ⚠ Без власної підписки на `QueryCache` (на відміну від `<Breadcrumbs/>`,
 * що її МАЄ): під час імплементації друга незалежна підписка `useCacheVersion`
 * у цьому ж дереві (`AppLayout`, де вже є `useSession()` — сама справжній
 * `useQuery`) детерміновано зациклювала рендер (`Maximum update depth
 * exceeded`, відтворено й підтверджено видаленням саме цього виклику —
 * `RouteFallback.test.tsx` падав з ним, минав без). Замість гонитви за
 * точною причиною в надрах TanStack Query, вибір — покластися на те, що
 * `location.pathname` і так примушує `AppLayout` перемалюватися на КОЖНІЙ
 * навігації (сам `useLocation()`), а `useSession()` перемальовує його на
 * кожній зміні профілю: `document.title` рахується заново з АКТУАЛЬНОГО
 * знімку кешу в кожен із цих моментів. Єдина вузька прогалина: якщо перехід
 * на глибокий маршрут стається РІВНО в мить, коли предковий список (чиє ім'я
 * резолвить крихту) ще вантажиться, а жодна інша причина перемалювати
 * `AppLayout` не настає — заголовок вкладки лишиться на статичному
 * `labelKey` довше, ніж видимі breadcrumbs (які свою підписку зберігають).
 * Назване прямо, не приховано: сама крихта (видимий UI) і далі резолвиться
 * коректно через `<Breadcrumbs/>` — прогалина лише в дзеркалі цього тексту
 * в `document.title`.
 */
export function formatDocumentTitle(chain: readonly Pick<CrumbEntry, 'text'>[]): string {
  const parts = chain
    .map((entry) => entry.text)
    .filter((text): text is string => text !== null && text.length > 0)
    .reverse();

  return parts.length > 0 ? `${parts.join(TitleSeparator)}${TitleSeparator}${AppTitleSuffix}` : AppTitleSuffix;
}

/**
 * `matchMedia`, а не лише покладання на CSS-заглушку (`motion.css`,
 * `ФВ-14.28`). Директива цієї картки прямо вимагає: «верифікувати ЧЕРЕЗ
 * `window.matchMedia`», а не довіряти, що анімація, якої ЩЕ немає, колись
 * буде вимкнена лише каскадом CSS — цей виклик перевіряється мутаційним
 * тестом (`useRouteTransitionFocus.test.tsx`) окремо від CSS.
 */
function prefersReducedMotion(): boolean {
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

/**
 * Побічні ефекти зміни МАРШРУТУ, централізовано в `AppLayout.tsx` (`PR
 * nav-arch #7`, директива B6/D):
 *
 * 1. **`document.title`** — з ланцюжка breadcrumbs (вище). Раніше в
 *    застосунку не встановлювався ВЗАГАЛІ (перевірено: жодного
 *    `document.title =` у всьому `src/`) — це єдина частина цієї картки без
 *    наявного часткового механізму.
 * 2. **Резервний фокус на `<main>`** — лише коли ніщо інше не забрало фокус
 *    саме (`PageHeader.tsx`, `Q-261`/`ФВ-14.19`, уже робить це для КОЖНОЇ
 *    з 24 листових сторінок застосунку — перевірено грепом перед цією
 *    карткою). Єдина знайдена прогалина — `AccessDeniedPage`
 *    (`RouteGuard.tsx`), закрита в цій самій картці ТИМ САМИМ прийомом, що
 *    й `PageHeader` (не через цей резерв): резерв тут — страхування на
 *    майбутнє (нова сторінка забуде `PageHeader`), не основний шлях.
 * 3. **CSS-перехід контейнера `<Outlet/>`** — клас знімається й ставиться
 *    заново (форсований reflow), щоб анімація програвалась на КОЖНІЙ
 *    навігації, не лише на першому монтуванні.
 *
 * ⛔ Оголошення (aria-live) НЕ дублюється тут. `PageHeader` уже викликає
 * `announceRoute()` для кожної зі своїх 24 сторінок; другий виклик тут із
 * ІНШИМ текстом (breadcrumb-резолв замість `t(labelKey)` сторінки) означав
 * би подвійне оголошення різними словами на кожній навігації — саме
 * регрес, названий прямо в завданні («a live-region double-announcing»).
 * `AccessDeniedPage` (єдиний виняток) оголошує сам, тим самим механізмом.
 *
 * ⛔ Межа — `location.pathname`, НЕ повний `location` (із `search`).
 * Директива прямо просить не переривати сценарій, де сама сторінка міняє
 * лише query-рядок (фільтр таблиці, `B5`: стан у `searchParams`) — це
 * оновлення URL, а не «перехід на новий екран», і фокус/анімація на
 * кожному натисканні клавіші у фільтрі були б саме тим «переривання дії
 * користувача», якого директива прямо остерігається.
 */
export function useRouteTransitionFocus(mainContentId: string): RefObject<HTMLDivElement | null> {
  const location = useLocation();
  const matches = useMatches();
  const queryClient = useQueryClient();
  const containerRef = useRef<HTMLDivElement>(null);

  // ⚠ Ініціалізовано ПОТОЧНИМ pathname, не порожнім рядком: перший рендер
  // застосунку — не навігація, і фокус/анімація не повинні спрацювати на
  // холодному завантаженні (`PageHeader` самої першої сторінки й так уже
  // забирає фокус при монтуванні — другий гравець тут зайвий).
  const previousPathname = useRef(location.pathname);

  const chain = buildCrumbChain(matches, queryClient);
  const title = formatDocumentTitle(chain);

  // `document.title` — окремий ефект, залежний лише від самого рядка: якщо
  // резолв кеша дав ТОЙ САМИЙ текст (типова ситуація для скелетів, що вже
  // встигли розвʼязатись до першого рендера), зайвого запису в DOM немає.
  useEffect(() => {
    document.title = title;
  }, [title]);

  useEffect(() => {
    if (previousPathname.current === location.pathname) return;
    previousPathname.current = location.pathname;

    const main = document.getElementById(mainContentId);
    if (main !== null && !main.contains(document.activeElement)) {
      main.focus();
    }

    // ⛔ Перевірка ПЕРША, до будь-якого доступу до DOM класу: якщо рух
    // вимкнено, клас НІКОЛИ не додається — не «коротша» анімація, а її
    // повна відсутність, як прямо вимагає директива (п. 3).
    if (prefersReducedMotion()) return;

    const container = containerRef.current;
    if (container === null) return;

    container.style.setProperty(routeMotionDurationProperty, routeMotionDuration);
    container.style.setProperty(routeMotionEasingProperty, routeMotionEasing);

    container.classList.remove(routeTransitionEnterClassName);
    // Форсований reflow: без цього читання браузер побачив би «клас
    // прибрано, клас додано» як одну операцію й не програв би `@keyframes`
    // повторно на другій і кожній наступній навігації.
    void container.offsetWidth;
    container.classList.add(routeTransitionEnterClassName);
  }, [location.pathname, mainContentId]);

  return containerRef;
}

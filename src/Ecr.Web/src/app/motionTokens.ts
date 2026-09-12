import { theme } from '@/shared/theme/theme';

/**
 * Токени руху для переходу між маршрутами (`PR nav-arch #7`, директива B6/D).
 *
 * ⛔ НЕ друге джерело правди. Кожне значення нижче читається з `theme.other`
 * (`shared/theme/theme.ts`) — єдиного дозволеного місця для літералів
 * (виняток ESLint-правила, підтверджений аудитом `Q-282`). Цей файл лише
 * називає, ЯКИЙ токен теми використовує саме маршрутний перехід, і дає
 * числову (мс) форму тривалості для викликів, яким рядок `"150ms"` не
 * підходить (`useRouteTransitionFocus.ts` рахує час, CSS отримує рядок).
 * Друге оголошення тих самих чисел тут означало б рівно ту розбіжність, від
 * якої й рятує єдине джерело: зміни `theme.ts` — і забутий дублікат лишає
 * старе число.
 *
 * ⚠ Тривалість — `theme.other.motionBase` (150 мс), найдовша з двох
 * наявних, а не нова, довша: `ФВ-14.27`/`D-129` прямо забороняють у темі
 * значення понад 150 мс, і маршрутний перехід — не виняток лише тому, що
 * зʼявився пізніше.
 */
const other = theme.other as { motionBase: string; motionEasing: string };

/** CSS-рядок тривалості (`"150ms"`) — для інлайн custom property. */
export const routeMotionDuration = other.motionBase;

/** Та сама тривалість у мілісекундах — для коду, якому потрібне число. */
export const routeMotionDurationMs = Number.parseInt(other.motionBase, 10);

/** Названа крива руху (`"ease-out"`) — не cubic-bezier на кожен виклик. */
export const routeMotionEasing = other.motionEasing;

/** Базовий клас контейнера переходу (`routeTransition.css`). */
export const routeTransitionClassName = 'ecr-route-transition';

/**
 * Клас, що ЗАПУСКАЄ анімацію (`routeTransition.css`, `@keyframes
 * ecr-route-fade-in`). Знімається й ставиться заново на кожній зміні
 * маршруту (`useRouteTransitionFocus.ts`), щоб анімація програлась повторно,
 * а не лише один раз при першому монтуванні контейнера.
 */
export const routeTransitionEnterClassName = 'ecr-route-transition--enter';

/** Назви CSS custom properties, якими контейнер отримує тривалість/криву. */
export const routeMotionDurationProperty = '--ecr-route-motion-duration';
export const routeMotionEasingProperty = '--ecr-route-motion-easing';

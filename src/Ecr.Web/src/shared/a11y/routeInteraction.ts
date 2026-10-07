/**
 * Чи людина вже щось робила на цьому екрані, поки шапка сторінки доїжджала.
 *
 * ⚠ Модульний стан (властивість вкладки, а не компонента): слухачі стоять у фазі перехоплення, щоб
 * побачити подію раніше за будь-який `stopPropagation`.
 *
 * `markRouteStart()` кличе каркас на кожен новий маршрут і при першому монтуванні — ДО ефектів
 * сторінки. Клік на посилання меню, що саме й запускає перехід, трапляється РАНІШЕ за цю мітку, тож
 * «людина вже діє на екрані» його не враховує: перехід, як і раніше, дає фокус заголовку.
 */
let routeStartedAt = 0;
let lastInteractionAt = 0;

if (typeof document !== 'undefined') {
  const record = (): void => {
    lastInteractionAt = performance.now();
  };

  document.addEventListener('keydown', record, true);
  document.addEventListener('pointerdown', record, true);
}

export function markRouteStart(): void {
  routeStartedAt = performance.now();
}

/**
 * Клавіша чи натискання після початку поточного маршруту. Без мітки (шапка поза каркасом,
 * юніт-тест) відповідь «ні»: початок маршруту невідомий.
 */
export function interactedSinceRouteStart(): boolean {
  return routeStartedAt > 0 && lastInteractionAt > routeStartedAt;
}

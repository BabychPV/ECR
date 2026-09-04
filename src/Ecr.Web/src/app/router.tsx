import { createBrowserRouter } from 'react-router-dom';

/**
 * Маршрути застосунку.
 *
 * TODO: створити з lazy-завантаженням сторінок:
 *   /login                              — вхід (два способи: Windows і локальний)
 *   /                                   — список документів
 *   /documents/:id                      — документ, вкладки аркушів, вибір періоду
 *   /admin/templates                    — конструктор шаблонів
 *   /admin/templates/:id/versions/:vid  — редактор структури
 *   /admin/registries                   — конструктор реєстрів
 *   /admin/methodologies                — конфігуратор методологій
 *   /admin/security                     — ролі, користувачі, матриця прав
 *   /admin/periods                      — календар періодів і матриця доступу
 *   /admin/sources                      — конфігуратор джерел і розклад збору
 *   /admin/jobs                         — черга, прогрес, історія перерахунку
 *   /admin/health                       — операційний дашборд і консистентність
 *
 * ⛔ Маршрутів /reports/* НЕМАЄ: звітність лишається в SSRS (D-52).
 */
export const router = createBrowserRouter([
  // TODO: заповнити за описом вище
]);

/**
 * Форматування дат, чисел і множини — ОДНЕ місце на весь клієнт
 * (`D15-08`, `D15-09`, `UI-04`).
 *
 * ⛔ Єдине воно не заради охайності. Розсипані по екранах `toLocaleString()`
 * розходяться між собою мовчки: частина місць бере локаль браузера, частина —
 * жорстко задану, і на одній сторінці з'являються два формати того самого
 * поняття. Саме тому `eslint.config.js` забороняє `toLocale*()` без локалі й
 * нативне `<input type="date">` — а ця тека є тим, чим їх замінюють.
 *
 * Правило місця виклику одне: НЕ звертатися до `Intl` напряму. Потрібен тег
 * локалі для чужого компонента (`DateInput`, `Intl.Collator`) — беріть його з
 * `formatLocale()`, а не з `navigator.language`.
 */

export { formatLocale } from './locale';
export { formatDate, formatDateTime, formatTime, type DateLike } from './datetime';
export { formatNumber } from './number';
export { formatCount, pluralCategory, type PluralCategory } from './plural';

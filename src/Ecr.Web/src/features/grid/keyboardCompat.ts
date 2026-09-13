/**
 * Сумісність клавіші Enter для клавіатур без сучасного `KeyboardEvent.key`
 * (Q-30x, Critical).
 *
 * ⛔ Корінь дефекту (Stage 1, доведено живим RevoGrid у браузері): вбудований
 * текстовий редактор RevoGrid розпізнає Enter ЛИШЕ строгим порівнянням
 * `event.key === 'Enter'` (`@revolist/revogrid`, `TextEditor.onKeyDown` →
 * `key.utils.isEnterKeyValue`) — без запасного варіанту на застарілий
 * числовий `keyCode`/`which`. Джерела вводу, які не заповнюють сучасний
 * `.key` (клавіатурні емулятори сканерів штрих-кодів і термінали збору даних
 * поза стандартною клавіатурою, деякі проксі клавіатури в RDP/Citrix-сесіях,
 * старі IME-прошарки), традиційно шлють ЛИШЕ код клавіші (`keyCode === 13`)
 * — і для такого натискання Enter у RevoGrid не спрацьовує НІЧОГО: ні
 * збереження комірки, ні перехід на наступний рядок. Клітинка лишається у
 * режимі редагування, що виглядає як звичайний незавершений ввід, аж доки
 * комірку не покинути ІНШИМ шляхом (Tab, клік) — а введене без цього губиться
 * мовчки. Tab у RevoGrid перевіряється тим самим способом (`event.key ===
 * 'Tab'`), але лишається надійним практично завжди: браузери й драйвери
 * клавіатури не жертвують `.key` саме для Tab, бо на ньому тримається
 * стандартна фокус-навігація.
 *
 * ⚠ Виправлення — НЕ власний шлях збереження в обхід RevoGrid: друга копія
 * логіки коміту розійшлася б із бібліотечною на першій же зміні її
 * поведінки. Натомість слухач встановлюється на КОНТЕЙНЕРІ з `capture: true`
 * — тобто спрацьовує РАНІШЕ за bubble-обробник, який RevoGrid вішає прямо на
 * `<input>` редактора (фаза capture завжди випереджає фазу «на цілі») — і
 * ДОПИСУЄ `key`, якщо джерело події його не заповнило, але залишило
 * впізнаваний legacy-код. Після цього RevoGrid бачить звичайний Enter і йде
 * своїм звичайним, уже перевіреним шляхом — без дублювання логіки збереження.
 */

/** Мінімальний перетин `KeyboardEvent`, потрібний для розпізнавання. */
export interface EnterLikeKeyEvent {
  readonly key: string;
  readonly keyCode?: number;
  readonly which?: number;
}

/** Код клавіші Enter/Return у застарілому (але й досі повсюдному) `keyCode`/`which`. */
const LEGACY_ENTER_CODE = 13;

/**
 * Чи слід трактувати подію як Enter, навіть якщо сучасний `key` порожній.
 *
 * ⚠ Якщо `key` заповнений і це НЕ `'Enter'` — подія свідомо не Enter (не
 * підміняємо, наприклад, `'Tab'` чи літеру). Легасі-код розглядається лише
 * тоді, коли сучасне поле взагалі відсутнє.
 */
export function isEnterKeyEvent(event: EnterLikeKeyEvent): boolean {
  if (event.key === 'Enter') return true;
  if (event.key.length > 0) return false;

  return event.keyCode === LEGACY_ENTER_CODE || event.which === LEGACY_ENTER_CODE;
}

/**
 * Встановлює нормалізацію Enter на контейнері grid (capture-фаза).
 *
 * @returns Функція відписки — знімає слухача при демонтажі.
 */
export function installEnterKeyCompat(container: HTMLElement): () => void {
  const handler = (event: KeyboardEvent): void => {
    if (event.key !== 'Enter' && isEnterKeyEvent(event)) {
      // ⚠ `key` у `KeyboardEvent.prototype` — лише геттер: власна властивість
      // екземпляра його екранує для БУДЬ-ЯКОГО подальшого читання цієї самої
      // події, включно з обробником RevoGrid нижче за течією (той самий
      // об'єкт події, що й тут).
      Object.defineProperty(event, 'key', { value: 'Enter', configurable: true });
    }
  };

  container.addEventListener('keydown', handler, true);

  return () => container.removeEventListener('keydown', handler, true);
}

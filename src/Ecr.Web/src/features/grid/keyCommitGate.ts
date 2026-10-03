/**
 * Ð¡ÐµÑ€Ñ–Ð°Ð»Ñ–Ð·Ð°Ñ†Ñ–Ñ ÑˆÐ²Ð¸Ð´ÐºÐ¾Ð³Ð¾ Ð²Ð²Ð¾Ð´Ñƒ Ð· ÐºÐ»Ð°Ð²Ñ–Ð°Ñ‚ÑƒÑ€Ð¸ Ð² ÐºÐ¾Ð¼Ñ–Ñ€ÐºÐ°Ñ… ÑÑ–Ñ‚ÐºÐ¸ (T3-01, P2,
 * Ñ€Ð¸Ð·Ð¸Ðº Ð¿ÑÑƒÐ²Ð°Ð½Ð½Ñ Ð´Ð°Ð½Ð¸Ñ…).
 *
 * â›” Ð”ÐµÑ„ÐµÐºÑ‚ (Ñ‚ÐµÑÑ‚ÑƒÐ²Ð°Ð»ÑŒÐ½Ð¸Ðº, Ð¿Ñ€Ð¾Ñ…Ñ–Ð´ 3): Ð¿Ð¾ÑÐ»Ñ–Ð´Ð¾Ð²Ð½Ñ–ÑÑ‚ÑŒ `Enter, 1, Enter, Enter, 2,
 * Enter` Ð±ÐµÐ· Ð¿Ð°ÑƒÐ· ÑÐºÐ»ÐµÑŽÐ²Ð°Ð»Ð° Ð·Ð½Ð°Ñ‡ÐµÐ½Ð½Ñ (`R2.QTY = 12` Ð·Ð°Ð¼Ñ–ÑÑ‚ÑŒ 1 Ñ– 2,
 * `NAME = aaabbbccc`) Ñ– Ð¿Ð¸ÑÐ°Ð»Ð° Ñ—Ñ… ÐÐ• Ð² Ñ‚Ñƒ ÐºÐ¾Ð¼Ñ–Ñ€ÐºÑƒ. Ð— Ð¿Ð°ÑƒÐ·Ð¾ÑŽ â‰¥ 0,1 Ñ ÑƒÑÐµ
 * Ð¿Ñ€Ð°Ñ†ÑŽÐ²Ð°Ð»Ð¾; Ñ‚Ñ€Ð¸Ð³ÐµÑ€Ð¸ â€” ÑÐºÐ°Ð½ÐµÑ€ ÑˆÑ‚Ñ€Ð¸Ñ…-ÐºÐ¾Ð´Ñ–Ð², Ð¼Ð°ÐºÑ€Ð¾Ñ, Ð°Ð²Ñ‚Ð¾Ð²Ð²Ñ–Ð´.
 *
 * â›” ÐŸÐµÑ€ÑˆÐ¾Ð¿Ñ€Ð¸Ñ‡Ð¸Ð½Ð° (RevoGrid 4.11, `revogr-overlay-selection` +
 * `keyboard.service`): Enter Ñƒ Ñ€ÐµÐ´Ð°ÐºÑ‚Ð¾Ñ€Ñ– Ð¡Ð˜ÐÐ¥Ð ÐžÐÐÐž Ð·Ð±ÐµÑ€Ñ–Ð³Ð°Ñ” ÐºÐ¾Ð¼Ñ–Ñ€ÐºÑƒ Ð¹ Ð·Ð°ÐºÑ€Ð¸Ð²Ð°Ñ”
 * Ñ€ÐµÐ´Ð°ÐºÑ‚Ð¾Ñ€, Ð°Ð»Ðµ Ñ„Ð¾ÐºÑƒÑ Ð½Ð° Ð½Ð°ÑÑ‚ÑƒÐ¿Ð½Ð¸Ð¹ Ñ€ÑÐ´Ð¾Ðº Ð¿ÐµÑ€ÐµÐ½Ð¾ÑÐ¸Ñ‚ÑŒ `focusNext()` â†’
 * `keyChangeSelection()`, ÑÐºÐ¸Ð¹ ÑÐ¿ÐµÑ€ÑˆÑƒ Ñ‡ÐµÐºÐ°Ñ” `timeout(RESIZE_INTERVAL + 30)`
 * (~70 Ð¼Ñ). Ð£ Ñ†ÑŒÐ¾Ð¼Ñƒ Ð²Ñ–ÐºÐ½Ñ– Ñ„Ð¾ÐºÑƒÑ Ñ‰Ðµ ÑÑ‚Ð¾Ñ—Ñ‚ÑŒ Ð½Ð° Ð¢Ð†Ð™ Ð¡ÐÐœÐ†Ð™ ÐºÐ¾Ð¼Ñ–Ñ€Ñ†Ñ–, Ð° Ñ€ÐµÐ´Ð°ÐºÑ‚Ð¾Ñ€ ÑƒÐ¶Ðµ
 * Ð·Ð°ÐºÑ€Ð¸Ñ‚Ð¸Ð¹ â€” Ñ‚Ð¾Ð¶ Ð½Ð°ÑÑ‚ÑƒÐ¿Ð½Ð¸Ð¹ Enter (Ñ€ÐµÐ¶Ð¸Ð¼ Â«Ð½Ðµ Ñ€ÐµÐ´Ð°Ð³ÑƒÐ²Ð°Ð½Ð½ÑÂ») Ð²Ñ–Ð´ÐºÑ€Ð¸Ð²Ð°Ñ” Ñ€ÐµÐ´Ð°ÐºÑ‚Ð¾Ñ€
 * Ð·Ð½Ð¾Ð²Ñƒ Ð½Ð° Ð½Ñ–Ð¹, Ñ– Ñ€ÐµÐ´Ð°ÐºÑ‚Ð¾Ñ€ Ð¿Ð¾ÐºÐ°Ð·ÑƒÑ” Ñ‰Ð¾Ð¹Ð½Ð¾ Ð·Ð±ÐµÑ€ÐµÐ¶ÐµÐ½Ðµ Ð·Ð½Ð°Ñ‡ÐµÐ½Ð½Ñ; Ð½Ð°ÑÑ‚ÑƒÐ¿Ð½Ð¸Ð¹ ÑÐ¸Ð¼Ð²Ð¾Ð»
 * Ð´Ð¾Ð¿Ð¸ÑÑƒÑ”Ñ‚ÑŒÑÑ Ð´Ð¾ Ð½ÑŒÐ¾Ð³Ð¾ (`1` + `2` = `12`). Ð”Ð°Ð»Ñ– Ð·Ð°Ð¿Ñ–Ð·Ð½Ñ–Ð»Ð¸Ð¹ Â«ÑÑ‚Ñ€Ñ–Ð»ÐºÐ° Ð²Ð½Ð¸Ð·Â»
 * Ð¿ÐµÑ€ÐµÐ½Ð¾ÑÐ¸Ñ‚ÑŒ Ñ„Ð¾ÐºÑƒÑ, Ð° Ñ€ÐµÑˆÑ‚Ð° Ð²Ð²Ð¾Ð´Ñƒ Ð¿Ð¾Ñ‚Ñ€Ð°Ð¿Ð»ÑÑ” Ð²Ð¶Ðµ Ð½Ðµ Ñ‚ÑƒÐ´Ð¸. ÐÐ½Ð°Ð»Ð¾Ð³Ñ–Ñ‡Ð½Ð¾ Ñ€ÐµÐ´Ð°ÐºÑ‚Ð¾Ñ€
 * Ð¿Ñ–ÑÐ»Ñ Ð²Ñ–Ð´ÐºÑ€Ð¸Ñ‚Ñ‚Ñ Ñ„Ð¾ÐºÑƒÑÑƒÑ” ÑÐ²Ñ–Ð¹ `<input>` Ð»Ð¸ÑˆÐµ Ñ‡ÐµÑ€ÐµÐ· `await timeout()`, Ñ‚Ð¾Ð¶
 * ÑÐ¸Ð¼Ð²Ð¾Ð»Ð¸, Ñ‰Ð¾ Ð¿Ñ€Ð¸Ð¹ÑˆÐ»Ð¸ Ð´Ð¾ Ñ†ÑŒÐ¾Ð³Ð¾, Ð³ÑƒÐ±Ð»ÑÑ‚ÑŒÑÑ.
 *
 * âš  Ð’Ð¸Ð¿Ñ€Ð°Ð²Ð»ÐµÐ½Ð½Ñ â€” Ð½Ðµ Ð´Ñ€ÑƒÐ³Ð° ÐºÐ¾Ð¿Ñ–Ñ Ð»Ð¾Ð³Ñ–ÐºÐ¸ ÐºÐ¾Ð¼Ñ–Ñ‚Ñƒ (Ð´Ð¸Ð². `keyboardCompat.ts`), Ð°
 * Ð§Ð•Ð Ð“Ð ÐºÐ»Ð°Ð²Ñ–Ñˆ Ñƒ capture-Ñ„Ð°Ð·Ñ– ÐºÐ¾Ð½Ñ‚ÐµÐ¹Ð½ÐµÑ€Ð°: Ñ‰Ð¾Ð¹Ð½Ð¾ Enter/Tab Ð·Ð°Ñ„Ñ–ÐºÑÑƒÐ²Ð°Ð² ÐºÐ¾Ð¼Ñ–Ñ€ÐºÑƒ
 * (Ð°Ð±Ð¾ Enter/ÑÐ¸Ð¼Ð²Ð¾Ð» Ð²Ñ–Ð´ÐºÑ€Ð¸Ð² Ñ€ÐµÐ´Ð°ÐºÑ‚Ð¾Ñ€), Ð¿Ð¾Ð´Ð°Ð»ÑŒÑˆÑ– ÐºÐ»Ð°Ð²Ñ–ÑˆÑ– Ð·Ð°Ñ‚Ñ€Ð¸Ð¼ÑƒÑŽÑ‚ÑŒÑÑ Ð¹
 * Ð²Ñ–Ð´Ñ‚Ð²Ð¾Ñ€ÑŽÑŽÑ‚ÑŒÑÑ Ð¿Ð¾ Ð¿Ð¾Ñ€ÑÐ´ÐºÑƒ, ÐºÐ¾Ð»Ð¸ RevoGrid Ð·Ð°Ð²ÐµÑ€ÑˆÐ¸Ð² Ð¿ÐµÑ€ÐµÑ…Ñ–Ð´ (Ð¿Ð¾Ð´Ñ–Ñ
 * `focuscell` / Ñ„Ð¾ÐºÑƒÑ Ð½Ð° `<input>` Ñ€ÐµÐ´Ð°ÐºÑ‚Ð¾Ñ€Ð°) Ð°Ð±Ð¾ Ð¼Ð¸Ð½Ðµ Ð·Ð°Ð¿Ð°ÑÐ½Ð¸Ð¹ Ñ‚ÐµÑ€Ð¼Ñ–Ð½. Ð¢Ð°Ðº
 * ÐºÐ¾Ð¶Ð½Ðµ Ð·Ð½Ð°Ñ‡ÐµÐ½Ð½Ñ Ð¿Ð¾Ñ‚Ñ€Ð°Ð¿Ð»ÑÑ” Ñƒ ÑÐ²Ð¾ÑŽ ÐºÐ¾Ð¼Ñ–Ñ€ÐºÑƒ, Ñƒ Ð¿Ð¾Ñ€ÑÐ´ÐºÑƒ Ð²Ð²ÐµÐ´ÐµÐ½Ð½Ñ; Ð·Ð²Ð¸Ñ‡Ð°Ð¹Ð½Ð¸Ð¹
 * Ñ‚ÐµÐ¼Ð¿ Ð»ÑŽÐ´Ð¸Ð½Ð¸ Ð½Ñ–Ñ‡Ð¾Ð³Ð¾ Ð½Ðµ Ð·Ð°Ñ‚Ñ€Ð¸Ð¼ÑƒÑ” â€” Ð²Ñ–ÐºÐ½Ð¾ Ð²Ñ–Ð´ÐºÑ€Ð¸Ð²Ð°Ñ”Ñ‚ÑŒÑÑ Ð»Ð¸ÑˆÐµ Ð¿Ñ–ÑÐ»Ñ Enter/Tab.
 */

import { isEnterKeyEvent } from './keyboardCompat';

/** Ð—Ð°Ð¿Ð°ÑÐ½Ð¸Ð¹ Ñ‚ÐµÑ€Ð¼Ñ–Ð½ Ð¾Ñ‡Ñ–ÐºÑƒÐ²Ð°Ð½Ð½Ñ ÐºÑ–Ð½Ñ†Ñ Ð¿ÐµÑ€ÐµÑ…Ð¾Ð´Ñƒ Ñ„Ð¾ÐºÑƒÑÐ° (Ð¾ÑÑ‚Ð°Ð½Ð½Ñ–Ð¹ Ñ€ÑÐ´Ð¾Ðº â€” `focuscell` Ð½Ðµ Ð±ÑƒÐ´Ðµ). */
export const CommitSettleMs = 250;
/** Ð—Ð°Ð¿Ð°ÑÐ½Ð¸Ð¹ Ñ‚ÐµÑ€Ð¼Ñ–Ð½ Ð¾Ñ‡Ñ–ÐºÑƒÐ²Ð°Ð½Ð½Ñ Ñ„Ð¾ÐºÑƒÑÐ° Ð½Ð° `<input>` Ñ‰Ð¾Ð¹Ð½Ð¾ Ð²Ñ–Ð´ÐºÑ€Ð¸Ñ‚Ð¾Ð³Ð¾ Ñ€ÐµÐ´Ð°ÐºÑ‚Ð¾Ñ€Ð°. */
export const OpenSettleMs = 200;

const EditWrapper = '.edit-input-wrapper';

function inEditor(target: EventTarget | null): boolean {
  return target instanceof Element && target.closest(EditWrapper) !== null;
}

function isPrintable(event: KeyboardEvent): boolean {
  return event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey;
}

interface QueuedKey {
  readonly init: KeyboardEventInit;
  readonly target: EventTarget | null;
}

/** Ð’ÑÑ‚Ð°Ð½Ð¾Ð²Ð»ÑŽÑ” Ñ‡ÐµÑ€Ð³Ñƒ ÐºÐ»Ð°Ð²Ñ–Ñˆ Ð½Ð° ÐºÐ¾Ð½Ñ‚ÐµÐ¹Ð½ÐµÑ€Ñ– ÑÑ–Ñ‚ÐºÐ¸; Ð¿Ð¾Ð²ÐµÑ€Ñ‚Ð°Ñ” Ñ„ÑƒÐ½ÐºÑ†Ñ–ÑŽ Ð²Ñ–Ð´Ð¿Ð¸ÑÐºÐ¸. */
export function installKeyCommitGate(container: HTMLElement): () => void {
  const doc = container.ownerDocument;
  let holding: 'commit' | 'open' | null = null;
  let timer: ReturnType<typeof setTimeout> | null = null;
  let queue: QueuedKey[] = [];

  const overlay = (): Element | null => container.querySelector('revogr-overlay-selection');

  const resolveTarget = (queued: QueuedKey): EventTarget => {
    const active = document.activeElement;
    if (active instanceof HTMLInputElement && inEditor(active) && container.contains(active)) return active;

    const original = queued.target;
    if (original instanceof Node && original.isConnected && container.contains(original)) return original;

    return overlay() ?? container;
  };

  const replay = (queued: QueuedKey): void => {
    const target = resolveTarget(queued);
    const event = new KeyboardEvent('keydown', { ...queued.init, bubbles: true, cancelable: true });
    target.dispatchEvent(event);

    // âš  Ð¡Ð¸Ð½Ñ‚ÐµÑ‚Ð¸Ñ‡Ð½Ð¸Ð¹ keydown ÑÐ¸Ð¼Ð²Ð¾Ð» Ñƒ `<input>` Ð½Ðµ Ð²Ð²Ð¾Ð´Ð¸Ñ‚ÑŒ â€” Ð´Ð¾Ð¿Ð¸ÑÑƒÑ”Ð¼Ð¾ ÑÐ°Ð¼Ñ–,
    // Ð»Ð¸ÑˆÐµ ÑÐºÑ‰Ð¾ Ð½Ñ–Ñ…Ñ‚Ð¾ Ð½Ðµ ÑÐºÐ°ÑÑƒÐ²Ð°Ð² Ð¿Ð¾Ð´Ñ–ÑŽ Ð¹ Ñ€ÐµÐ´Ð°ÐºÑ‚Ð¾Ñ€ ÑƒÐ¶Ðµ Ð¼Ð°Ñ” Ñ„Ð¾ÐºÑƒÑ.
    if (
      !event.defaultPrevented &&
      target instanceof HTMLInputElement &&
      inEditor(target) &&
      isPrintable(event)
    ) {
      target.setRangeText(event.key, target.selectionStart ?? target.value.length, target.selectionEnd ?? target.value.length, 'end');
      target.dispatchEvent(new Event('input', { bubbles: true }));
    }
  };

  const release = (): void => {
    if (timer !== null) clearTimeout(timer);
    timer = null;
    holding = null;

    // Ð’Ñ–Ð´Ñ‚Ð²Ð¾Ñ€ÐµÐ½Ð½Ñ Ð¼Ð¾Ð¶Ðµ Ð·Ð½Ð¾Ð²Ñƒ Ð²Ñ–Ð´ÐºÑ€Ð¸Ñ‚Ð¸ Ð²Ñ–ÐºÐ½Ð¾ (Enter Ñƒ Ñ€ÐµÐ´Ð°ÐºÑ‚Ð¾Ñ€Ñ–) â€” Ñ‚Ð¾Ð´Ñ– Ñ€ÐµÑˆÑ‚Ð° Ð»Ð¸ÑˆÐ°Ñ”Ñ‚ÑŒÑÑ Ð² Ñ‡ÐµÑ€Ð·Ñ–.
    while (queue.length > 0 && holding === null) {
      const next = queue.shift();
      if (next !== undefined) replay(next);
    }
  };

  const hold = (kind: 'commit' | 'open'): void => {
    holding = kind;
    if (timer !== null) clearTimeout(timer);
    timer = setTimeout(release, kind === 'commit' ? CommitSettleMs : OpenSettleMs);
  };

  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.isComposing) return;

    // âš  Ð¡Ð»ÑƒÑ…Ð°Ñ‡ ÑÑ‚Ð¾Ñ—Ñ‚ÑŒ Ð½Ð° `document`: Ð½Ð° Ñ‡Ð°Ñ Ð¿ÐµÑ€ÐµÑ…Ð¾Ð´Ñƒ Ñ€ÐµÐ´Ð°ÐºÑ‚Ð¾Ñ€ Ð²Ð¶Ðµ Ð²Ñ‚Ñ€Ð°Ñ‚Ð¸Ð² Ñ„Ð¾ÐºÑƒÑ
    // (`blur` Ð¿Ñ€Ð¸ Ñ„Ñ–ÐºÑÐ°Ñ†Ñ–Ñ—), Ð° Ð½Ð¾Ð²Ð¾Ð³Ð¾ Ñ‰Ðµ Ð½ÐµÐ¼Ð°Ñ” â€” ÐºÐ»Ð°Ð²Ñ–ÑˆÐ° Ð¹Ð´Ðµ Ð² `<body>` Ñ– Ð´Ð¾
    // ÐºÐ¾Ð½Ñ‚ÐµÐ¹Ð½ÐµÑ€Ð° Ð½Ðµ Ð´Ñ–Ð¹ÑˆÐ»Ð° Ð± ÑƒÐ·Ð°Ð³Ð°Ð»Ñ–, Ñ‚Ð¾Ð±Ñ‚Ð¾ Ð³ÑƒÐ±Ð¸Ð»Ð°ÑÑŒ Ð±Ð¸ Ð¼Ð¾Ð²Ñ‡ÐºÐ¸.
    const target = event.target;
    const key = isEnterKeyEvent(event) ? 'Enter' : event.key;
    const inside = target instanceof Node && container.contains(target);
    const detached = target === doc.body || target === doc.documentElement;
    if (!inside && !(holding !== null && detached)) return;

    if (holding !== null) {
      event.preventDefault();
      event.stopImmediatePropagation();
      queue.push({
        init: {
          key,
          code: event.code,
          shiftKey: event.shiftKey,
          ctrlKey: event.ctrlKey,
          altKey: event.altKey,
          metaKey: event.metaKey,
        },
        target: event.target,
      });

      return;
    }

    if (inEditor(event.target)) {
      if (key === 'Enter' || key === 'Tab') hold('commit');

      return;
    }

    // Ð ÐµÐ´Ð°ÐºÑ‚Ð¾Ñ€ Ñ‰Ðµ Ð½Ðµ Ð²Ñ–Ð´ÐºÑ€Ð¸Ñ‚Ð¸Ð¹: Enter Ð°Ð±Ð¾ ÑÐ¸Ð¼Ð²Ð¾Ð» Ð²Ñ–Ð´ÐºÑ€Ð¸Ñ” Ð¹Ð¾Ð³Ð¾, Ð° Ñ„Ð¾ÐºÑƒÑ Ð½Ð°
    // `<input>` Ð²Ñ–Ð½ Ð¾Ñ‚Ñ€Ð¸Ð¼Ð°Ñ” Ð½Ðµ Ð¾Ð´Ñ€Ð°Ð·Ñƒ.
    if (container.querySelector(EditWrapper) === null && (key === 'Enter' || isPrintable(event))) {
      hold('open');
    }
  };

  const onFocusCell = (): void => {
    if (holding === 'commit') release();
  };

  const onFocusIn = (event: FocusEvent): void => {
    if (holding === 'open' && inEditor(event.target)) release();
  };

  doc.addEventListener('keydown', onKeyDown, true);
  container.addEventListener('focuscell', onFocusCell);
  container.addEventListener('focusin', onFocusIn);

  return () => {
    doc.removeEventListener('keydown', onKeyDown, true);
    container.removeEventListener('focuscell', onFocusCell);
    container.removeEventListener('focusin', onFocusIn);
    if (timer !== null) clearTimeout(timer);
    queue = [];
    holding = null;
  };
}

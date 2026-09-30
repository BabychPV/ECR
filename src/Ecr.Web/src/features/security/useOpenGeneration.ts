import { useState } from 'react';

/**
 * Номер «відкриття» діалогу: росте щоразу, коли `opened` стає `true`.
 *
 * ⛔ Потрібен як `key` форми. Закритий `Modal` розмонтовує вміст лише ПІСЛЯ
 * анімації закриття, і повторне відкриття в цьому вікні (Cancel → одразу
 * «New role») підхопило б ту саму, ще живу форму зі старою чернеткою
 * (аудит-пас 5, `SecurityPage.modalStaleData.test.tsx`).
 */
export function useOpenGeneration(opened: boolean): number {
  const [previous, setPrevious] = useState(opened);
  const [generation, setGeneration] = useState(0);

  if (opened !== previous) {
    setPrevious(opened);
    if (opened) setGeneration((current) => current + 1);
  }

  return generation;
}

import { createElement, type ComponentType, type ReactNode } from 'react';
import { Button, Group, Text, type ButtonProps, type GroupProps, type TextProps } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { logSuppressedDetail, problemText } from './problemText';

/*
 * ⚠ Псевдоніми, а не прямі виклики `createElement(Button, …)`.
 *
 * Компоненти Mantine ПОЛІМОРФНІ: їхній тип — перетин узагальнених сигнатур
 * (`component=` міняє набір допустимих пропів), і під
 * `exactOptionalPropertyTypes` він не звужується до `FunctionComponent<P>`,
 * якого чекає `createElement` (`TS2769`). У JSX цього не видно — там працює
 * інший шлях виводу типів, і саме тому решта файлів проблеми не має.
 *
 * ⚠ Звуження НЕ послаблює перевірку: перелічені тут пропи перевіряються далі
 * як завжди — зайвий або помилковий проп так само не скомпілюється. Втрачено
 * рівно одне — можливість передати сюди `component=`, яка тут і не потрібна.
 */
const UndoRow = Group as ComponentType<GroupProps & { children?: ReactNode }>;
const UndoText = Text as ComponentType<TextProps & { children?: ReactNode }>;
const UndoButton = Button as ComponentType<
  ButtonProps & { onClick: () => void; children?: ReactNode }
>;

/**
 * Доступне ім'я хрестика на сповіщенні (UI-прохід, F8).
 *
 * ⛔ Mantine 7 малює цю кнопку через `<CloseButton>` БЕЗ тексту й без
 * `aria-label` (`Notification.mjs`: `withCloseButton && <CloseButton iconSize
 * … {...closeButtonProps} />`), тобто читалка оголошує її просто «кнопка».
 * На відміну від стрілок лічильника `NumberInput` (яким Mantine сам ставить
 * `aria-hidden`, тобто з дерева доступності їх прибрано), хрестик тоста
 * ДОСЯЖНИЙ — і це єдина дія, якою сповіщення можна прибрати з екрана.
 *
 * ⚠ Напис — ЛІТЕРАЛ, не `t()`, з тієї ж причини, що `passwordToggleProps` у
 * `pages/LoginPage.tsx`: рядки цього застосунку йдуть із серверного каталогу
 * (`09-seed.sql`), ключа під цей напис там ще немає, а голий `t()` без рядка
 * показав би читалці позначений ключ (`⟦…⟧`) замість опису кнопки.
 *
 * ⚠ Експортується навмисно: `notifications.show(...)` кличуть і повз цей
 * модуль (`features/workflow/SheetActions.tsx`, `pages/DocumentPage.tsx`,
 * `features/export/ExportButton.tsx`, `features/security/UserAccessEditor.tsx`,
 * `features/mapping/CreateMappingModal.tsx`, `pages/admin/SourcesPage.tsx`,
 * `pages/admin/PeriodsPage.tsx`, `pages/admin/SnapshotsPage.tsx`,
 * `pages/admin/GrantsPanel.tsx`), і другий літерал у кожному з них розійшовся
 * б із цим непомітно.
 */
export const notificationCloseButtonProps = { 'aria-label': 'Close notification' } as const;

/**
 * Показує причину відмови так, як її назвав сервер.
 *
 * ⛔ Саме `error.message`, а не «не вдалося». Відмови цієї системи змістовні:
 * «період закрито — спершу відкрийте період» (`ECR-PRD-4223`), «затвердження
 * відхилено: маршрут не містить вашої ролі» (`ECR-ACCS-0403`), «коментар при
 * відхиленні обов'язковий» (`ECR-DOC-0422`). Замінити їх на «щось пішло не
 * так» означає викинути єдину підказку, яка веде до дії (`ФВ-14.24`).
 *
 * ⚠ Функція існує тому, що цей самий блок був скопійований у кожному екрані.
 * Скопійований — означає, що в одному з них рано чи пізно лишиться `String(error)`
 * без розбору, і саме там відмова стане німою.
 */
export function showApiError(error: unknown): void {
  /*
   * ✎ 2026-09-20, рішення людини: «українську прибрати — має бути залежно
   * від обраної мови». Тут стояв `error.message` (`detail ?? title`) без
   * розбору мови, а серверні речення писалися українською — якої в продукті
   * немає (`D-95`: en, ru, kz).
   *
   * ⚠ Тост, на відміну від `ErrorAlert`, НЕ має окремого заголовка: сховати
   * неперекладену подробицю тут означає лишити саму назву проблеми. Тому,
   * коли подробиці показати не можна, до назви додається КОД — з ним
   * звернення в підтримку лишається однозначним, а без нього тост
   * перетворився б на голе «Conflict» без жодної зачіпки.
   */
  const shown = problemText(error);

  logSuppressedDetail(shown);

  notifications.show({
    color: 'statusError',
    message: shown.detail ?? (shown.code === null ? shown.title : `${shown.title} · ${shown.code}`),
    closeButtonProps: notificationCloseButtonProps,
  });
}

/** Показує підтвердження успішної дії. */
export function showDone(message: string): void {
  notifications.show({
    color: 'statusSuccess',
    message,
    closeButtonProps: notificationCloseButtonProps,
  });
}

/**
 * Скільки живе вікно «назад» (директива №15, §2, Шар 2: `ms = 8000`).
 *
 * ⚠ Вісім секунд — не «щоб довше повисіло»: це час, за який людина встигає
 * ПРОЧИТАТИ, що саме сталося, і лише тоді вирішити. Тост на 3–4 секунди
 * з кнопкою «назад» — це кнопка, якої встигають торкнутися випадково або не
 * встигають узагалі.
 */
const UndoWindowMs = 8000;

/** Номер тоста: id потрібен, щоб закрити СВІЙ тост, а не чийсь сусідній. */
let undoSequence = 0;

/**
 * Тост із дією «назад» (`L7`: після дії — що сталося і що далі).
 *
 * ⛔ **За замовчуванням — компенсація, а не відкладання.** `onUndo`
 * викликається РІВНО тоді, коли користувач натиснув «назад»; сама дія на цей
 * момент уже виконана викликачем. Так обрано тому, що відкладений варіант має
 * ціну, яку платить не той, хто його обрав: вкладку закривають, ноутбук
 * складають, мережа падає — і дія, про яку людині вже написали в минулому
 * часі («Документ подано»), не відбувається НІКОЛИ, а на екрані не лишається
 * жодного сліду. Повідомити про стан, якого потім не буде, гірше, ніж зробити
 * зайвий запит і скасувати його.
 *
 * ⚠ Але відкладений варіант **доступний**, і саме цього вимагає директива
 * («виконується ПІСЛЯ спливу таймера або одразу з компенсацією — вирішується
 * на екрані»): функція повертає `Promise<boolean>` — `true`, якщо натиснули
 * «назад», `false`, якщо вікно збігло або тост закрили хрестиком. Екран, де
 * сервер НЕ вміє «назад», чекає на `false` і лише тоді робить запит:
 *
 *     if (!(await showUndo(msg, () => {}))) await api.delete(id);   // відкладено
 *     await api.delete(id); void showUndo(msg, () => api.restore(id)); // компенсація
 *
 * ⛔ Таймер тут ВЛАСНИЙ, а не `autoClose` Mantine, хоча `autoClose` теж
 * заданий тим самим числом. `autoClose` вирішує, коли тост зникне з екрана;
 * `Promise` — коли вікно рішення ЗАКРИТЕ, а це обіцянка перед викликачем, і
 * вона не має залежати від того, чи змонтований `<Notifications />` і чи
 * доїхала анімація виходу.
 *
 * ⚠ Обіцянка виконується рівно один раз (`settled`): натиснута кнопка спершу
 * розв'язує `Promise`, і лише потім ховає тост — інакше `onClose` від
 * власного ж `hide` прочитався б як «вікно збігло».
 *
 * ⚠ `undoLabel` — літерал із тієї самої причини, що й
 * `notificationCloseButtonProps` вище: ключа під цей напис у каталозі
 * (`09-seed.sql`) ще немає, а `t()` на неіснуючий ключ показав би `⟦…⟧`.
 * Викликач, у якого ключ уже є, передає підпис сам.
 */
export function showUndo(
  message: string,
  onUndo: () => void,
  ms: number = UndoWindowMs,
  undoLabel = 'Undo',
): Promise<boolean> {
  undoSequence += 1;

  const id = `ecr-undo-${String(undoSequence)}`;

  return new Promise<boolean>((resolve) => {
    let settled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;

    function settle(undone: boolean): void {
      if (settled) return;

      settled = true;

      if (timer !== undefined) clearTimeout(timer);
      if (undone) onUndo();

      resolve(undone);
    }

    timer = setTimeout(() => {
      settle(false);
      notifications.hide(id);
    }, ms);

    notifications.show({
      id,
      autoClose: ms,
      closeButtonProps: notificationCloseButtonProps,

      // Хрестик — теж відповідь «ні»: вікно закрите, дію не скасовано.
      onClose: () => {
        settle(false);
      },

      /*
       * ⚠ `createElement`, а не JSX: файл лишається `.ts`. Перейменування на
       * `.tsx` заради двох вузлів зачепило б імпорти в тринадцяти місцях і
       * прийшло б окремим PR-перейменуванням (CLAUDE.md §4) — ціна вища за
       * незручність двох викликів.
       *
       * ⚠ Саме `Button`, а не текст із `onClick`: «назад» має бути досяжним
       * табом і спрацьовувати пробілом (`ФВ-14.19`), а читалка має почути
       * роль, що обіцяє дію.
       */
      message: createElement(
        UndoRow,
        { gap: 'xs', wrap: 'nowrap', justify: 'space-between' },
        createElement(UndoText, { size: 'sm' }, message),
        createElement(
          UndoButton,
          {
            size: 'xs',
            variant: 'default',
            onClick: () => {
              settle(true);
              notifications.hide(id);
            },
          },
          undoLabel,
        ),
      ),
    });
  });
}

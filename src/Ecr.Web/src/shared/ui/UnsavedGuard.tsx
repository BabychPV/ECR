import { useCallback, useEffect, useRef, useState, type JSX } from 'react';
import { Button, Group, Modal, Text } from '@mantine/core';
import { useBlocker, type BlockerFunction } from 'react-router-dom';
import { AutosaveSettleMs, flushAutosaveAndSettle } from '@/features/grid/autosave';
import { hasPending, pendingCount } from '@/features/grid/pendingStore';
import { t } from '@/shared/i18n';

/**
 * Вихід із документа з незбереженими правками (`D14-12`, крок 3; `D15` `UI-00`).
 *
 * Директива задає поведінку дослівно: «вихід із документа — `useBlocker`
 * (react-router 7): спершу `flush()`, діалог "є незбережені зміни" ЛИШЕ якщо
 * збереження не вдалося».
 *
 * ⛔ Саме «лише якщо не вдалося», а не «завжди питати». Питання на КОЖНОМУ
 * переході — це не захист: користувач, який бачить його двадцять разів на день
 * і двадцять разів відповідає «так», на двадцять перший підтвердить втрату
 * даних не читаючи. Автозбереження існує рівно для того, щоб потреби питати не
 * було; діалог лишається для єдиного випадку, коли автозбереження не впоралося
 * і мовчазний перехід справді коштував би даних.
 *
 * ⛔ Блокує лише зміну ШЛЯХУ (`pathname`), не адреси цілком. Аркуш документа
 * живе в пошуковому рядку (`DocumentPage.tsx`: `useUrlState('sheet')`), тобто
 * перемикання аркуша — це навігація з погляду роутера. Блокувати її означало б
 * питати про збереження при кожному перемиканні аркуша — рівно та поведінка,
 * яку директива відхиляє окремим абзацом («карає користувача за швидкість»), і
 * рівно той дефект (`W-02`), заради якого правки взагалі переїхали в сховище
 * рівня документа.
 *
 * ⚠ Компонент нічого не знає ні про сітку, ні про документ: рішення «є що
 * зберігати» ухвалює сховище (`hasPending()`), а «зберегти і дочекатися» —
 * `flushAutosaveAndSettle()`. Тому один екземпляр на весь застосунок (в
 * `AppLayout`) покриває будь-який маршрут, де щось редагується, і не змушує
 * кожну сторінку вигадувати власний вихід.
 */

interface UnsavedGuardProps {
  /**
   * Скільки чекати на результат збереження, перш ніж вважати його невдалим.
   *
   * ⚠ Проп існує заради тестів: три секунди реального очікування в наборі —
   * це три секунди на кожну перевірку відмови. Продукт передає дефолт.
   */
  readonly settleTimeoutMs?: number;
}

export function UnsavedGuard({
  settleTimeoutMs = AutosaveSettleMs,
}: UnsavedGuardProps = {}): JSX.Element | null {
  /*
   * ⚠ `useCallback` із порожніми залежностями, а не вбудована стрілка:
   * `useBlocker` перереєстровує блокувальник у роутері на КОЖНУ зміну
   * ідентичності функції (див. його `useEffect` за `blockerFunction`). Свіжість
   * даних від цього не страждає — `hasPending()` читає модульне сховище в
   * момент виклику, а не в момент оголошення.
   */
  const shouldBlock = useCallback<BlockerFunction>(
    ({ currentLocation, nextLocation }) =>
      hasPending() && currentLocation.pathname !== nextLocation.pathname,
    [],
  );

  const blocker = useBlocker(shouldBlock);
  const { state } = blocker;

  /** Збереження при виході ПРОВАЛИЛОСЬ — єдина підстава показати діалог. */
  const [saveFailed, setSaveFailed] = useState(false);

  /*
   * ⚠ Блокувальник читається через `ref`, бо рішення ухвалюється ПІСЛЯ `await`:
   * до цього моменту об'єкт `blocker` із замикання вже застарів би (роутер
   * віддає новий на кожну зміну свого стану), а `proceed()` застарілого — це
   * перехід, який нікуди не веде.
   */
  const blockerRef = useRef(blocker);
  useEffect(() => {
    blockerRef.current = blocker;
  });

  /*
   * ⚠ Стан очікування. Доки збереження йде, роутер тримає навігацію
   * заблокованою — і якщо користувач тисне ще раз (інший пункт меню, `Назад`),
   * роутер просто переставляє блокування на НОВУ ціль, не знімаючи попереднього.
   * Другого `flush()` при цьому не запускається: одного досить, він і так
   * зберігає ВСЕ сховище, а два паралельні дали б два `PATCH` тих самих комірок.
   * Коли збереження відстоїться, `proceed()` поведе туди, куди користувач
   * натиснув ОСТАННІМ, — саме цього він і чекає.
   */
  const flushing = useRef(false);

  useEffect(() => {
    if (state !== 'blocked') {
      setSaveFailed(false);

      return;
    }

    if (flushing.current) return;
    flushing.current = true;

    void flushAutosaveAndSettle(settleTimeoutMs).then((saved) => {
      flushing.current = false;

      // Користувач міг зняти блокування сам (`reset()`), доки запит летів.
      if (blockerRef.current.state !== 'blocked') return;

      // ⛔ Ось воно, головне: збереглося — виходимо МОВЧКИ, без жодного питання.
      if (saved) blockerRef.current.proceed?.();
      else setSaveFailed(true);
    });
  }, [state, settleTimeoutMs]);

  if (!saveFailed) return null;

  const stay = (): void => {
    setSaveFailed(false);
    blockerRef.current.reset?.();
  };

  const leave = (): void => {
    setSaveFailed(false);
    blockerRef.current.proceed?.();
  };

  return (
    <Modal opened onClose={stay} title={t('unsaved.title')}>
      {/*
       * ⛔ `pendingCount()`, а не хук `usePendingCount()`. Хук підписав би на
       * сховище САМ КАРКАС застосунку — а сховище змінюється на кожне
       * натискання клавіші в сітці, тобто весь `AppLayout` перемальовувався б
       * при введенні числа в комірку. Тут потрібен знімок, і саме знімок:
       * доки діалог на екрані, ніхто нічого не редагує.
       */}
      <Text size="sm">{t('unsaved.body', { count: pendingCount() })}</Text>

      <Group justify="flex-end" mt="md">
        {/*
         * ⚠ Фокус за замовчуванням — на БЕЗПЕЧНІЙ дії (`data-autofocus`).
         * Діалог з'являється несподівано, посеред переходу; `Enter`, натиснутий
         * за звичкою, має залишити користувача з його даними, а не викинути
         * його з ними.
         */}
        <Button variant="default" data-autofocus onClick={stay} data-testid="unsaved-stay">
          {t('unsaved.stay')}
        </Button>

        <Button color="statusError" onClick={leave} data-testid="unsaved-leave">
          {t('unsaved.leave')}
        </Button>
      </Group>
    </Modal>
  );
}

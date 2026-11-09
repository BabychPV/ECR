import { useEffect, useId, useRef, useState, type JSX } from 'react';
import { Button, Center, Group, Overlay, Paper, Portal, Stack, Text, Title, VisuallyHidden } from '@mantine/core';
import {
  ACTIVITY_PATH,
  CORRELATION_HEADER,
  IDLE_LOGOUT_PATH,
  apiFetch,
  newCorrelationId,
  sessionUserHeaders,
  setUserActivityListener,
  signOutForIdle,
} from '@/api/client';
import { t } from '@/shared/i18n';
import { IdleClock, type IdlePhase } from './idleClock';

/** Події, що означають дію людини (D-471). `mousemove` — ні: зачеплена миша не є присутністю. */
const ActivityEvents = ['pointerdown', 'keydown', 'wheel', 'touchstart'] as const;

/** `m:ss` для зворотного відліку. */
function formatRemaining(ms: number): string {
  const total = Math.max(0, Math.ceil(ms / 1000));
  const minutes = Math.floor(total / 60);
  const seconds = total % 60;
  return `${String(minutes)}:${String(seconds).padStart(2, '0')}`;
}

/** Повідомляє сервер, що вкладка виходить через бездіяльність; відповідь не важить (сервер і сам відкине). */
async function postIdleLogout(): Promise<void> {
  try {
    await fetch(IDLE_LOGOUT_PATH, {
      method: 'POST',
      credentials: 'include',
      headers: { [CORRELATION_HEADER]: newCorrelationId(), ...sessionUserHeaders() },
    });
  } catch {
    // Мережа: cookie однаково мертва за межею на сервері (`IdleSession`).
  }
}

/**
 * Вихід після бездіяльності (D-471): спільний для вкладок годинник, попередження за хвилину і сам вихід.
 *
 * ⚠ Одна копія на застосунок — у `AppLayout`, який змонтований для будь-якого автентифікованого маршруту.
 *
 * ⛔ Сервер тримає ту саму межу незалежно від цього компонента (`IdleSession`): вкладка, у якої заснув таймер,
 * однаково отримає `401` на наступному опитуванні.
 */
export function IdleSignOut({ minutes }: { minutes: number }): JSX.Element | null {
  const [phase, setPhase] = useState<IdlePhase>('active');
  const [deadline, setDeadline] = useState(0);
  const [now, setNow] = useState(() => Date.now());
  const clockRef = useRef<IdleClock | null>(null);
  const minutesRef = useRef(minutes);

  useEffect(() => {
    const clock = new IdleClock(minutesRef.current * 60_000, {
      now: () => Date.now(),
      touch: () => apiFetch<void>(ACTIVITY_PATH, { method: 'POST' }),
      onPhase: (next, at) => {
        setPhase(next);
        setDeadline(at);
        setNow(Date.now());
      },
      onExpire: (remote) => {
        const limit = minutesRef.current;
        if (remote) {
          signOutForIdle(limit);
          return;
        }
        void postIdleLogout().finally(() => signOutForIdle(limit));
      },
    });
    clockRef.current = clock;

    const onActivity = (): void => clock.recordActivity();
    for (const name of ActivityEvents) {
      window.addEventListener(name, onActivity, { capture: true, passive: true });
    }
    setUserActivityListener(onActivity);
    clock.start();

    return () => {
      setUserActivityListener(null);
      for (const name of ActivityEvents) {
        window.removeEventListener(name, onActivity, { capture: true });
      }
      clock.stop();
      clockRef.current = null;
    };
  }, []);

  // Адміністратор змінив межу — діє від останньої дії, без перезавантаження.
  useEffect(() => {
    minutesRef.current = minutes;
    clockRef.current?.setTimeoutMs(minutes * 60_000);
  }, [minutes]);

  // Зворотний відлік — лише поки показано попередження.
  useEffect(() => {
    if (phase !== 'warning') return undefined;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [phase]);

  if (phase !== 'warning') return null;

  return (
    <IdleWarningDialog
      remainingMs={deadline - now}
      onStay={() => clockRef.current?.stayActive()}
    />
  );
}

/**
 * Попередження за хвилину до виходу.
 *
 * ⚠ Власна розмітка, а не `Modal`: у `Modal` Mantine роль `dialog` зашита, а це — `alertdialog` (WAI-ARIA):
 * термінове повідомлення, що вимагає відповіді. Фокус переходить на «Stay signed in» і не виходить за межі
 * діалогу (Tab лишається на кнопці); Escape = «Stay signed in» (людина явно тут).
 *
 * ⚠ Відлік видно щосекунди (`role="timer"`, читалка його не оголошує), а живий регіон оголошує його
 * щопівхвилини — інакше читалка перебивала б людину шістдесят разів поспіль.
 */
export function IdleWarningDialog({
  remainingMs,
  onStay,
}: {
  remainingMs: number;
  onStay: () => void;
}): JSX.Element {
  const titleId = useId();
  const textId = useId();
  const stayRef = useRef<HTMLButtonElement>(null);
  const dialogRef = useRef<HTMLDivElement>(null);
  const remaining = formatRemaining(remainingMs);
  const seconds = Math.max(0, Math.ceil(remainingMs / 1000));
  const announced = formatRemaining(Math.ceil(seconds / 30) * 30 * 1000);

  // ⚠ Через такт: `Portal` монтує вміст у власному ефекті, і в ефекті діалогу кнопки ще немає в документі.
  useEffect(() => {
    const timer = window.setTimeout(() => stayRef.current?.focus(), 0);
    return () => window.clearTimeout(timer);
  }, []);

  return (
    <Portal>
      <Overlay fixed zIndex={1000} backgroundOpacity={0.55} />
      <Center
        ref={dialogRef}
        role="alertdialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={textId}
        data-testid="idle-warning"
        pos="fixed"
        top={0}
        left={0}
        right={0}
        bottom={0}
        p="md"
        style={{ zIndex: 1001 }}
        onKeyDown={(event) => {
          if (event.key === 'Escape') {
            event.preventDefault();
            onStay();
            return;
          }
          if (event.key !== 'Tab') return;
          const buttons = dialogRef.current?.querySelectorAll<HTMLButtonElement>('button') ?? [];
          if (buttons.length === 0) return;
          const first = buttons[0];
          const last = buttons[buttons.length - 1];
          if (event.shiftKey && document.activeElement === first) {
            event.preventDefault();
            last?.focus();
          } else if (!event.shiftKey && document.activeElement === last) {
            event.preventDefault();
            first?.focus();
          }
        }}
      >
        <Paper withBorder shadow="lg" p="lg" maw={420} w="100%">
          <Stack gap="sm">
            <Title order={2} fz="lg" id={titleId}>
              {t('idle.warning.title')}
            </Title>
            <Text id={textId}>{t('idle.warning.text')}</Text>
            <Text fz={28} fw={600} ta="center" role="timer" aria-label={t('idle.warning.remaining')}>
              {remaining}
            </Text>
            <VisuallyHidden aria-live="polite" aria-atomic="true">
              {t('idle.warning.announce', { time: announced })}
            </VisuallyHidden>
            <Group justify="flex-end" gap="sm">
              <Button ref={stayRef} onClick={onStay}>
                {t('idle.warning.stay')}
              </Button>
            </Group>
          </Stack>
        </Paper>
      </Center>
    </Portal>
  );
}

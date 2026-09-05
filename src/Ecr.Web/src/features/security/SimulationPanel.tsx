import { useState, type JSX } from 'react';
import { Button } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { SimulationSessionResponse, StartSimulationRequest } from '@/api/types';
import { MeQueryKey } from '@/shared/session/useSession';
import { ReasonModal } from '@/shared/ui/ReasonModal';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/**
 * Де зберігається ідентифікатор сеансу симуляції.
 *
 * ⛔ У `sessionStorage`, а не в стані компонента: завершити сеанс треба з
 * шапки застосунку, а починають його з екрана безпеки — це різні дерева
 * компонентів. І не в `localStorage`: сеанс належить цій вкладці й цьому
 * входу, а не машині.
 *
 * ⚠ Сам факт симуляції клієнт не вигадує — його віддає `/me`
 * (`isSimulation`). Тут лише **номер**, потрібний, щоб сеанс завершити:
 * `DELETE /security/simulation?sessionId=…`.
 */
const SessionKey = 'ecr.simulation.sessionId';

/** Читає збережений номер сеансу; `null` — сховище недоступне або порожнє. */
export function storedSessionId(): string | null {
  try {
    return globalThis.sessionStorage?.getItem(SessionKey) ?? null;
  } catch {
    // Приватний режим і політики браузера роблять сховище недоступним. Це не
    // причина ламати екран: без номера кнопка завершення просто не з'явиться.
    return null;
  }
}

/** Запам'ятовує або забуває номер сеансу. */
function rememberSession(id: string | null): void {
  try {
    if (id === null) globalThis.sessionStorage?.removeItem(SessionKey);
    else globalThis.sessionStorage?.setItem(SessionKey, id);
  } catch {
    // Те саме: без сховища симуляція працює, лише завершити її доведеться
    // виходом із системи.
  }
}

/**
 * Початок сеансу «подивитися чужими правами» (`ФВ-6.16`).
 *
 * ⛔ До аудиту цієї дії в інтерфейсі не було зовсім, хоча бадж «дивлюся
 * чужими правами» у шапці вже малювався. Тобто система вміла показати стан,
 * у який не могла увійти (`A7-39`).
 *
 * ⛔ Сеанс — **лише читання**: будь-який запис під симуляцією відхиляється
 * `EditDenyReason.SimulationReadOnly`, навіть із правом `Manage` (`ФВ-6.16a`).
 * Це не обмеження інтерфейсу, а правило сервера.
 *
 * ⚠ Причина обов'язкова і пишеться в аудит ОДРАЗУ, до видачі профілю: без
 * запису «подивитися очима» стало б способом безслідно переглянути чужі дані.
 */
export function StartSimulationButton({ userId }: { userId: number }): JSX.Element {
  const queryClient = useQueryClient();
  const [asking, setAsking] = useState(false);

  const start = useMutation({
    mutationFn: (reason: string) =>
      apiFetch<SimulationSessionResponse>('/api/v1/security/simulation', {
        method: 'POST',
        body: JSON.stringify({ subjectUserId: userId, reason } satisfies StartSimulationRequest),
      }),
    onSuccess: async (session) => {
      rememberSession(String(session.sessionId));

      // ⚠ Профіль перечитується негайно: саме він вмикає бадж у шапці і
      // звужує права. Без цього адміністратор далі бачив би свої кнопки.
      await queryClient.invalidateQueries({ queryKey: MeQueryKey });

      setAsking(false);
      showDone(t('security.simulationStarted'));
    },
    onError: showApiError,
  });

  return (
    <>
      <Button size="compact-xs" variant="subtle" onClick={() => setAsking(true)}>
        {t('security.simulate')}
      </Button>

      <ReasonModal
        opened={asking}
        title={t('security.simulate')}
        label={t('workflow.reason')}
        description={t('security.simulateHint')}
        confirmLabel={t('security.simulate')}
        isPending={start.isPending}
        onConfirm={(reason) => start.mutate(reason)}
        onClose={() => setAsking(false)}
      />
    </>
  );
}

/**
 * Завершення власного сеансу симуляції.
 *
 * ⚠ Кнопка живе поруч із баджем у шапці: саме там користувач помічає, що
 * дивиться чужими правами, і саме там має бути вихід. Чужий сеанс завершити
 * не можна — сервер відповість 403, бо обрив чужого сеансу псує чужий аудит.
 */
export function EndSimulationButton(): JSX.Element | null {
  const queryClient = useQueryClient();
  const sessionId = storedSessionId();

  const end = useMutation({
    mutationFn: (id: string) =>
      apiFetch(`/api/v1/security/simulation?sessionId=${encodeURIComponent(id)}`, {
        method: 'DELETE',
      }),
    onSuccess: async () => {
      rememberSession(null);
      await queryClient.invalidateQueries({ queryKey: MeQueryKey });
      showDone(t('security.simulationEnded'));
    },
    onError: showApiError,
  });

  // ⛔ Без номера сеансу кнопки немає: вона гарантовано дала б відмову.
  // Це справді буває — сеанс почали в іншій вкладці. Тоді вихід із системи
  // лишається чесним способом його завершити, і мовчазна непрацездатна
  // кнопка була б гіршою за відсутню.
  if (sessionId === null) return null;

  return (
    <Button
      size="compact-xs"
      variant="white"
      color="dark"
      loading={end.isPending}
      onClick={() => end.mutate(sessionId)}
    >
      {t('security.simulationEnd')}
    </Button>
  );
}

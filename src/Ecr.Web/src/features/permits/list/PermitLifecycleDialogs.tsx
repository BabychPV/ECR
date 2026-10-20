import { useRef, type JSX } from "react";
import { useNavigate } from "react-router-dom";
import { usePendingLoading } from "@/features/common/usePendingLoading";
import { formatDate } from "@/shared/format";
import { language, t } from "@/shared/i18n";
import { ConfirmModal } from "@/shared/ui/ConfirmModal";
import { showApiError, showDone } from "@/shared/ui/notify";
import { ReasonModal } from "@/shared/ui/ReasonModal";
import type { PermitListItem } from "../api/types";
import {
  permitHref,
  permitTypeName,
  type PermitAction,
} from "./permitsListModel";
import {
  useActivatePermit,
  useDeletePermit,
  useEndPermit,
  useRenewPermit,
} from "./usePermits";

/** Мінімальна довжина причини дострокового завершення: дзеркало сервера (`endReasonTooShort`, `{min}`). */
export const MinEndReasonLength = 10;

export interface PermitActionTarget {
  readonly item: PermitListItem;
  readonly action: PermitAction;
}

export interface PermitLifecycleDialogsProps {
  /** Що підтверджується зараз; `null` — діалогів немає. */
  readonly target: PermitActionTarget | null;
  readonly onClose: () => void;
}

function range(item: PermitListItem): { from: string; to: string } {
  return {
    from: item.validFrom === null ? "" : formatDate(item.validFrom),
    to: item.validTo === null ? "" : formatDate(item.validTo),
  };
}

/**
 * Діалоги життєвого циклу дозволу з переліку (макет `activate-permit`, `end-permit`, `renew-permit`; плюс
 * «Delete draft»): активація, дострокове завершення з причиною, поновлення, видалення чернетки. Одна дія за раз.
 *
 * ⛔ Відмову сервера показує `showApiError` — текст береться з каталогу за `messageKey` (`4092` перетин, `4096`
 * стан не той, `4095` уже поновлено). Діалог при відмові лишається відкритим: людина бачить причину й не
 * губить введену причину завершення.
 * Поновлення веде на нову чернетку (як у макеті), решта лишається в переліку.
 */
export function PermitLifecycleDialogs({
  target,
  onClose,
}: PermitLifecycleDialogsProps): JSX.Element {
  const navigate = useNavigate();
  const activate = useActivatePermit();
  const end = useEndPermit();
  const renew = useRenewPermit();
  const remove = useDeletePermit();
  const lang = language();
  const activatePending = usePendingLoading(activate.isPending);
  const endPending = usePendingLoading(end.isPending);
  const renewPending = usePendingLoading(renew.isPending);
  const removePending = usePendingLoading(remove.isPending);
  // ⛔ Запобіжник від подвійного кліку - у ref, а не в isPending: другий клік приходить до перемальовування.
  const busy = useRef(false);
  const once = (start: (settled: () => void) => void): void => {
    if (busy.current) return;
    busy.current = true;
    start(() => {
      busy.current = false;
    });
  };

  const item = target?.item;
  const action = target?.action;

  // P3-F2: діалог відкрито пунктом меню рядка, а пункт зникає разом із випадним списком. Після закриття (`target === null`)
  // дозвіл уже невідомий, тож id пам'ятається; фокус повертається на «⋯» цього рядка.
  const lastId = useRef<number | null>(null);
  if (item !== undefined) lastId.current = item.id;
  const rowMenuButton = (): HTMLElement | null =>
    lastId.current === null ? null : document.querySelector<HTMLElement>(`[data-permit-menu="${String(lastId.current)}"]`);
  const number = item?.number ?? "";
  const { from, to } = item === undefined ? { from: "", to: "" } : range(item);

  return (
    <>
      <ConfirmModal
        opened={action === "activate"}
        title={t("permits.list.activate.title", { number })}
        verb={t("permits.list.activate.verb")}
        consequences={[
          t("permits.list.activate.reads", {
            type: item === undefined ? "" : permitTypeName(item, lang),
            from,
            to,
          }),
          { text: t("permits.list.activate.editable"), note: true },
          { text: t("permits.list.activate.overlap"), note: true },
        ]}
        isPending={activatePending}
        onConfirm={() => {
          if (item === undefined) return;
          once((settled) =>
            activate.mutate(item.id, {
              onSuccess: () => {
                showDone(t("permits.list.done.activated", { number }));
                onClose();
              },
              onError: showApiError,
              onSettled: settled,
            }),
          );
        }}
        returnFocusTo={rowMenuButton}
        onClose={onClose}
      />

      <ReasonModal
        opened={action === "end"}
        title={t("permits.list.end.title", { number })}
        label={t("permits.list.end.reason")}
        description={t("permits.list.end.text")}
        confirmLabel={t("permits.list.end.verb")}
        minLength={MinEndReasonLength}
        isPending={endPending}
        onConfirm={(reason) => {
          if (item === undefined) return;
          once((settled) =>
            end.mutate(
              { id: item.id, body: { reason, endDate: null } },
              {
                onSuccess: () => {
                  showDone(t("permits.list.done.ended", { number }));
                  onClose();
                },
                onError: showApiError,
                onSettled: settled,
              },
            ),
          );
        }}
        returnFocusTo={rowMenuButton}
        onClose={onClose}
      />

      <ConfirmModal
        opened={action === "renew"}
        title={t("permits.list.renew.title", { number })}
        verb={t("permits.list.renew.verb")}
        consequences={[
          t("permits.list.renew.copy", { from: to }),
          { text: t("permits.list.renew.kept"), note: true },
          { text: t("permits.list.renew.switch"), note: true },
        ]}
        isPending={renewPending}
        onConfirm={() => {
          if (item === undefined) return;
          once((settled) =>
            renew.mutate(item.id, {
              onSuccess: (created) => {
                showDone(t("permits.list.done.renewed"));
                onClose();
                void navigate(permitHref(created.id));
              },
              onError: showApiError,
              onSettled: settled,
            }),
          );
        }}
        returnFocusTo={rowMenuButton}
        onClose={onClose}
      />

      <ConfirmModal
        opened={action === "delete"}
        title={t("permits.list.delete.title", { number })}
        verb={t("permits.list.delete.verb")}
        danger
        consequences={[
          t("permits.list.delete.removed"),
          { text: t("permits.list.delete.onlyDraft"), note: true },
        ]}
        isPending={removePending}
        onConfirm={() => {
          if (item === undefined) return;
          once((settled) =>
            remove.mutate(item.id, {
              onSuccess: () => {
                showDone(t("permits.list.done.deleted", { number }));
                onClose();
              },
              onError: showApiError,
              onSettled: settled,
            }),
          );
        }}
        returnFocusTo={rowMenuButton}
        onClose={onClose}
      />
    </>
  );
}

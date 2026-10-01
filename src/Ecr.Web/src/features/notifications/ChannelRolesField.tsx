import type { JSX } from "react";
import { MultiSelect, Text } from "@mantine/core";
import { useQuery } from "@tanstack/react-query";
import { apiFetch } from "@/api/client";
import type { RoleView } from "@/api/types";
import { t } from "@/shared/i18n";
import { can, useSession } from "@/shared/session/useSession";

/**
 * Ролі-адресати поштового каналу (`D-256`): лист іде активним користувачам цих ролей, що мають
 * адресу пошти, — кожному його мовою. Іменних осіб тут немає.
 *
 * ⚠ Перелік ролей — право `Security.ManageRoles`. Без нього (чи при відмові) вибору немає, але вже
 * задані ролі каналу ЛИШАЮТЬСЯ: форма повертає їх як є, а не стирає мовчки при збереженні.
 */
export function ChannelRolesField({
  value,
  onChange,
}: {
  readonly value: readonly number[];
  readonly onChange: (ids: number[]) => void;
}): JSX.Element {
  const session = useSession();
  const canList = can(session.data, "Security.ManageRoles");
  const roles = useQuery({
    queryKey: ["roles"],
    queryFn: () => apiFetch<RoleView[]>("/api/v1/roles"),
    enabled: canList,
  });

  if (!canList || roles.error !== null) {
    return (
      <Text size="sm" c="dimmed" data-channel-roles="unavailable">
        {t("notifications.channelRolesUnavailable")}
      </Text>
    );
  }

  return (
    <MultiSelect
      label={t("notifications.channelRoles")}
      description={t("notifications.channelRolesHint")}
      data={(roles.data ?? []).map((role) => ({
        value: String(role.id),
        label: role.code,
      }))}
      value={value.map(String)}
      searchable
      onChange={(ids) => onChange(ids.map(Number))}
    />
  );
}

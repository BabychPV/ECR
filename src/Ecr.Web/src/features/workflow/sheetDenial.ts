import { notifications } from '@mantine/notifications';
import { EcrApiError } from '@/api/client';
import { hasText, t } from '@/shared/i18n';
import { notificationCloseButtonProps, showApiError } from '@/shared/ui/notify';

/**
 * Відмови робочого процесу, що називають аркуш (`ECR-ACCS-0403`).
 *
 * ⛔ A2-08 (= A1-10, приймальна №2): сервер складає ці речення з каталогу і
 * підставляє в `{sheetDefId}` ЧИСЛО — «Sheet 2 cannot be approved…». Назви
 * аркуша мовою користувача в обробнику немає (лише id), і тягнути її туди
 * означало б новий порт у трьох обробниках. Екран же назву має — тож речення
 * перескладається тут із ТОГО САМОГО рядка каталогу (`messageKey`), з тими
 * самими підстановками, лише замість id — назва. Сервер і контракт не
 * змінюються; споживач API і далі отримує id.
 */
const SheetDenialKeys: ReadonlySet<string> = new Set([
  'err.ECR-ACCS-0403.submitDenied',
  'err.ECR-ACCS-0403.approveDenied',
  'err.ECR-ACCS-0403.reopenDenied',
]);

/**
 * Текст відмови з назвою аркуша (немає назви - з id); `null` — це інша відмова,
 * і її показує звичайний `showApiError`.
 *
 * ⚠ Причина - не сирий `reason` сервера: див. `denialReasonText`.
 *
 * @param error Відмова з мутації.
 * @param sheetName Назва аркуша мовою інтерфейсу.
 */
export function sheetDenialText(error: unknown, sheetName: string | null | undefined): string | null {
  if (!(error instanceof EcrApiError) || error.problem.errorCode !== 'ECR-ACCS-0403') return null;

  const extensions = error.problem.extensions2 ?? {};
  const key = extensions['messageKey'];
  if (typeof key !== 'string' || !SheetDenialKeys.has(key) || !hasText(key)) return null;

  const params: Record<string, string> = {};
  for (const [param, value] of Object.entries(extensions)) {
    if (typeof value === 'string') params[param] = value;
  }

  // PM-PASS-A-P3 п.7: назви аркуша може не бути (вкладка ще не завантажена) - тоді лишається id з відповіді,
  // як у сервера, але ПРИЧИНА все одно підставляється нижче: інакше `showApiError` показав би текст про комірку.
  const name = sheetName?.trim() ?? '';
  if (name.length > 0) {
    params['sheetDefId'] = `«${name}»`;
  } else if ((params['sheetDefId'] ?? '').length === 0) {
    return null;
  }

  const reason = denialReasonText(DenialActions[key] ?? 'approve', extensions);
  if (reason === null) return null;
  params['reason'] = reason;

  return t(key, params);
}

type DenialAction = 'submit' | 'approve' | 'reopen';

const DenialActions: Readonly<Record<string, DenialAction>> = {
  'err.ECR-ACCS-0403.submitDenied': 'submit',
  'err.ECR-ACCS-0403.approveDenied': 'approve',
  'err.ECR-ACCS-0403.reopenDenied': 'reopen',
};

/** Причини, які в каталозі говорять про ПРАВКУ КОМІРКИ - для дій над аркушем вони оманливі. */
const GrantReasonKeys: ReadonlySet<string> = new Set(['deny.NoGrant', 'deny.InsufficientGrantLevel']);

/** Ім'я причини сервера (`EditDenyReason`) - лише латинські літери, без крапок. */
const ReasonName = /^[A-Za-z]+$/;

/**
 * Причина відмови людською мовою; `null` - навіть узагальненого тексту в каталозі немає.
 *
 * ⚠ Ніколи не віддає сирий ідентифікатор. Порядок:
 * 1) ключ `reasonKey` з відповіді, а коли його нема (reopenDenied) - `deny.<reason>` за іменем причини;
 * 2) ✎ D4: причини-про-грант (`NoGrant`, `InsufficientGrantLevel`) замінюються власним текстом дії
 *    `deny.<дія>.NoRight`: людині бракує права на дію, а не на правку комірки;
 * 3) специфічний для дії `deny.<дія>.<Причина>` (напр. `deny.reopen.BusinessRule`);
 * 4) загальний рядок каталогу за ключем;
 * 5) `deny.reason.Unknown`.
 */
function denialReasonText(action: DenialAction, extensions: Record<string, unknown>): string | null {
  const rawKey = extensions['reasonKey'];
  const rawReason = extensions['reason'];

  let reasonKey: string | null = null;
  if (typeof rawKey === 'string' && rawKey.length > 0) {
    reasonKey = rawKey;
  } else if (typeof rawReason === 'string' && ReasonName.test(rawReason)) {
    reasonKey = `deny.${rawReason}`;
  }

  if (reasonKey !== null) {
    if (GrantReasonKeys.has(reasonKey)) {
      const own = NoRightTexts[action];
      if (hasText(own.key)) return own.text().replace(/\.+$/, '');
    }

    const specific = SpecificTexts[`${action}:${reasonKey}`];
    if (specific !== undefined && hasText(specific.key)) return specific.text().replace(/\.+$/, '');

    if (hasText(reasonKey)) return t(reasonKey).replace(/\.+$/, '');
  }

  return hasText('deny.reason.Unknown') ? t('deny.reason.Unknown') : null;
}

// Ключі - ЛІТЕРАЛАМИ в `t(...)`: сторож EndpointCoverageTests бачить їх у сіді, а не через змінну.
interface DenialText {
  readonly key: string;
  readonly text: () => string;
}

const NoRightTexts: Readonly<Record<DenialAction, DenialText>> = {
  submit: { key: 'deny.submit.NoRight', text: () => t('deny.submit.NoRight') },
  approve: { key: 'deny.approve.NoRight', text: () => t('deny.approve.NoRight') },
  reopen: { key: 'deny.reopen.NoRight', text: () => t('deny.reopen.NoRight') },
};

/** Причини, які для дії мають власний текст замість загального `deny.<Причина>`; ключ - `дія:reasonKey`. */
const SpecificTexts: Readonly<Record<string, DenialText>> = {
  'reopen:deny.BusinessRule': { key: 'deny.reopen.BusinessRule', text: () => t('deny.reopen.BusinessRule') },
};

/**
 * Показує відмову дії над аркушем: з назвою, якщо це відмова про аркуш,
 * інакше — як `showApiError`.
 *
 * @param error Відмова з мутації.
 * @param sheetName Назва аркуша мовою інтерфейсу.
 */
export function showSheetError(error: unknown, sheetName: string | null | undefined): void {
  const text = sheetDenialText(error, sheetName);
  if (text === null) {
    showApiError(error);
    return;
  }

  notifications.show({
    color: 'statusError',
    message: text,
    closeButtonProps: notificationCloseButtonProps,
  });
}

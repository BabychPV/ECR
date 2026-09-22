import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { emptyColumnDraft, type ColumnDraft } from '../column';
import { testTheme } from '@/test/render';

/**
 * Директива D15 §0, правило L10: «немає прав» ≠ «порожньо» ≠ «фільтр нічого не
 * знайшов». У цій формі клас коштує двічі, і другий раз — дорожче за перший.
 *
 * ⛔ Довідники: відмова `GET /api/v1/registries` давала порожній `Select` із
 * підписом «довідників не знайдено». Автор шаблону читає це як факт про світ —
 * і йде заводити довідник, який уже є.
 *
 * ⛔ Стилі: `existingStyle` рахувався як `styles.data?.find(...) ?? null`, тобто
 * відмова `GET …/styles` давала те саме значення, що й колонка БЕЗ стилю.
 * Увімкнення перемикача відкривало порожню чернетку з кодом `${code}Style` —
 * рівно того стилю, який форма створила колись, — і `saveColumn` записував її
 * першим `PUT …/styles/{code}`, затираючи збережене дефолтами. Коментар на
 * `ColumnEditor.tsx:91-94` каже, що запит існує САМЕ щоб цього не сталося.
 */

const SeededStrings: Record<string, string> = {
  'columns.lookupRegistryDefId': 'Registry',
  'columns.lookupRegistryDefIdHint': 'The registry this column looks values up from.',
  'columns.lookupRegistryDefIdEmpty': 'No registries found',
  'columns.customStyle': 'Custom style',
  'columns.customStyleHint': 'Draw this column differently.',
  'common.cancel': 'Cancel',
  'common.retry': 'Retry',
  'columns.save': 'Save',
};

const registries = [
  { id: 7, code: 'PERMITS', nameL10n: { values: { en: 'Permits' } }, fields: [], isHierarchical: false, isTemporal: true, sourceKind: 'Master' },
];

/** ⚠ Форма `StyleDefDto` (`style.ts`), а не вигадана: `styleDraftOf` читає саме ці поля. */
const styles = [
  {
    id: 5,
    code: 'AmountStyle',
    fontName: 'Calibri',
    // ⚠ Рядок, як на дроті: `fontSize` — `decimal` контракту (`e470777a`),
    // і число тут описувало б відповідь, якої сервер уже не віддає.
    fontSize: '11',
    isBold: true,
    isItalic: false,
    foregroundArgb: -16777216,
    backgroundArgb: null,
    borderJson: null,
    horizontalAlign: 2,
    verticalAlign: 1,
    wrapText: false,
    numberFormat: '#,##0.00',
  },
];

/** ⚠ Без `messageKey` подробиця до екрана не доходить (рішення про мову). */
function refusal(detail: string, errorCode: string, correlationId: string): unknown {
  return {
    type: 'about:blank',
    title: 'Internal Server Error',
    status: 500,
    detail,
    errorCode,
    correlationId,
    messageKey: `err.${errorCode}.unexpected`,
  };
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json',
    },
  });
}

function mockServer(fail: { registries: boolean; styles: boolean }): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      }

      if (path.endsWith('/api/v1/languages')) {
        return json([{ code: 'en', nameNative: 'English', isDefault: true }]);
      }

      if (path.endsWith('/api/v1/registries')) {
        return fail.registries
          ? json(refusal('перелік довідників прочитати не вдалося', 'ECR-SYS-0500', 'cid-reg-1'), 500)
          : json(registries);
      }

      if (/\/api\/v1\/template-versions\/\d+\/styles$/.test(path)) {
        return fail.styles
          ? json(refusal('стилі версії прочитати не вдалося', 'ECR-SYS-0503', 'cid-sty-1'), 500)
          : json(styles);
      }

      return json(null);
    }),
  );
}

async function show(draft: ColumnDraft, onChange: (next: ColumnDraft) => void = () => {}): Promise<void> {
  await loadCatalog('en', 'private');

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const { ColumnEditor } = await import('../ColumnEditor');

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <ColumnEditor
          draft={draft}
          disabled={false}
          saving={false}
          templateVersionId={1}
          onChange={onChange}
          onSubmit={() => {}}
          onCancel={() => {}}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/*
 * ⚠ Чернетка ЗАПОВНЕНА (код і заголовок), і це не косметика: форма малює
 * власний `Alert` із причиною, чому зберегти ще не можна, а Mantine `Alert` —
 * це теж `role="alert"`. На порожній чернетці `getByRole('alert')` знаходив би
 * «заголовок порожній» і твердження про відмову сервера лишалося б зеленим
 * незалежно від коду.
 */
const lookupDraft: ColumnDraft = {
  ...emptyColumnDraft(1),
  code: 'Amount',
  headerL10n: { en: 'Amount' },
  dataType: 'Lookup',
};

const styledDraft: ColumnDraft = {
  ...emptyColumnDraft(1),
  code: 'Amount',
  headerL10n: { en: 'Amount' },
  isNew: false,
  styleId: 5,
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ColumnEditor: відмова довідників ≠ «довідників не завели»', () => {
  it('довідники не приїхали — на екрані причина з кодом, а порожнього переліку НЕМАЄ', async () => {
    mockServer({ registries: true, styles: false });
    await show(lookupDraft);

    const alert = await waitFor(() => screen.getByRole('alert'));

    expect(alert.textContent ?? '').toContain('перелік довідників прочитати не вдалося');
    expect(alert.textContent ?? '').toContain('ECR-SYS-0500');

    // ⛔ Головне твердження картки: елемент, для якого даних немає, не
    // малюється. Порожній `Select` із «довідників не знайдено» — це і є та
    // сама брехня про світ, від якої PR.
    expect(screen.queryByLabelText('Registry')).toBeNull();
  });

  it('довідники приїхали — перелік на місці, банера немає', async () => {
    mockServer({ registries: false, styles: false });
    await show(lookupDraft);

    // ⚠ Спершу дочекатися саме наповненого переліку: запит у дорозі зробив би
    // друге твердження зеленим на будь-якому коді.
    const select = await screen.findByLabelText('Registry');

    expect((select as HTMLInputElement).disabled).toBe(false);
    expect(screen.queryByRole('alert')).toBeNull();
  });
});

describe('ColumnEditor: невідомий стан стилю ≠ «стилю немає»', () => {
  it('стилі не приїхали — перемикач недоступний, причину видно, порожня чернетка не відкривається', async () => {
    const onChange = vi.fn();
    mockServer({ registries: false, styles: true });
    await show(styledDraft, onChange);

    // ⚠ Спершу відмова має прийти в стан, і лише ПОТІМ питаємо про перемикач:
    // інакше випадок був би зеленим і на коді, що просто малює форму повільно
    // (урок із #446).
    const alert = await waitFor(() => screen.getByRole('alert'));
    expect(alert.textContent ?? '').toContain('стилі версії прочитати не вдалося');

    // ⚠ За роллю з регекспом, а не `getByLabelText('Custom style')`: у Mantine
    // `Switch` опис лежить ВСЕРЕДИНІ `<label>`, тож доступна назва — це
    // «Custom style» разом із підказкою, і точний збіг не спрацював би.
    const toggle = screen.getByRole('switch', { name: /Custom style/ }) as HTMLInputElement;
    expect(toggle.disabled).toBe(true);

    // ⛔ І жодна чернетка стилю не потрапила у форму сама: єдине, що могло б
    // викликати `onChange`, — автозаповнення з переліку, якого немає.
    expect(onChange).not.toHaveBeenCalled();
  });

  it('стилі приїхали — перемикач ДОСТУПНИЙ і форма бачить збережені значення', async () => {
    const onChange = vi.fn();
    mockServer({ registries: false, styles: false });
    await show(styledDraft, onChange);

    // ⚠ Дзеркало: заборона не стала тотальною. Без цього випадку «полагодити»
    // запобіжник можна було б назавжди вимкненим перемикачем.
    await waitFor(() => {
      expect(onChange).toHaveBeenCalledWith(
        expect.objectContaining({ style: expect.objectContaining({ code: 'AmountStyle' }) }),
      );
    });

    const toggle = screen.getByRole('switch', { name: /Custom style/ }) as HTMLInputElement;
    expect(toggle.disabled).toBe(false);
  });
});

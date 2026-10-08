import { expect, test } from '@playwright/test';
import { DocumentId, mockDocumentCard, PeriodKey, signIn } from './staleContractMocks';

/**
 * RC15-E: секція «Contract» шапки документа на живому стенді (`DocumentHeaderPanel`, RC15-A).
 * Порядок полів як в Excel, вибір Lookup зі словника + збереження + перезавантаження, File Number і Version
 * не редагуються, але фокусуються. Запуск «Аудитом»/CI: `tools/e2e-stand.ps1 … -Grep "Contract"`.
 *
 * ⚠ Стенд не має шапки Contract: `GET …/header`, довідники й `PATCH …/header` підмінені маршрутами зі станом у
 * пам'яті (відповідь після перезавантаження віддає збережене). Користувацький шлях (клік, вибір, «Save») —
 * справжній; сервер шапки тут не перевіряється (його ганяють інтеграційні тести).
 */
const Dictionaries: Record<string, { id: number; display: string }[]> = {
  E2E_AREA: [
    { id: 11, display: 'Атирау' },
    { id: 12, display: 'Мангістау' },
  ],
  E2E_CONTRACTOR: [{ id: 21, display: 'Підрядник А' }],
  E2E_REGION: [{ id: 31, display: 'Захід' }],
  E2E_LOCATION: [{ id: 41, display: 'Майданчик 1' }],
  E2E_ONOFF: [
    { id: 51, display: 'Onshore' },
    { id: 52, display: 'Offshore' },
  ],
  E2E_ACTIVITY: [{ id: 61, display: 'Видобуток' }],
  E2E_PERMIT: [{ id: 71, display: 'Дозвіл №1' }],
};

/** Поля навмисно у ПЕРЕМІШАНОМУ порядку: порядок на екрані мусить братися з `ContractOrder`, а не з відповіді. */
const FieldDefs = [
  { code: 'Permit', en: 'Permit', ru: 'Разрешение', registry: 'E2E_PERMIT', id: 107 },
  { code: 'TypeOfActivity', en: 'Type of Activity', ru: 'Вид деятельности', registry: 'E2E_ACTIVITY', id: 106 },
  { code: 'Location', en: 'Location', ru: 'Местоположение', registry: 'E2E_LOCATION', id: 104 },
  { code: 'Area', en: 'Area', ru: 'Район', registry: 'E2E_AREA', id: 101 },
  { code: 'OnOffshore', en: 'Onshore/Offshore', ru: 'Суша/Море', registry: 'E2E_ONOFF', id: 105 },
  { code: 'Region', en: 'Region', ru: 'Регион', registry: 'E2E_REGION', id: 103 },
  { code: 'Contractor', en: 'Contractor', ru: 'Подрядчик', registry: 'E2E_CONTRACTOR', id: 102 },
] as const;

const RegistryIds = Object.fromEntries(Object.keys(Dictionaries).map((code, index) => [code, 9001 + index]));

test.describe('Contract: шапка документа', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  test('Contract: порядок полів, вибір зі словника переживає перезавантаження, службові поля лише для читання', async ({
    page,
  }) => {
    test.slow();

    // Стан «сервера» шапки: значення Lookup за кодом поля і версія (змінюється з кожним збереженням).
    const stored: Record<string, number | null> = {};
    let version = 'v1';
    const patches: { code: string; value: unknown }[] = [];

    await mockDocumentCard(page, { stale: false });
    await page.route(`**/api/v1/documents/${DocumentId}/header`, async (route) => {
      const request = route.request();
      if (request.method() === 'PATCH') {
        const body = request.postDataJSON() as { fields: { code: string; value: unknown; isEmpty: boolean }[] };
        for (const field of body.fields) {
          patches.push({ code: field.code, value: Number(field.value) });
          stored[field.code] = field.isEmpty ? null : Number(field.value);
        }
        version = `v${String(Number(version.slice(1)) + 1)}`;
      } else if (request.method() !== 'GET') {
        return route.fallback();
      }
      return route.fulfill({
        json: {
          version,
          fields: FieldDefs.map((def, index) => ({
            headerFieldDefId: def.id,
            code: def.code,
            label: { values: { en: def.en, ru: def.ru } },
            dataType: 'Lookup',
            isRequired: false,
            lookupRegistryDefId: RegistryIds[def.registry],
            value: stored[def.code] ?? null,
            ordinal: index + 1,
          })),
        },
      });
    });
    await page.route('**/api/v1/registries', (route) =>
      route.request().method() === 'GET'
        ? route.fulfill({
            json: Object.keys(Dictionaries).map((code) => ({
              id: RegistryIds[code],
              code,
              isTemporal: false,
              isHierarchical: false,
              fields: [],
            })),
          })
        : route.fallback(),
    );
    await page.route(/\/api\/v1\/registries\/(E2E_[A-Z_]+)\/entries/, (route) => {
      const code = /\/registries\/(E2E_[A-Z_]+)\/entries/.exec(route.request().url())?.[1] ?? '';
      return route.fulfill({
        json: (Dictionaries[code] ?? []).map((entry) => ({
          id: entry.id,
          code: `${code}-${String(entry.id)}`,
          display: entry.display,
          parentEntryId: null,
          validFrom: null,
          validTo: null,
        })),
      });
    });

    await signIn(page);

    const openContract = async (): Promise<void> => {
      await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
      const toggle = page.getByTestId('document-header-toggle');
      await expect(toggle).toBeVisible({ timeout: 60_000 });
      if ((await toggle.getAttribute('aria-expanded')) !== 'true') await toggle.click();
      await expect(toggle).toHaveAttribute('aria-expanded', 'true');
      await expect(page.getByTestId('document-header-contract')).toBeVisible();
    };

    await openContract();
    const contract = page.getByTestId('document-header-contract');
    await expect(contract.getByRole('heading', { name: /Contract|Контракт|Келісімшарт/ })).toBeVisible();

    // (1) Порядок як в Excel; File Number і Version — службові рядки на своїх місцях.
    await expect(contract.locator('[data-header-field]')).toHaveCount(9);
    const order = await contract.locator('[data-header-field]').evaluateAll((nodes) =>
      nodes.map((node) => node.getAttribute('data-header-field')),
    );
    expect(order).toEqual([
      'Area',
      'Contractor',
      'Region',
      'Location',
      'OnOffshore',
      'TypeOfActivity',
      'fileNumber',
      'Permit',
      'version',
    ]);

    // Підпис двомовний «EN — RU».
    await expect(contract.getByLabel('Area — Район')).toBeVisible();

    // (2) Службові поля: readOnly без disabled, фокусуються, ввід нічого не змінює.
    for (const key of ['fileNumber', 'version']) {
      const input = contract.locator(`[data-header-field="${key}"]`);
      await expect(input).toHaveAttribute('readonly', '');
      await expect(input).toBeEnabled();
      const before = await input.inputValue();
      expect(before, `${key}: службове поле порожнє`).not.toBe('');
      await input.focus();
      await expect(input).toBeFocused();
      await page.keyboard.type('zzz');
      await expect(input).toHaveValue(before);
    }

    // (3) Вибір зі словника → «Save» → PATCH → перезавантаження зберігає вибір.
    const area = contract.getByLabel('Area — Район');
    await area.click();
    await page.getByRole('option', { name: 'Атирау' }).click();
    await expect(area).toHaveValue('Атирау');

    const saved = page.waitForResponse(
      (response) => response.request().method() === 'PATCH' && response.url().includes(`/documents/${DocumentId}/header`),
    );
    await page
      .getByTestId('document-header-panel')
      .getByRole('button', { name: /^(Save|Зберегти|Сохранить|Сақтау)$/ }).click();
    expect((await saved).ok()).toBe(true);
    expect(patches).toEqual([{ code: 'Area', value: 11 }]);

    await page.reload();
    await openContract();
    await expect(page.getByTestId('document-header-contract').getByLabel('Area — Район')).toHaveValue('Атирау', {
      timeout: 15_000,
    });
  });
});

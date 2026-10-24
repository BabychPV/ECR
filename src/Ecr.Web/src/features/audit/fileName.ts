/**
 * Ім'я файлу з `Content-Disposition`; `filename*` (RFC 5987) має перевагу.
 *
 * ⚠ Шлях і керівні символи відкидаються: ім'я прийшло ззовні, а `download`
 * зі слешем браузери трактують по-різному.
 */
export function fileNameOf(disposition: string | null): string | null {
  if (disposition === null) return null;

  const extended = /filename\*\s*=\s*[^']*'[^']*'([^;]+)/i.exec(disposition);
  let name: string | null = null;

  if (extended?.[1] !== undefined) {
    try {
      name = decodeURIComponent(extended[1].trim());
    } catch {
      name = null;
    }
  }

  if (name === null) {
    const plain = /filename\s*=\s*("([^"]*)"|[^;]+)/i.exec(disposition);
    name = (plain?.[2] ?? plain?.[1])?.trim() ?? null;
  }

  if (name === null) return null;

  const safe = name.replace(/[\\/\p{Cc}]/gu, '_').trim();

  return safe.length > 0 ? safe : null;
}

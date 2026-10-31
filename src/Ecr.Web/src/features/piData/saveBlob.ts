/**
 * Віддає blob браузеру як завантаження (`D-463`, CSV точок PI).
 *
 * ⚠ URL звільняється одразу після кліку: інакше кожен експорт тримав би весь файл у пам'яті вкладки до її закриття
 * (той самий прийом, що в `AuditExportButton`).
 */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);

  try {
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    anchor.style.display = 'none';
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  } finally {
    URL.revokeObjectURL(url);
  }
}

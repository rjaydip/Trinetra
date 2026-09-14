/** Triggers a browser download of in-memory text via a throwaway object URL — the shared pattern
 * behind every "Download …" button in this app (bulk-import samples, registry CSV export). */
export function downloadText(text: string, mimeType: string, fileName: string) {
  const blob = new Blob([text], { type: mimeType });
  const objectUrl = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = objectUrl;
  link.download = fileName;
  link.click();
  URL.revokeObjectURL(objectUrl);
}

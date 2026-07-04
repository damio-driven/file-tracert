/**
 * Client-side mirror of the backend `OperationName.TryValidateLeaf` (FileTracert.Business).
 * The backend is the authority — this only gives the user instant, inline feedback in the
 * rename / new-folder dialogs before a round-trip. Rules must stay in sync with the server.
 */

// Characters Windows forbids in a file/folder name (separators handled explicitly below).
// Space and dash ARE allowed (e.g. "New Album"), matching Path.GetInvalidFileNameChars().
const INVALID_CHARS = new RegExp('[<>:"|?*\\u0000-\\u001f]');

/**
 * Validates a single folder/file name (no path). Returns an Italian error message,
 * or null when the name is valid.
 */
export function validateLeafName(name: string): string | null {
  const trimmed = name.trim();

  if (trimmed.length === 0) {
    return 'Il nome non può essere vuoto.';
  }
  if (trimmed === '.' || trimmed === '..') {
    return "Il nome non può essere '.' o '..'.";
  }
  if (trimmed.includes('\\') || trimmed.includes('/')) {
    return "Il nome non può contenere separatori di percorso ('\\' o '/').";
  }
  if (INVALID_CHARS.test(trimmed)) {
    return 'Il nome contiene caratteri non consentiti.';
  }
  return null;
}

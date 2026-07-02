# Move dialog: folder-tree picker (replace manual path input)

## Problem

`OperationPicker` ("Sposta N file" dialog) currently requires the user to
type the destination folder as a free-text relative path
(`operation-picker.html` — "Cartella destinazione" input, placeholder
`es. Documenti\Foto 2025`). No way to browse or pick a folder, no way to
create a new folder from the dialog. This is the reported bug/UX gap to fix.

## Scope

Frontend-only change, confined to
`src/frontend/src/app/shared/components/operation-picker/`
(`operation-picker.ts`, `.html`, `.scss`) and its spec.

**No backend changes.** `CatalogApi.children(volumeId, directoryId, skip, take)`
(`core/api/catalog-api.service.ts`) already returns the catalogued directory
tree for a volume, works offline (same guarantee the Catalogo screen already
relies on — catalog data is DB-backed, not a live filesystem read). Preview
(`/operations/preview`) and enqueue (`/operations`) already accept a plain
`targetRelativePath` string; this feature only changes how that string is
produced client-side.

The existing real-filesystem browse endpoint
(`FoldersController` / `SetupApi.browse`, used by the Volumi/watched-roots
picker) is explicitly **not** reused here: it 409s when the volume is
offline, which conflicts with the move dialog's requirement to accept
targets on offline volumes (estimate-based feasibility, per the project's
projection model).

Manual path entry is fully removed — no advanced-mode fallback.

## Design

### New local state in `OperationPicker`

- `targetDirId: number | null` — current real directory being browsed
  (`null` = volume root).
- `crumbs: { id: number; name: string }[]` — breadcrumb of real directories
  walked into, root not included (root is implicit/always first chip).
- `newFolderSegments: string[]` — names entered via "new folder", appended
  after `crumbs`. These do not exist on disk yet; no API call is made for
  them. Backend already creates missing target folders at job execution
  time (existing hint text: "La cartella viene creata se non esiste").
- `dirChildren` signal (`CatalogDirDto[]`) — subfolders of `targetDirId`,
  fetched via `CatalogApi.children`. Not fetched while
  `newFolderSegments.length > 0` (nothing real to list — the current
  position is virtual).
- `loadingDirs`, `newFolderInputOpen`, `newFolderName` — UI-only state for
  the inline create affordance.

Resetting: changing `targetVolumeId` clears `crumbs`, `newFolderSegments`,
`dirChildren` and refetches root children for the new volume.

### UI (replacing the "Cartella destinazione" text input block)

- **Breadcrumb row**: chip for volume root, then one chip per `crumbs`
  entry, then one chip per `newFolderSegments` entry. Clicking a real-crumb
  chip truncates `crumbs`/`newFolderSegments` to that point and refetches
  children for that directory. Clicking a new-folder chip truncates
  `newFolderSegments` only (no fetch needed, still virtual).
- **Folder list panel**: rows for each entry in `dirChildren`, using the
  same visual language as Catalogo's `dir-card` (folder icon, name,
  child-dir/file counts, `materializedPath` tooltip). Clicking a row pushes
  it onto `crumbs`, sets `targetDirId`, fetches its children.
- **Empty state**: "Cartella vuota" when `dirChildren` is empty, or always
  when inside a virtual (`newFolderSegments`) position.
- **`+ Nuova cartella`**: toggle button reveals a small inline text input +
  confirm button. Confirming a non-empty name pushes it onto
  `newFolderSegments` and clears/hides the input. Can be invoked again while
  already inside a virtual segment (nesting new folders inside new
  folders).
- **Offline banner**: when the selected target volume is offline, show a
  faint non-blocking notice reusing Catalogo's pattern ("dati aggiornati al
  …", from `lastSeenUtc`). Browsing still works against the last-known
  index.

### Path derivation

```
targetFolder = [...crumbs.map(c => c.name), ...newFolderSegments].join('\\')
```

Empty string is valid (volume root as target). `canSubmit` changes from
"non-empty trimmed string" to `!!targetVolumeId` — an empty folder path
(root) is now a legitimate, submittable target, consistent with "folder is
created if missing" applying at any depth including none.

`runPreview()` / `enqueue()` are unchanged apart from reading the derived
`targetFolder` instead of a bound input value.

## Testing

`operation-picker.spec.ts` (Vitest), mocking `CatalogApi.children`:

- Selecting a volume fetches and renders root children.
- Clicking a directory row descends: updates breadcrumb, fetches its
  children, updates derived path.
- Clicking a breadcrumb chip navigates back and refetches correctly.
- "+ Nuova cartella" pushes a virtual segment, suppresses further fetches,
  and can nest a second virtual segment.
- Navigating back past a virtual segment via breadcrumb click drops it
  without an API call.
- Derived `targetFolder` (including the empty/root case) matches what's
  sent as `targetRelativePath` in both `preview()` and `enqueue()` calls.
- `canSubmit` is true with volume selected and empty path (root target).

## Out of scope

- Backend changes of any kind.
- Manual/advanced text-path fallback.
- Creating the folder eagerly (e.g. as a separate `CreateFolder` job) —
  execution-time auto-create already covers this per existing backend
  behavior.

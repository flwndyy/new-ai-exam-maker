# B9 Exam Composer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Frontend screens for listing/creating exams and composing an exam version (pick, order, score questions; save; submit/withdraw/archive/new version) on top of the existing A7 API.

**Architecture:** Pure helpers in `src/lib/exams/` (decimal points math, Zod form schema, selection/permission rules) are unit-tested first; React components in `src/components/exams/` consume them; two static-export pages (`/exams/`, `/exams/view/?id=&vid=`) wire components to TanStack Query. No backend changes.

**Tech Stack:** Next.js static export (App Router, TS), TanStack Query, React Hook Form + `useFieldArray` + Zod 4 (`@hookform/resolvers/zod`), `openapi-fetch` client `api`, Tailwind, Vitest + Testing Library.

**Spec:** `docs/superpowers/specs/2026-11-23-b9-exam-composer-design.md`

## Global Constraints

- Work on a new branch `feat/b9-exam-composer` from `dev`; PR into `dev` (Squash). Do not touch the uncommitted B8 files on other branches.
- Commit steps run only after the user has authorized committing in this session (CLAUDE.md: không tự commit/push khi chưa được yêu cầu).
- No new npm dependencies; no backend/OpenAPI changes.
- Dynamic routes via query string only: `/exams/view/?id=<examId>&vid=<versionId>`.
- All UI copy in `src/messages/vi.ts` (Vietnamese); code/identifiers/comments in English.
- Question content renders only through `MathMarkdown` / `QuestionPayloadView`; never build answer content from anything but API responses.
- Points: never add/split with JS float; use `src/lib/exams/points.ts`. Convert to number only for the request body, via `parseExactDecimal`.
- Composer limits (match API): `durationMinutes` integer 1–600; ≤ 200 items; no duplicate `sourceQuestionId`; points > 0 matching `^\d+([.,]\d+)?$`. New item default points `"1"`. "Chia đều" total 10, 2 decimals, remainder on the last item.
- Editing allowed only when `me.id === exam.createdBy.id`, `me.role ∈ {teacher, head}`, status ∈ {Draft, Rejected}. Admin never sees create/edit controls.
- Tests mock `@/lib/api/client`, `@/lib/auth/auth-context`, `next/navigation` the way `src/app/ai/page.test.tsx` does; synthetic data only (`@example.invalid`).
- Run commands from `frontend/`.

## Review Focus

1. Comma decimals and float-prone sums (`"0,1"` + `"0.2"`, three × `3.33`/`3.34`): totals must read exactly `0.3` / `10` and the PUT body must carry dot numbers → Task 1 tests.
2. Unsaved edits lost when switching version via the dropdown or after a background refetch → Task 6 test (confirm on switch) and `refetchOnWindowFocus: false` on the version query.
3. Item whose content is `null` (`SourceContentUnavailable`) must still render, be removable, and be saved back with its id/points → Task 6 test.
4. Teacher opening another teacher's Published exam: version GET returns 403 → message, no edit/action controls → Task 5 test.
5. Empty Draft: save allowed, "Gửi duyệt" disabled; "Tạo phiên bản mới" hidden while a Draft exists → Task 7 tests.

---

### Task 1: Points math and composer form schema

**Files:**
- Create: `src/lib/exams/points.ts`, `src/lib/exams/composer-schema.ts`
- Test: `src/lib/exams/points.test.ts`, `src/lib/exams/composer-schema.test.ts`

**Interfaces:**
- Produces:
  - `sumDecimalStrings(values: string[]): string` — accepts `,` or `.`; blank/invalid entries count as 0; returns normalized string without trailing zeros (`"10"`, `"2.5"`, `"0"`).
  - `splitPointsEvenly(total: number, n: number): string[]` — `n <= 0` → `[]`; integer-cent arithmetic: `base = floor(total*100/n)`, last = `total*100 - base*(n-1)`; format cents to normalized decimal string.
  - `type ItemView = { stem: string|null; type: QuestionType|null; difficulty: Difficulty|null; payload: QuestionPayload|null; explanation: string|null; sourceRevision: number|null; currentSourceRevision: number|null; sourceStatus: QuestionStatus|null }`
  - `type ComposerItem = { sourceQuestionId: string; points: string; view: ItemView }`
  - `type ComposerFormInput = { durationMinutes: string; items: ComposerItem[] }`
  - `composerSchema` (Zod) — output `components["schemas"]["ExamVersionUpdate"]` (drops `view`, points via `parseExactDecimal`). Errors: points at `items.<i>.points`, duplicates at `items`, duration at `durationMinutes`; messages from `vi.exams.validation.*` (Task 2 adds the keys — in this task reference them; if Task 2 not done yet, add the `validation` sub-block now).
  - `toFormValues(version: ExamVersion): ComposerFormInput` (points via `formatDecimalForInput`, `view` via `itemViewFromVersionItem`).
  - `itemViewFromVersionItem(item: ExamVersionItem): ItemView` (`sourceStatus: null`); `itemViewFromQuestion(q: Question): ItemView` (`sourceRevision`/`currentSourceRevision` null, `sourceStatus: q.status`).

- [ ] **Step 1: Write failing tests**

```ts
expect(sumDecimalStrings(["0.1", "0,2"])).toBe("0.3");
expect(sumDecimalStrings(["1.25", "1.25", "", "abc"])).toBe("2.5");
expect(sumDecimalStrings([])).toBe("0");
expect(splitPointsEvenly(10, 3)).toEqual(["3.33", "3.33", "3.34"]);
expect(splitPointsEvenly(10, 8)).toEqual(Array(8).fill("1.25"));
expect(splitPointsEvenly(10, 4)).toEqual(["2.5", "2.5", "2.5", "2.5"]);
expect(splitPointsEvenly(10, 0)).toEqual([]);
expect(sumDecimalStrings(splitPointsEvenly(10, 7))).toBe("10");

// composerSchema
ok({ durationMinutes: "45", items: [item("q1", "1,5")] }) → { durationMinutes: 45, items: [{ sourceQuestionId: "q1", points: 1.5 }] }
fails: points "0", "-1", "1e2", "" → issue path ["items",0,"points"]
fails: duration "0", "601", "4.5" → path ["durationMinutes"]
fails: two items "q1" → path ["items"]
fails: 201 items
ok: items [] (empty draft)
toFormValues(version with item points 1.25) → items[0].points === "1.25", view.currentSourceRevision copied
```

- [ ] **Step 2: Run** `npx vitest run src/lib/exams` — Expected: FAIL (modules missing).
- [ ] **Step 3: Implement** both files per Interfaces.
- [ ] **Step 4: Run** `npx vitest run src/lib/exams` — Expected: PASS.
- [ ] **Step 5: Commit** `feat(frontend): add exam composer points math and form schema`

---

### Task 2: Selection/permission rules, messages, nav link

**Files:**
- Create: `src/lib/exams/rules.ts`
- Modify: `src/messages/vi.ts` (add `exams` block; replace `examView` usage), `src/components/role-nav.tsx`
- Test: `src/lib/exams/rules.test.ts`, `src/components/role-nav.test.tsx` (add case)

**Interfaces:**
- Produces:
  - `isSelectable(q: Question, meId: string): boolean` — `(q.createdBy.id === meId && q.status !== "Archived") || q.status === "Approved"`.
  - `hasRevisionDrift(v: ItemView): boolean` — both revisions non-null and different.
  - `pickDefaultVersionId(versions: ExamVersionSummary[], requested: string|null): string|undefined` — requested if present in list; else first (versions are `versionNo` desc) with status Draft/Rejected; else `versions[0]?.id`.
  - `export type Role = "teacher" | "head" | "admin"`
  - `versionActions(a: { status: ExamVersionStatus; isAuthor: boolean; role: Role; hasDraft: boolean }): { canEdit: boolean; submit: boolean; withdraw: boolean; archive: boolean; createVersion: boolean }` — all false unless `isAuthor && role !== "admin"`; then per spec table: Draft → canEdit+submit; Rejected → canEdit+submit+archive+createVersion(!hasDraft); Submitted → withdraw; Approved → archive+createVersion(!hasDraft); Published/Archived → createVersion(!hasDraft).
  - `vi.exams` keys used by later tests (exact copy):
    `listTitle: "Đề thi"`, `create: "Tạo đề"`, `mineOnly: "Chỉ đề của tôi"`, `titleLabel: "Tiêu đề"`, `editTitle: "Sửa tiêu đề"`, `versionLabel: "Phiên bản"`, `duration: "Thời gian (phút)"`, `totalPoints: "Tổng điểm"`, `questionCount: "Số câu"`, `bankTitle: "Ngân hàng câu hỏi"`, `itemsTitle: "Câu trong đề"`, `add: "Thêm"`, `remove: "Bỏ"`, `moveUp: "Lên"`, `moveDown: "Xuống"`, `splitEvenly: "Chia đều 10 điểm"`, `save: "Lưu"`, `submit: "Gửi duyệt"`, `withdraw: "Rút lại"`, `archive: "Lưu trữ"`, `createVersion: "Tạo phiên bản mới"`, `saveBeforeSubmit: "Lưu thay đổi trước khi gửi duyệt."`, `revisionDrift: "Câu nguồn đã thay đổi so với lúc chọn"`, `contentUnavailable: "Không xem được nội dung câu này"`, `needsApproval: "Phải được duyệt trước khi gửi đề"`, `notApproved: "Các câu được tô đỏ chưa được duyệt."`, `conflictReload: "Đề vừa thay đổi ở nơi khác, hãy tải lại."`, `reload: "Tải lại"`, `confirmDiscard: "Bạn có thay đổi chưa lưu. Bỏ các thay đổi này?"`, `confirmSubmit: "Gửi phiên bản này cho tổ trưởng duyệt?"`, `confirmArchive: "Phiên bản đã lưu trữ không thể khôi phục. Tiếp tục?"`, `draftExists: "Đề đang có bản nháp."`, `studentSheetOnly: "Bạn chỉ được xem bản đề học sinh của phiên bản này."`, `notFound: "Không tìm thấy đề hoặc bạn không có quyền xem."`, `missingId: "Thiếu mã đề thi trên đường dẫn."`, `savedSuccess: "Đã lưu phiên bản."`, `validation: { pointsInvalid: "Điểm phải là số dương.", durationInvalid: "Thời gian từ 1 đến 600 phút.", duplicate: "Câu hỏi bị trùng trong đề.", tooMany: "Tối đa 200 câu.", titleRequired: "Nhập tiêu đề (tối đa 300 ký tự).", required: "Bắt buộc chọn." }`. Status labels reuse `vi.review.examStatuses`; type/difficulty labels reuse `vi.questions.types/difficulties`.
  - `RoleNav` shows link `vi.nav.exams` → `/exams/` for every signed-in role.

- [ ] **Step 1: Write failing tests** — `versionActions` table-driven over all 6 statuses × {author teacher, author head, non-author head, admin} with `hasDraft` true/false; `pickDefaultVersionId` (requested valid, requested foreign → fallback, no editable → first, empty → undefined); `isSelectable` (own Draft true, own Archived false, other Approved true, other Submitted false); `hasRevisionDrift` (2 vs 3 true, null current false, equal false); RoleNav: each role sees link named `messages.nav.exams` with href matching `/^\/exams\/?$/`.
- [ ] **Step 2: Run** `npx vitest run src/lib/exams/rules.test.ts src/components/role-nav.test.tsx` — Expected: FAIL.
- [ ] **Step 3: Implement** `rules.ts`, `vi.exams`, nav link. Remove `vi.examView` once Task 5 replaces its only consumer (keep it in this task to stay green).
- [ ] **Step 4: Run** same command — Expected: PASS.
- [ ] **Step 5: Commit** `feat(frontend): add exam rules, messages and nav link`

---

### Task 3: Exam list page and create form

**Files:**
- Create: `src/app/exams/page.tsx`, `src/components/exams/create-exam-form.tsx`
- Test: `src/app/exams/page.test.tsx`

**Interfaces:**
- Consumes: `vi.exams.*`, `fetchAllSubjects(departmentId)`, `api`.
- Produces: `CreateExamForm({ departmentId: string; onCreated: (exam: ExamDetail) => void })` — RHF + Zod: title trimmed 1–300, `subjectId` required, grade `"10"|"11"|"12"` → `Grade`. `POST /exams`; on 400 map `errors.title/subjectId/grade` to fields via `setError`, else form-level `getApiErrorMessage`.
- Page: `GET /exams` with `page`, `pageSize: 20`, `subjectId`, `grade`, `mine`; columns title (link `/exams/view/?id=`), subject name (from `fetchAllSubjects()`), grade, `createdBy.fullName`, `v{latestVersionNo}`, published badge if `publishedVersionId`, `updatedAt` in `Asia/Ho_Chi_Minh`. Create button only when `role ∈ {teacher, head}` and `me.departmentId`. On created → `router.push(\`/exams/view/?id=${exam.id}&vid=${exam.versions[0].id}\`)`.

- [ ] **Step 1: Write failing tests** — renders rows from mocked `/exams`; admin sees no `messages.exams.create` button; teacher toggling `mineOnly` re-queries with `mine: true`; create flow posts `{ title: "Kiểm tra 15 phút", subjectId: SUBJECT, grade: 10 }` and pushes `/exams/view/?id=E1&vid=V1`; 400 `{ errors: { title: ["Trùng"] } }` shows "Trùng" next to the title field.
- [ ] **Step 2: Run** `npx vitest run src/app/exams/page.test.tsx` — Expected: FAIL.
- [ ] **Step 3: Implement** page + form.
- [ ] **Step 4: Run** same — Expected: PASS.
- [ ] **Step 5: Commit** `feat(frontend): add exam list and create form`

---

### Task 4: Question picker

**Files:**
- Create: `src/components/exams/question-picker.tsx`
- Test: `src/components/exams/question-picker.test.tsx`

**Interfaces:**
- Consumes: `isSelectable`, `itemViewFromQuestion`, `fetchAllTopics`, `vi.exams`, `vi.questions.types/difficulties/statuses`.
- Produces: `QuestionPicker({ subjectId: string; grade: Grade; meId: string; selectedIds: ReadonlySet<string>; onAdd: (q: Question) => void })` — `GET /questions` with fixed `subjectId`, `grade`, optional `topicId`, `type`, `difficulty`, `mine`, `page`, `pageSize: 20`; query key `["questionsPicker", filters]`. Each row: stem via `MathMarkdown` (clamped), status label, "Thêm" button only if `isSelectable && !selectedIds.has(q.id)`; non-Approved selectable rows show `vi.exams.needsApproval`.

- [ ] **Step 1: Write failing tests** — request always includes the exam's `subjectId`/`grade`; own Draft row has `Thêm` + needsApproval text; other's Approved row has `Thêm`; other's Submitted row has no `Thêm`; row already in `selectedIds` has no `Thêm`; clicking `Thêm` calls `onAdd` with the question; changing the topic filter re-queries with `topicId`.
- [ ] **Step 2: Run** `npx vitest run src/components/exams/question-picker.test.tsx` — Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** same — Expected: PASS.
- [ ] **Step 5: Commit** `feat(frontend): add exam question picker`

---

### Task 5: Exam view page shell (load, version switch, read-only, title edit)

**Files:**
- Modify: `src/app/exams/view/page.tsx` (replace placeholder)
- Create: `src/components/exams/exam-header.tsx`, `src/components/exams/version-readonly.tsx`, `src/components/exams/version-item-card.tsx`
- Test: `src/app/exams/view/page.test.tsx`

**Interfaces:**
- Consumes: `pickDefaultVersionId`, `versionActions`, `hasRevisionDrift`, `itemViewFromVersionItem`, `vi.exams`.
- Produces:
  - `VersionItemCard({ index: number; view: ItemView; highlighted?: boolean; children?: ReactNode })` — number, type/difficulty labels, stem (clamped, click to expand `QuestionPayloadView` + explanation), drift badge when `hasRevisionDrift`, `contentUnavailable` text when `stem === null`; `children` slot for points input/buttons (used by Task 6).
  - `ExamHeader({ exam: ExamDetail; selectedVid: string; onSelectVersion: (vid: string) => void; canEditTitle: boolean })` — title + inline edit (`PUT /exams/{id}`, invalidate `["exam", id]`), subject/grade/author, version `<select>` labelled `vi.exams.versionLabel` with `v{n} — {examStatuses[status]}`, `reviewNote` of selected Rejected version.
  - `VersionReadonly({ version: ExamVersion })` — duration, total, count, items via `VersionItemCard`, short `contentHash`, reviewer/publisher.
  - Page state: `id`, `vid` from `useSearchParams`; queries `["exam", id]` and `["examVersion", vid]` (`refetchOnWindowFocus: false`, `retry: false`); 403 on version → `vi.exams.studentSheetOnly`; 404 on exam → `vi.exams.notFound`; warnings rendered as a list of `message`. Version switch: `router.replace(\`/exams/view/?id=${id}&vid=${next}\`)`, guarded by `confirmDiscard` when composer is dirty (dirty state comes from Task 6; in this task keep a `dirty` state initialised `false`). Renders `VersionReadonly` when `!actions.canEdit`; the editable branch is a placeholder filled in Task 6.

- [ ] **Step 1: Write failing tests** — missing `id` shows `missingId`; no `vid` + versions [v2 Draft, v1 Rejected] loads v2; Published version for non-author teacher → version GET 403 → `studentSheetOnly`, no `save`/`submit` buttons; read-only Approved version lists items and shows drift badge only for item with revisions 2→3; null-content item shows `contentUnavailable`; author edits title → PUT body `{ title: "Mới" }`; changing the select calls `router.replace` with new `vid`.
- [ ] **Step 2: Run** `npx vitest run src/app/exams/view/page.test.tsx` — Expected: FAIL.
- [ ] **Step 3: Implement**; drop `vi.examView` if now unused.
- [ ] **Step 4: Run** same — Expected: PASS.
- [ ] **Step 5: Commit** `feat(frontend): add exam view page with version switching`

---

### Task 6: Exam composer (editable version)

**Files:**
- Create: `src/components/exams/exam-composer.tsx`, `src/lib/exams/use-unsaved-guard.ts`
- Modify: `src/app/exams/view/page.tsx` (render composer when `canEdit`, wire `dirty`)
- Test: `src/components/exams/exam-composer.test.tsx`, add one case to `src/app/exams/view/page.test.tsx`

**Interfaces:**
- Consumes: Task 1 schema/types/points, `QuestionPicker`, `VersionItemCard`, `itemViewFromQuestion`.
- Produces:
  - `useUnsavedGuard(dirty: boolean): void` — registers `beforeunload` (preventDefault + `returnValue = ""`) while dirty.
  - `ExamComposer({ exam: ExamDetail; version: ExamVersion; meId: string; highlightIds: ReadonlySet<string>; onDirtyChange: (dirty: boolean) => void })` — `useForm<ComposerFormInput, unknown, ExamVersionUpdate>({ resolver: zodResolver(composerSchema), defaultValues: toFormValues(version) })`; `reset(toFormValues(version))` when `version.id`/`version.updatedAt` change; `useFieldArray` name `items`; two-column grid `lg:grid-cols-2` (stacked below `lg`); header row with duration input, live total `sumDecimalStrings(points)`, count; per item `VersionItemCard` + points input + ↑ ↓ ✕ (`move`/`remove`, ↑ disabled on first, ↓ on last); picker `onAdd` → `append({ sourceQuestionId: q.id, points: "1", view: itemViewFromQuestion(q) })`; `splitEvenly` → `setValue` each `items.i.points` from `splitPointsEvenly(10, n)` with `shouldDirty`; Save enabled when `isDirty` → `PUT /exam-versions/{vid}`; success: `setQueryData(["examVersion", vid], data)`, invalidate `["exam", exam.id]` and `["exams"]`, show `savedSuccess`; 400 → map `errors` keys `durationMinutes` / `items[i].points` to fields, else banner; 409 → banner `conflictReload` + `reload` button that refetches version and resets form. Calls `onDirtyChange(isDirty)` on change.

- [ ] **Step 1: Write failing tests** —
  - add from picker, move second item up, set points `"1,5"`, save → PUT body `{ durationMinutes: 45, items: [{ sourceQuestionId: "q2", points: 1.5 }, { sourceQuestionId: "q1", points: 1 }] }`;
  - split evenly with 3 items → inputs `3.33`, `3.33`, `3.34`, total text shows `10`;
  - points `"0"` → inline `validation.pointsInvalid`, no PUT;
  - item with null content shows `contentUnavailable`, can be removed, and an unchanged save keeps its id/points in the body;
  - PUT 409 → `conflictReload` and `reload` button;
  - `onDirtyChange(true)` after an edit, `false` after successful save;
  - `highlightIds` containing `q1` marks that row (e.g. `data-highlighted="true"`);
  - page test: dirty composer + version select change → `window.confirm` called with `confirmDiscard`; returning false keeps the `vid`.
- [ ] **Step 2: Run** `npx vitest run src/components/exams/exam-composer.test.tsx src/app/exams/view/page.test.tsx` — Expected: FAIL.
- [ ] **Step 3: Implement** composer, guard hook, page wiring.
- [ ] **Step 4: Run** same — Expected: PASS.
- [ ] **Step 5: Commit** `feat(frontend): add exam version composer`

---

### Task 7: Version actions (submit, withdraw, archive, new version)

**Files:**
- Create: `src/components/exams/version-actions.tsx`
- Modify: `src/app/exams/view/page.tsx` (render actions, hold `highlightIds`)
- Test: `src/components/exams/version-actions.test.tsx`

**Interfaces:**
- Consumes: `versionActions`, `getApiErrorCode`, `getApiErrorMessage`.
- Produces: `VersionActions({ exam: ExamDetail; version: ExamVersion; role: Role; meId: string; dirty: boolean; onNotApproved: (ids: string[]) => void; onVersionCreated: (vid: string) => void })`.
  - Flags from `versionActions({ status, isAuthor: exam.createdBy.id === meId, role, hasDraft: exam.versions.some(v => v.status === "Draft") })`.
  - Submit: disabled when `dirty` (shows `saveBeforeSubmit`) or `version.questionCount === 0`; `window.confirm(confirmSubmit)` → `POST /exam-versions/{vid}/submit`; 409 with `code === "QuestionsNotApproved"` → `onNotApproved(error.invalidQuestionIds)` + `notApproved` banner.
  - Withdraw: `POST …/withdraw`. Archive: `confirm(confirmArchive)` → `POST …/archive`.
  - Create version: `POST /exams/{id}/versions` body `{ fromVersionId: version.id }` → `onVersionCreated(data.id)`; 409 → `draftExists` + link to the Draft version (`/exams/view/?id=&vid=`).
  - Every success: `setQueryData(["examVersion", vid], data)` where applicable, invalidate `["exam", exam.id]`, `["exams"]`. Page passes `onVersionCreated` → `router.replace` with new `vid`; new version's `warnings` show via the normal version query.

- [ ] **Step 1: Write failing tests** — Draft with 0 questions: submit disabled; Draft dirty: submit disabled + `saveBeforeSubmit`; submit 409 `{ code: "QuestionsNotApproved", invalidQuestionIds: ["q1"] }` → `onNotApproved(["q1"])` and `notApproved` text; Submitted → only `withdraw`, posts withdraw; Approved with no Draft → `archive` + `createVersion`, create posts `{ fromVersionId: V }` and calls `onVersionCreated("V3")`; Approved while exam has a Draft → no `createVersion`; create 409 → `draftExists`; non-author head and admin → renders no buttons.
- [ ] **Step 2: Run** `npx vitest run src/components/exams/version-actions.test.tsx` — Expected: FAIL.
- [ ] **Step 3: Implement** and wire into the page (pass composer `dirty`, set `highlightIds` from `onNotApproved`, clear it on version change or successful save).
- [ ] **Step 4: Run** `npx vitest run src/components/exams src/app/exams` — Expected: PASS.
- [ ] **Step 5: Commit** `feat(frontend): add exam version workflow actions`

---

### Task 8: Docs and full verification

**Files:**
- Modify: `docs/context.md` (header version → 0.23; new "#### Chi tiết triển khai B9" after B8 in 9.2; history entry **0.23 — <date>**), `docs/tasks.md` (note under B9)

- [ ] **Step 1:** Write "Chi tiết triển khai B9" (Vietnamese, ~8 bullets): routes `/exams/`, `/exams/view/?id=&vid=`; default version rule; edit permission rule; composer limits and decimal handling ("Chia đều 10 điểm", default 1 điểm); save-only (no autosave), 409 reload; submit requires saved state, `QuestionsNotApproved` highlighting; drift warning rule; 403 → bản đề học sinh (B10); unsaved guard limitation (menu links only `beforeunload`).
- [ ] **Step 2: Run full checks** from `frontend/`: `npm run lint && npm run typecheck && npm test && npm run build && npm run api:check` — Expected: all exit 0; build output contains `out/exams/index.html` and `out/exams/view/index.html`.
- [ ] **Step 3:** Note in `docs/tasks.md` B9: implemented locally, test counts from Step 2, pending PR/CI; do not tick until merged.
- [ ] **Step 4: Commit** `docs: document B9 exam composer`

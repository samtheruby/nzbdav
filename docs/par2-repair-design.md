# PAR2-Based Repair — Design

Status: **Draft / for discussion**
Author: (planning doc)
Scope: a new "big feature" — when a health check finds an NZB has missing
articles, reconstruct the dead articles from the NZB's own PAR2 recovery
volumes (the way a normal usenet downloader would), persisting **only** the
recovered bytes rather than the whole release.

---

## 1. Goals & constraints

### What we want

* When health check detects missing/dead articles on a mounted usenet file,
  attempt a **PAR2 repair** instead of (or before) the current behaviour of
  deleting the file and asking Radarr/Sonarr to re-download the whole release.
* Repair works like a regular downloader's PAR2 pass: use the recovery volumes
  (`*.vol000+NN.par2`) that were posted alongside the content to reconstruct
  the bytes that are no longer retrievable from usenet.
* **The only persistent disk space used is the recovered ("bad") article bytes,
  plus whatever PAR2 data we choose to keep.** We never materialise the whole
  repaired file on disk — that would defeat the entire point of nzbdav.

### Hard constraints (from the product's nature)

1. **Streaming, not storage.** nzbdav mounts NZBs as a virtual filesystem.
   `DavNzbFile` is just an ordered `SegmentIds[]`; bytes are fetched and
   yEnc-decoded on demand (`NzbFileStream` → `MultiSegmentStream`). Repair must
   fit this model: stream the *present* data through the recovery math without
   writing it to disk, and persist only the reconstructed missing slices.
2. **Persistent footprint = recovered bytes (+ optional kept PAR2).** Peak
   transient memory is allowed; peak *disk* is not. This rules out any approach
   whose natural mode is "rebuild the full file on disk."
3. **Off by default, opt-in via a settings tab.** Automatic once enabled, but a
   dedicated settings section gates it (toggle + storage location + options),
   defaulting OFF.
4. **Graceful fallback.** If PAR2 repair is impossible (no par2 in the NZB, not
   enough surviving recovery blocks, recovery articles themselves dead), fall
   back to the existing repair behaviour (Arr remove-and-search / delete).

### Non-goals (initial release)

* Repairing releases that ship no PAR2 data.
* Re-posting / re-uploading recovered data anywhere.
* Repairing corruption *within* an article that still downloads (usenet
  articles are integrity-checked; the realistic failure mode is a *missing*
  article, i.e. DMCA/takedown/retention, not silent bit-rot).

---

## 2. Background: how the current pieces fit

| Concern | Where | Notes |
|---|---|---|
| Health check loop | `backend/Services/HealthCheckService.cs` | Walks `UsenetFile` DavItems, calls `CheckAllSegmentsAsync`; on `UsenetArticleNotFoundException` calls `Repair(...)`. |
| Current "repair" | `HealthCheckService.Repair(...)` | **Replace, not repair**: blocklist→delete, orphan→delete, in-library→`ArrClient.RemoveAndSearch`, else delete file+link. No PAR2. |
| PAR2 parsing (today) | `backend/Par2Recovery/` | **Parse-only.** Reads `FileDesc` packets (filename + MD5-16k) for filename deobfuscation in `GetPar2FileDescriptorsStep`. No Main/IFSC/RecoverySlice packets, no Galois-field math. |
| Posted-file models | `DavNzbFile` (`SegmentIds[]`), `DavRarFile.RarPart` (`SegmentIds[]`, `Offset`, `PartSize`, `ByteCount`), `DavMultipartFile.FilePart` (`SegmentIds[]`, `SegmentIdByteRange`, `FilePartByteRange`) | Each "posted file" is a segment list with known byte ranges. This is the unit PAR2 protects. |
| Original NZB persistence | `DavItem.NzbBlobId` → `BlobStore.ReadBlob(id)`; filename in `NzbNames`; serving in `DownloadNzbController` | **Load-bearing:** the full original NZB survives, so at repair time we can re-parse it to find the `.par2` recovery volumes and their segment IDs. |
| Blob storage | `backend/Database/BlobStore.cs` | Compressed MemoryPack blobs under `/config/blobs/{aa}/{bb}/{guid}`. Natural home for recovered bytes. Cleanup via DB triggers (see `Add-NzbBlobId-And-NzbNames` migration). |
| Streaming read path | `NzbFileStream`, `MultiSegmentStream`, plus rar/7z/multipart stream stacks | All ultimately read a posted file's bytes by fetching its segments. This is the injection point for serving recovered bytes. |
| Health UI | `frontend/app/routes/health/*`, `frontend/app/routes/settings/repairs/repairs.tsx` | Existing repair settings cover health-check scheduling + Arr replacement. New PAR2 settings = a new tab/section. |
| Config | `ConfigManager` (keys like `repair.enable`, `repair.healthcheck.*`) | New keys under `repair.par2.*`. |
| Websocket status | `WebsocketManager`, `WebsocketTopic.HealthItem*` | Reuse for repair progress/status. |

**Key takeaway:** the storage model is *ideal* for this feature, but the actual
Reed-Solomon recovery engine and a "serve recovered bytes" overlay are net-new.

---

## 3. The recovery engine decision (you asked me to decide)

PAR2 recovery is Reed-Solomon over GF(2^16). To reconstruct `m` missing input
slices you must read **every surviving input slice once** and combine it with
`m` surviving recovery slices, then solve an `m×m` linear system. Two ways to
get that engine:

### Option A — Native C# streaming RS engine (RECOMMENDED)

Implement PAR2 recovery in-process: extend the existing `Par2Recovery` parser
(Main / IFSC / RecoverySlice packets) and add a GF(2^16) Reed-Solomon solver.

* **Disk footprint:** ✅ ideal. Present input slices stream in from usenet and
  are accumulated online (multiply-add into `m` slice-sized accumulators), then
  discarded. Persistent disk = recovered slices only. No full-file
  materialisation, ever.
* **Fits architecture:** ✅ same streaming model as the rest of nzbdav.
* **No packaging concerns:** ✅ no external binary, no amd64/arm64 builds, no
  subprocess management, no temp-dir lifecycle.
* **Correctness verification built in:** ✅ each reconstructed slice's MD5 is
  checked against the IFSC packet checksums before we trust it.
* **Cost:** ❌ most code, and RS-over-GF(2^16) is easy to get subtly wrong
  (field generator, per-block Vandermonde constants, recovery-block exponents,
  endianness). Mitigated by validating against test vectors generated with
  `par2cmdline` (see Phase 0) — this is a *bounded, testable* risk.
* **Perf:** ⚠️ naïve GF multiply is slow. Acceptable for a background job
  initially (log/antilog tables); can add SIMD (`System.Runtime.Intrinsics`)
  later. Cost ≈ `surviving_input_slices × m` slice-sized GF operations.

```
NZB blob ──parse──► locate par2 recovery volumes (segment ids from stored NZB)
        │
   stream present input slices (RAM, online accumulate)
        +
   download m recovery slices (small, slice-sized)
        │
   GF(2^16) Reed-Solomon solve  ──►  reconstruct ONLY missing slices
        │
   verify each slice MD5 vs IFSC packet
        │
   persist recovered slice ranges ──► BlobStore (+ overlay metadata row)
        │
   streaming overlay serves recovered bytes when a dead segment is requested
```

### Option B — Bundle `par2cmdline` and shell out

Ship the `par2` binary in the Docker image and call it.

* **Correctness:** ✅ battle-tested.
* **Code volume:** ✅ far less of *our* code.
* **Disk footprint:** ❌ this is the dealbreaker. `par2cmdline` is file-based:
  it memory-maps input files and rebuilds *whole files* on disk. We'd have to
  (1) download **all** surviving data and write it to a temp file (zero-filling
  the holes so par2 sees a "damaged" file of the right size), (2) write the par2
  volumes, (3) repair, (4) extract just the previously-missing byte ranges,
  (5) delete everything else. **Peak disk = full release size**, transiently.
  That contradicts the spirit of the constraint and of the product.
* **Packaging:** ❌ multi-arch binary, subprocess + temp-dir management, parsing
  par2's stdout, partial-repair semantics.

### Recommendation

**Build the feature around Option A (native C# streaming engine).** It is the
only approach that actually satisfies "only persistent space used is the
recovered bytes," and it matches nzbdav's streaming design with no packaging
baggage. The cost is implementation effort and getting the field math right —
both are de-risked by a Phase 0 spike that validates the engine offline against
`par2cmdline`-generated PAR2 sets before any integration.

Choose Option B only if we later decide the engine is too costly to maintain
and we're willing to accept transient full-size temp storage during a repair.

> **Decision needed from you:** confirm Option A, or ask for a deeper spike on
> Option B's transient-storage variant before committing.

### Library survey (can we build Option A around an existing lib?)

Searched for an existing C#/.NET PAR2 **repair** library. Conclusion: **none is
usable as a dependency — Option A means writing the engine ourselves**, using the
in-repo MIT parser as the base and clean-rooming the math from the spec.

| Candidate | License | What it is | Verdict |
|---|---|---|---|
| `heksesang/Parchive.NET` | **GPL-3.0** | The *only* .NET impl with real recovery: `GaloisField.fs`, `Recovery.fs`, `RecoveryMatrix.cs`, + Main/IFSC/RecoverySlice/FileDesc packets. | ❌ Can't use. GPL-3.0 is incompatible with nzbdav's **MIT**; math is **F#** (wrong for this codebase); **abandoned since Jan 2017**, experimental (4★, 24 commits, no releases). Useful as a *structural reference only* — do **not** copy code. |
| `egbakou/reedsolomon` / `ReedSolomon` (NuGet) | MIT | Port of Backblaze JavaReedSolomon, **GF(2⁸)**. | ❌ Wrong field. PAR2 is **GF(2¹⁶)** with its own Vandermonde matrix + recovery-block exponents; this can't decode PAR2 recovery blocks. Generic RS ≠ PAR2-compatible. |
| `par2cmdline` / `par2cmdline-turbo` | **GPL-2.0** (C++/native) | Authoritative PAR2 repairer; turbo has fast GF16 (ParPar backend). | ❌ As a linked dependency. ✅ As the **spec oracle** to generate Phase-0 test vectors, and as an optional **subprocess** (see licensing note — no relicensing needed). |
| ParPar (animetosho) | (JS + native) | Fast GF16 backend; **encoder**-focused. | ❌ Not a C# repair lib. Good perf reference if we add SIMD later. |
| existing `backend/Par2Recovery/` | **MIT** (vendored, in repo) | Parse-only: reads `FileDesc` for deobfuscation. | ✅ **Our starting point** — extend with Main/IFSC/RecoverySlice parsers + the GF/RS solver. |

### Licensing: stay MIT — relicensing buys nothing here

We considered relicensing nzbdav (currently **MIT**) to GPL to unlock GPL code.
Conclusion: **don't — license choice and feature feasibility barely intersect.**

* **The most powerful external tool is already usable without relicensing.**
  Invoking `par2cmdline`/`par2cmdline-turbo` as a **separate subprocess** is
  arm's-length "mere aggregation" — it does **not** make nzbdav a derivative
  work. We could bundle the binary in the Docker image and `exec` it while
  staying MIT. So the license is *not* the blocker for Option B; the blocker is
  Option B's transient full-size disk usage (§3 Option B), which a license
  change does nothing to fix.
* **What relicensing would actually unlock is weak.** Linking/vendoring GPL
  source means either (a) vendoring `Parchive.NET` — abandoned since 2017,
  experimental, F#, not on NuGet (so we own maintenance anyway), and it only
  covers the GF/matrix **core**, which is the smallest, best-specified, most
  testable slice of the work; or (b) linking native `libpar2` — P/Invoke +
  multi-arch native builds, still file-oriented (whole-file rebuild), buying
  nothing over the subprocess.
* **No library — at any license — solves the hard part.** The risk and effort
  live in nzbdav-specific glue: streaming present slices through the math
  without materialising files, the byte-range recovery overlay in the read
  path, the data model, health-check integration, settings, and re-repair. The
  field math is the easy, fully-testable ~few-hundred lines.
* **Relicensing has a real, near-irreversible cost** (needs every contributor's
  consent to undo) and imposes copyleft on the whole downstream
  Sonarr/Radarr/Docker ecosystem — paid permanently for a marginal head-start.

**Decision:** keep **MIT**. Implement the engine **clean-room** from the PAR2
specification (GF(2¹⁶), Vandermonde recovery matrix, Gaussian elimination),
*without copying* from the GPL references (Parchive.NET, par2cmdline) — read the
spec, not their code. Validate correctness against PAR2 sets produced by
`par2cmdline` (Phase 0). par2cmdline-as-subprocess remains a fallback we can
reach for later (Option B) without any license change, if ever needed.

---

## 4. Repair model: what unit do we repair, and how do we serve it?

### The repair unit is the **posted file**, not the content file

PAR2 protects the files *as posted* — usually the `.rar`/`.r00…`/`.partNN.rar`
volumes or a raw `.mkv`, never the inner content of a rar. In nzbdav terms a
"posted file" is a segment list with a known size:

* `DavNzbFile` → the file itself.
* `DavRarFile` → **each `RarPart`** is a posted file (`SegmentIds`, `PartSize`).
* `DavMultipartFile` → **each `FilePart`** is a posted file.

A dead article is a missing yEnc segment = a contiguous **byte range of a posted
file**. We reconstruct those bytes and overlay them when that range is read. This
works uniformly for plain/rar/7z/multipart/encrypted content, because we repair
the *posted bytes* underneath the existing decode stack — the rar/7z reader
never knows the bytes were reconstructed.

### Serve by **byte range**, not by segment id

Store recovered data as **slice-aligned byte ranges of the posted file** and
overlay them at read time. This is strictly better than keying by segment id:

* PAR2 reconstructs *slices* (fixed `sliceSize`), so slice-aligned ranges are
  the natural output.
* It cleanly handles **runs of consecutive dead articles** — we reconstruct the
  whole missing span without having to split it back into individual articles.
* We avoid having to know a dead article's exact size (we can't read the yEnc
  header of an article that's gone).

### Mapping dead articles → missing slices

For a posted file we know total size and the ordered segment list. The present
segments' yEnc headers give `(PartOffset, PartSize)` for each (already used by
`NzbFileStream.SeekSegment` via `GetYencHeadersAsync`). The missing span for a
gap is `[end_of_prev_present_segment, start_of_next_present_segment)`. Convert
that span to slice indices: `firstSlice = span.Start / sliceSize`,
`lastSlice = (span.End - 1) / sliceSize`. Those are the input slices we must
reconstruct. (For rar parts / multipart parts the part already carries its
offset within the NZB file, so spans compose directly.)

Edge: if a posted file's **first or last** article is missing we can't bound the
gap from a neighbour on one side — use the posted file's known total size (NZB
`FileDesc.FileLength` / stored part size) as the boundary.

---

## 5. Data model & migrations

### 5.1 New: recovery overlay attached to a DavItem

Reuse the existing "sidecar MemoryPackable keyed by DavItem.Id" pattern
(`DavNzbFile`/`DavRarFile`/`DavMultipartFile`).

```csharp
// backend/Database/Models/DavFileRecovery.cs
[MemoryPackable(GenerateType.VersionTolerant)]
public partial class DavFileRecovery
{
    [MemoryPackOrder(0)] public Guid Id { get; set; }          // FK to DavItem.Id
    [MemoryPackOrder(1)] public Guid RecoveryBlobId { get; set; } // blob holding recovered bytes
    [MemoryPackOrder(2)] public RecoveredRange[] Ranges { get; set; } = [];
    [MemoryPackOrder(3)] public DateTimeOffset RepairedAt { get; set; }

    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class RecoveredRange
    {
        // byte range within the *posted file* this recovery covers
        [MemoryPackOrder(0)] public long FileOffset { get; set; }
        [MemoryPackOrder(1)] public long Length { get; set; }
        // where those bytes live inside RecoveryBlobId
        [MemoryPackOrder(2)] public long BlobOffset { get; set; }
        // which posted-file this applies to (0 for DavNzbFile; part index for rar/multipart)
        [MemoryPackOrder(3)] public int PartIndex { get; set; }
    }
}
```

* Recovered bytes go to a **single blob** (`RecoveryBlobId`) — a concatenation
  of recovered slice ranges; `Ranges` is the index into it. This keeps the DB
  row tiny and the bytes in the existing blob store.
* `PartIndex` lets one overlay cover all rar/multipart parts of one DavItem.

### 5.2 DavItem column

Add `RecoveryBlobId Guid?` to `DavItem` (mirrors `FileBlobId`/`NzbBlobId`).
Lets the stream-construction path cheaply know "this item has an overlay" and
gives the cleanup triggers something to target. (Alternatively, look up
`DavFileRecovery` by id — but a nullable column avoids a join on the hot read
path. Recommended: the column.)

### 5.3 Blob cleanup

Follow the existing trigger pattern from `Add-NzbBlobId-And-NzbNames`:

```sql
CREATE TRIGGER TR_DavItems_Delete_AddRecoveryBlobCleanup
AFTER DELETE ON DavItems
WHEN OLD.RecoveryBlobId IS NOT NULL
BEGIN
    INSERT OR IGNORE INTO BlobCleanupItems (Id) VALUES (OLD.RecoveryBlobId);
END
```

So when a repaired item is deleted/replaced, its recovered blob is GC'd by the
existing `BlobCleanupService`.

### 5.4 Migrations

* `Add-DavFileRecovery-Table` (+ the new table / model registration in
  `DavDatabaseContext`).
* `Add-RecoveryBlobId-To-DavItems-Table` (column + cleanup trigger).
* Config defaults seeded the way other `repair.*` keys are
  (e.g. see `Populate-Health-Check-Categories-Setting`).

---

## 6. Repair pipeline (stages)

New service: `backend/Services/Par2RepairService.cs` (invoked from
`HealthCheckService.Repair` when enabled, and via a manual API). One repair
operation, given a `DavItem`:

1. **Gate.** Repair enabled? Item type repairable (`UsenetFile` with an
   NzbBlob)? Storage budget not exceeded? If not → return `NotAttempted`.
2. **Load NZB.** `BlobStore.ReadBlob(davItem.NzbBlobId)` → `NzbDocument.LoadAsync`.
   If absent → `NotAttempted` (fallback to existing behaviour).
3. **Locate PAR2.** In the NZB, find par2 files (magic bytes in first 16KB, as
   `GetPar2FileDescriptorsStep` already does). Read the **index** + **recovery
   volumes**. Parse Main (slice size, recovery-set file IDs + order), all
   `FileDesc`, all IFSC (per-slice MD5/CRC), and the available RecoverySlice
   packets (each carries its exponent/constant). *(New packet parsers required —
   see §7.)*
4. **Identify damage.** For each posted file in the item, determine the missing
   byte span(s) from present-neighbour yEnc headers (§4). Map to global input
   slice indices. Build the set `M` of missing input slices.
5. **Feasibility check.**
   * Count surviving recovery slices `R` (the recovery articles must themselves
     be retrievable — `STAT`/header-check them).
   * If `|M| > R` → **infeasible** → return `Infeasible` (fallback).
6. **Reconstruct (streaming).**
   * Pick `|M|` surviving recovery slices; download them (slice-sized).
   * Allocate `|M|` slice-sized accumulators.
   * Stream **every surviving input slice** of the recovery set once
     (from usenet, yEnc-decoded, online) and GF-multiply-add its contribution.
     Present input bytes are never written to disk.
   * Solve the `|M|×|M|` system (Gauss-Jordan over GF(2^16)) → missing slices.
7. **Verify.** MD5 each reconstructed slice against its IFSC checksum. Any
   mismatch → abort, return `Failed` (do not persist partial/garbage).
8. **Extract & persist.** From reconstructed slices, cut exactly the missing
   byte spans (trim slice padding / file tail), concatenate into one
   `RecoveryBlobId` blob, write `DavFileRecovery` + set `DavItem.RecoveryBlobId`.
9. **Mark.** Write a `HealthCheckResult` with a new `RepairAction.Par2Repaired`
   and a websocket status update. Reset `HealthCheckFailureCount`, advance
   `NextHealthCheck`.

Return value enum: `Par2RepairOutcome { NotAttempted, Repaired, Infeasible, Failed }`.
`HealthCheckService` falls back to its current logic for everything except
`Repaired`.

### Re-repair (articles die after a prior repair)

On a later health check, treat **already-recovered ranges as present** (they're
served from the overlay). Recompute `M` only for newly-dead, non-overlaid spans,
and merge new recovered ranges into the existing `DavFileRecovery`. Recovery
math is identical; the overlay grows.

---

## 7. PAR2 parser additions

Today only `FileDesc` is parsed. Add packet types (all share `Par2PacketHeader`,
which already exposes `PacketLength`, `PacketHash`, `RecoverySetID`, `PacketType`):

| Packet | `PacketType` | Why we need it |
|---|---|---|
| Main | `PAR 2.0\0Main\0\0\0\0` | Slice size; ordered list of recovery-set file IDs (defines global slice numbering). |
| Input File Slice Checksum (IFSC) | `PAR 2.0\0IFSC\0\0\0\0` | Per-slice MD5 + CRC32 → verify reconstructed slices. |
| Recovery Slice | `PAR 2.0\0RecvSlic` | The recovery data + its exponent (matrix column). |
| File Description | `PAR 2.0\0FileDesc` | (exists) filename, length, MD5-16k → match input files. |

Plus a `GaloisField16` + `ReedSolomon` recovery solver. Authority for constants
and matrix construction: the PAR2 spec and `par2cmdline`
(`galois.cpp`, `reedsolomon.cpp`) — GF(2^16), generator `0x1100B`,
Vandermonde-style matrix, recovery-block exponents from the RecoverySlice
packets. **Validate against `par2cmdline`-generated vectors (Phase 0).**

---

## 8. Streaming injection point (serving recovered bytes)

Goal: when any read touches a recovered range of a posted file, serve those
bytes from the overlay; otherwise fetch the article as today. Must work for
plain, rar, 7z, and multipart because it sits at the **posted-file read layer**,
below the rar/7z decoders.

Approach: a `RecoveryOverlayStream` decorator (or an optional overlay parameter
threaded into the posted-file stream construction):

* Inputs: the posted file's segment stream (existing `MultiSegmentStream` /
  `NzbFileStream`) + the `RecoveredRange[]` for that posted file (+ its blob).
* On `ReadAsync` at file offset `p`:
  * If `p` falls in a recovered range → read from `RecoveryBlobId` at the
    mapped `BlobOffset`.
  * Else → read from the live segment stream.
  * Stitch across boundaries.
* Because reads are sequential within a segment stream, the overlay can be a
  thin position-aware switch; random access already re-seeks per request.

Wiring:

* `DatabaseStoreNzbFile` / `DatabaseStoreRarFile` / `DatabaseStoreMultipartFile`
  (and the `MultiSegmentStream`/`DavMultipartFileStream` builders) gain an
  optional overlay sourced from `DavFileRecovery`.
* A dead segment that is **fully covered** by the overlay no longer throws
  `UsenetArticleNotFoundException` — the overlay short-circuits the fetch.
* Partial coverage (shouldn't happen if we slice-align correctly, but defensive)
  → recovered bytes for the covered part, live fetch for the rest.

This is also what makes a repaired file *actually playable* again: Plex/Jellyfin
streaming hits the dead range, the overlay supplies reconstructed bytes, playback
continues.

---

## 9. UI / API / config

### 9.1 New settings tab — "PAR2 Repair" (default OFF)

A new section/tab in `frontend/app/routes/settings/` (sibling to the existing
`repairs/repairs.tsx`). Config keys under `repair.par2.*`:

| Key | UI | Default |
|---|---|---|
| `repair.par2.enable` | Enable PAR2 repair (master toggle) | `false` |
| `repair.par2.storage-path` | Storage location for recovered data | `/config/blobs` (i.e. default blob store) |
| `repair.par2.max-storage-bytes` | Cap on total recovered-data on disk (0 = unlimited) | e.g. `0` |
| `repair.par2.fallback` | When repair impossible: `arr-research` \| `mark-only` \| `delete` | `arr-research` (current behaviour) |
| `repair.par2.keep-par2` | Keep downloaded recovery slices for faster future re-repair | `false` |
| `repair.par2.max-concurrent` | Max simultaneous repair jobs | `1` |

`ConfigManager` getters mirror existing style: `IsPar2RepairEnabled()`,
`GetPar2StoragePath()`, `GetPar2MaxStorageBytes()`, `GetPar2Fallback()`, etc.
Seed defaults via a `Populate-Par2-Repair-Settings` migration.

> Note: the existing **Repairs** settings page governs health-check scheduling +
> Arr replacement. PAR2 repair is a *strategy that runs first* when a missing
> article is found. Keep them as separate tabs; the PAR2 toggle does nothing
> unless background health checks / repairs are also on.

### 9.2 Health page

* Show a `Par2Repaired` status (new badge) distinct from `Repaired`
  (Arr re-search) and `Deleted`.
* Surface recovered-storage usage (sum of `DavFileRecovery` blob sizes), since
  this is the one place the feature consumes disk.
* (Optional, later) a manual **Repair**/**Retry** button per item that calls a
  new endpoint to run `Par2RepairService` on demand — useful for testing and
  for items that previously hit `Infeasible`.

### 9.3 API / websocket

* Reuse `WebsocketTopic.HealthItemProgress` / `HealthItemStatus` for repair
  progress (download + reconstruct phases).
* Extend `HealthCheckResult.RepairAction` enum with `Par2Repaired = 4`
  (append-only; existing numeric values must not change).
* (Optional) `POST /api/repair-item` for manual trigger.

---

## 10. Edge cases & fallbacks

| Case | Handling |
|---|---|
| NZB has no PAR2 | `NotAttempted` → existing fallback (Arr / delete). |
| Not enough surviving recovery blocks (`|M| > R`) | `Infeasible` → fallback. |
| Recovery (par2) articles themselves dead | Counted in feasibility; reduces `R`. |
| Obfuscated par2 filenames | Already handled — par2 located by magic bytes in first 16KB. |
| First/last article of a posted file missing | Bound the gap with the known file/part size, not a neighbour. |
| Consecutive dead articles | Naturally handled — reconstruct the whole missing span (byte-range keying). |
| Reconstructed slice fails IFSC MD5 | Abort, `Failed`, persist nothing. |
| Encrypted / password-protected rar | Works — we repair posted bytes beneath the decoder. |
| Re-repair after more loss | Treat overlaid ranges as present; merge new ranges (§6). |
| Storage budget exceeded | `NotAttempted`; surface in UI; respect `max-storage-bytes`. |
| Slice size very large / huge `|M|` | Memory ≈ `|M| × sliceSize`; cap via feasibility + `max-concurrent`; consider chunked column processing later. |
| Original NZB blob missing (`/blobs` tampered) | `NotAttempted` → fallback. |
| Multiple recovery sets in one NZB | Match each posted file to its recovery set via Main/FileDesc; repair per set. |
| Item deleted mid-repair | Repair runs on its own DB context (like health checks); discard on cancel. |

---

## 11. Phased delivery plan

**Phase 0 — Engine spike (offline, no integration).**
Extend `Par2Recovery` with Main/IFSC/RecoverySlice parsers + `GaloisField16` +
RS solver. Unit-test against PAR2 sets generated by `par2cmdline`: delete known
blocks, confirm byte-exact reconstruction and IFSC verification. *Exit criteria:
we can recover arbitrary missing slices from real par2 sets in a test.*

**Phase 1 — Repair pipeline (headless).**
`Par2RepairService` end-to-end against a real mounted item via an internal/test
endpoint: load NZB → locate par2 → identify damage → stream-reconstruct →
verify → persist `DavFileRecovery` + blob. New migrations. No auto-trigger, no
serving yet. *Exit: a damaged item produces a verified recovery blob.*

**Phase 2 — Streaming overlay.**
`RecoveryOverlayStream` + wiring into NzbFile/RarFile/Multipart read paths; dead
segments covered by the overlay stop throwing. *Exit: a repaired file streams
end-to-end through a previously-dead range (incl. inside a rar).*

**Phase 3 — Settings tab + config (default OFF).**
New PAR2 Repair settings tab, `repair.par2.*` keys, `ConfigManager` getters,
defaults migration.

**Phase 4 — Auto-trigger + health UI.**
Hook `Par2RepairService` into `HealthCheckService.Repair` (attempt first, fall
back on non-`Repaired`). `Par2Repaired` status/badge, recovered-storage usage,
blob-cleanup trigger. Optional manual repair button.

**Phase 5 — Hardening.**
Concurrency limits, storage-budget enforcement, re-repair merging, GF SIMD perf,
metrics/logging, docs (`docs/setup-guide.md`).

---

## 12. Open questions for you

1. **Engine:** confirm **Option A (native C# engine)**? (My recommendation.)
2. **Fallback default** when PAR2 can't repair: keep today's behaviour
   (Arr remove-and-search) as the default `repair.par2.fallback`? (Assumed yes.)
3. **Keep PAR2 data after repair?** Default OFF (re-download recovery slices if
   ever needed again) vs. ON (faster re-repair, more disk). (Assumed OFF.)
4. **Manual repair button** in scope for v1, or automatic-only first?
5. **Storage budget**: hard cap (`max-storage-bytes`) in v1, or ship unlimited
   + usage display first?
```

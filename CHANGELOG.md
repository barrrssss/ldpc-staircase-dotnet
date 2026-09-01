# Changelog

## Unreleased

- Adopted specification **0.1.2**. It adds production-scale vectors at `K = 16000`,
  `R = 8192`, `N1 = 7` — one matrix, one encode, and two decode cases. This implementation
  verified no shared vector above `K = 250` before; it now reproduces the matrix
  serialization, the repair bytes, and both decode outcomes at the top of the calibrated
  envelope, including a residual solve thousands of unknowns wide. No library change was
  needed: the vectors passed on the first run.
- `ConformanceTests` reads the `missingBlob` form of a decode case's missing list and
  expects `manifestVersion` 2. A case must carry exactly one of `missing` / `missingBlob`;
  a reader that silently skipped the field it did not understand would report a pass on a
  decode it never ran.

- Adopted specification **0.1.1**. It adds three matrix vectors and one encode vector that
  exercise `SPEC.md` section 4.3, the low-degree-raising pass that no committed vector
  previously caused to add a single entry. Every 0.1.0 vector is byte-identical at 0.1.1,
  so this implementation passes unchanged; the new cases simply cover ground that was
  uncovered.
- Adopted the section 6 correction: decoding recovers and reports every symbol the
  received set determines, with completion still judged on the source symbols alone.
  Section 6 had said reconstructing missing repair symbols was not required, which
  contradicted `dec-all-repair-missing`. This implementation already did the right thing.

- Tracked the specification's extended differential corpus. The 2026-09-01 independence
  review found that `SPEC.md` section 4.3, which raises rows of left degree below two,
  added no entry in any committed vector or any corpus case — so two implementations could
  have disagreed about it entirely without any gate noticing. Five corpus cases now
  exercise both of its branches, and this implementation agrees with the independent
  reference checker on all 36 cases.
- Adopted the `SPEC.md` section 2 clarification that `N1 >= 1` is required always, not only
  when `R >= 1`. This implementation already enforced it; the specification now states it.
  No behavior changed.

- Added `ReferenceCorpusTests`, the independent-oracle gate of specification decision
  0002. It runs the differential corpus committed at
  `external/ldpc-staircase-spec/reference/corpus.json` through this codec and compares
  the matrix serialization, encoded symbols, completion flag, recovered symbol count,
  recovered source and repair index lists, still-missing source indices, whether peeling
  alone sufficed, and the post-decode source bytes against the answers of the
  specification repository's dependency-free Python checker. All 31 corpus cases agree.
  Test-only; nothing here changes the library, its API, or its dependencies.
- Adopted the `SPEC.md` section 6 clarification that decoding requires at least one
  present symbol. This implementation already enforced it; the specification now states
  it. No behavior changed.

## 0.1.2 — 2026-08-23

- Added publication-ready NuGet metadata, package README/license/notice content,
  XML documentation output, and symbol packages.
- Aligned the NuGet package version with the Java 0.1.2 implementation release line.
- No codec behavior, public API, specification, or conformance vector changed.

## 0.1.0 — 2026-07-23

- Initial .NET implementation of the constrained RFC 5170 LDPC-Staircase profile.
- Targets `netstandard2.0` and `net8.0` with byte-exact conformance on both runtime
  paths.
- Declared the package version explicitly on 2026-08-04; no codec implementation or
  public API changed.

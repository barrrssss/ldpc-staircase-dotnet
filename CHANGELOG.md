# Changelog

## 0.1.0 — 2026-07-23

- Initial .NET implementation of the constrained RFC 5170 LDPC-Staircase profile.
- Targets `netstandard2.0` and `net8.0` with byte-exact conformance on both runtime
  paths.
- Declared the package version explicitly on 2026-08-04; no codec implementation or
  public API changed.

The Java residual-solver optimization released as Java 0.1.1 is not present here, so
this package correctly remains at 0.1.0 rather than receiving a lockstep version bump.

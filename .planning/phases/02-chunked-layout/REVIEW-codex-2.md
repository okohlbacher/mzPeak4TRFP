P1: RESOLVED - the plan now says the reference chunks chromatograms and Phase 2 keeps them point as an explicit deviation, with the old false claim called out as wrong. [02-01-PLAN.md](/Users/kohlbach/Claude/mzPeak4TRFR/.planning/phases/02-chunked-layout/02-01-PLAN.md:61)

P2: RESOLVED - the plan requires bitwise multiset keys, forbids decimal rounding, and defines the empirical bit-exact vs bounded-tolerance decision procedure. [02-01-PLAN.md](/Users/kohlbach/Claude/mzPeak4TRFR/.planning/phases/02-chunked-layout/02-01-PLAN.md:273)

P3: RESOLVED - the plan adds a footer-parse lock for prefix, buffer formats, transform CURIEs, sorting rank, and `--point` retaining the v1 point index. [02-01-PLAN.md](/Users/kohlbach/Claude/mzPeak4TRFR/.planning/phases/02-chunked-layout/02-01-PLAN.md:264)

P4: RESOLVED - the plan defines and tests empty, all-equal, non-monotonic, singleton, and reference null-decode cases. [02-01-PLAN.md](/Users/kohlbach/Claude/mzPeak4TRFR/.planning/phases/02-chunked-layout/02-01-PLAN.md:157)

NEW issues:
- Minor wording conflict: the plan says “never emit a length-1 chunk” but also says a single-point spectrum yields one chunk; clarify the no-length-1 rule excludes the whole-spectrum `k==1` case. [02-01-PLAN.md](/Users/kohlbach/Claude/mzPeak4TRFR/.planning/phases/02-chunked-layout/02-01-PLAN.md:155), [02-01-PLAN.md](/Users/kohlbach/Claude/mzPeak4TRFR/.planning/phases/02-chunked-layout/02-01-PLAN.md:171)

VERDICT: SHIP-WITH-FIXES

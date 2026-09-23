# Agent notes for pins-guider

* Read `docs/DESIGN.md` first; it is the agreed v1 design. Don't change agreed behaviour silently.
* Build/test only via `./build.sh` (dotnet SDK runs in docker). `./build.sh` = `dotnet test`.
  Warnings are errors.
* Engine code (`src/PinsGuider.Engine`) must not reference NINA, WPF, or any IO/UI framework.
  Time via `IClock`, hardware via `ICameraSource` / `IPulseOutput` / `IMountState`.
* Files marked `API CONTRACT` define shared signatures: add members freely, don't change or remove
  existing ones without coordinating.
* Headers: original files start with `// SPDX-License-Identifier: MPL-2.0`. Files ported from PHD2
  start with `// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause`, followed by the original PHD2
  copyright lines and `// Ported from PHD2 src/<file> (a6c02722)`. Never copy KStars (GPL) code.
* Port PHD2 behaviour faithfully (same constants, same order of checks); document deliberate
  deviations with a `// Deviation from PHD2:` comment.
* Tests: NUnit 4 + FluentAssertions 7. Prefer synthetic star fields with known sub-pixel truth.
* Style: file-scoped namespaces, 4-space indent, `PascalCase` public members, fields `camelCase`
  without underscore prefix, XML doc comments on public API.

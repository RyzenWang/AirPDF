# Security policy

## Supported versions

AirPDF is in early preview. Security fixes are applied to the latest `main`
branch and the most recent tagged preview release.

| Version | Supported |
| --- | --- |
| 0.1.x | Yes |
| Older | No |

## Reporting a vulnerability

Please use GitHub's **Private vulnerability reporting** on the repository's
Security tab. Do not open a public issue for a vulnerability.

Include:

- the affected version or commit,
- the platform and runtime,
- a description of the impact,
- reproduction steps or a minimal PDF, if it is safe to share.

You can expect an acknowledgement within a few days. Confirmed issues will be
fixed on `main` and noted in the release changelog. Credit is offered unless you
prefer to remain anonymous.

## Scope

In scope:

- memory-safety issues in native PDFium interop,
- path traversal or unintended file access,
- unsafe deserialization of settings or documents,
- denial of service caused by a crafted PDF that can be avoided in AirPDF code.

Out of scope:

- vulnerabilities in PDFium itself that are not reachable through AirPDF (please
  also report those upstream),
- social engineering,
- issues that require a compromised local account or an already-malicious
  executable.

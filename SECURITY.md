# Security policy

## Current status

ContainerToDrive is pre-release. A successful unit test or package build is not a security certification, a filesystem durability guarantee, or approval to use production/customer containers. No supported production release or response-time commitment is declared here.

## Reporting

Do **not** put SAS URLs, account keys, authorization headers, private keys, customer names/contents, personal paths, or unredacted logs into issues, pull requests, screenshots, or CI artifacts.

Use [GitHub private vulnerability reporting](https://github.com/zmustafa/ContainerToDrive/security/advisories/new), available under **Security > Report a vulnerability**. Maintainers must enable this feature before making the repository public. This policy does not claim that the remote setting has already been enabled.

If private reporting is unavailable, do not post vulnerability details publicly. A public issue may ask only for a private reporting route, without reproduction steps, sensitive data, or exploit details. No security email address or response-time guarantee is offered.

Provide a synthetic reproduction, affected source revision/dependency versions, Windows version, expected/observed behavior, and a minimal redacted impact description. Do not test against third-party/customer resources without explicit authorization. If a real credential was exposed, revoke or rotate it through its issuing system; deleting a message does not revoke a SAS.

## Boundaries and safe handling

- The normal application is intended to run as a standard Windows user; administrator/same-user malware is outside the local secrecy boundary.
- Never place SAS values in build/launch arguments or environment-wide settings. Unit fixtures contain only deliberately invalid synthetic signatures and make no Azure requests.
- Default test scripts do not install WinFsp, mount drives, provision Azure, or run cloud tests. NuGet restore and dependency bootstrap may use their declared public download origins.
- Downloads require reviewed pinned SHA-256 values. Checksums supplied by the same download server are an additional consistency check, not an independent trust anchor.
- Do not disable TLS validation, driver-signature checks, or endpoint restrictions to make a test pass.
- Development data and uncertain VFS recovery state are not build output. Do not manually purge a dirty/unknown cache.
- Signing uses an explicitly selected external certificate store/private-key facility. Never commit or upload PFX files, signing passwords, or private keys.
- Untrusted pull requests must not access signing/cloud credentials or persistent privileged runners. Ordinary CI performs local unit/build checks only.

Review the security and recovery gates before claiming production readiness. Diagnostics require human review; redaction is defense in depth, not permission to log arbitrary request bodies.

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos. [WinFsp repository](https://github.com/winfsp/winfsp).
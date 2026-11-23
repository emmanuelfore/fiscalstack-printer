# Security policy

PrintInterceptor runs on tills, handles FiscalStack credentials and decodes print
jobs that originate in a POS. Treat configuration, secret blobs, fiscal state and
captured jobs as sensitive.

## Reporting a vulnerability

Report suspected vulnerabilities privately through this repository's **Security →
Report a vulnerability** (GitHub private vulnerability reporting). Do not open a
public issue with an exploit, a real API key, a device private key or a captured
receipt. Include the release version, Windows version, component affected and a
minimal reproduction with secrets redacted.

## Secret handling

- Register the FiscalStack API key with `PrintInterceptor.exe --register-key`
  (hidden prompt) or the console's **Settings → API key**. The key is never passed on
  a command line or stored in `config.json`.
- The API key and (when offline signing is enabled) the ZIMRA device private key
  are `LocalMachine` DPAPI blobs under `<config folder>\secrets\`, each with an
  ACL limited to SYSTEM, Administrators and the till account. DPAPI protects
  copied files; the ACL protects local readers of the machine DPAPI master key.
- The installer restricts the root, config and data folders before writing any
  file, and `Save()` fails closed if an ACL cannot be applied. Do not loosen
  those ACLs to work around a permission error.
- `PRINTINTERCEPTOR_API_KEY` and `install.ps1 -ApiKey` are deprecated legacy
  fallbacks: the registry-readable variable can be read by any local account and
  leaks into imaging scripts. Remove the variable after registering the key.
- Rotate by generating a new key in FiscalStack, running `--register-key` again,
  restarting the service or tray, then revoking the old key. `--clear-key`
  removes the local copy. A restored image must be re-registered because DPAPI
  blobs are machine-bound.
- Logs and UI show only a coarse length bucket for a configured key — never a
  prefix or exact length. Do not attach raw secrets or unredacted logs to a
  public report.

## Offline signing

The ZIMRA private key is accepted only from the DPAPI secret store. The legacy
plaintext key in `data\state\offline-provisioning.json` is stripped on startup
and never used for signing. If the store is missing or cannot be written,
offline signing refuses to start with an actionable message; do not re-enable
plaintext caching as a workaround.

## Print-job decoding

Ghostscript parses untrusted PDFs/PostScript while the service may run elevated.
The bundled binary is preferred. A configured `ghostscriptPath` must resolve
(including symlinks/junctions) inside the application folder, Program Files, or
a trusted OS location on Linux; user-profile paths and arbitrary `PATH` entries
are rejected. Keep the Ghostscript install directory writable only by
administrators.

## Installation and updates

Run the installer from an authenticated release package and keep automatic
updates enabled for the OS and .NET runtime. Verify that switching between
Service and Tray modes leaves no stale explicit ACE for the previous account.
Report install or ACL failures instead of continuing with a manually relaxed
configuration.

# Release signing setup

This document is a provisioning checklist for the repo owner. It does **not**
get executed automatically — nothing in `.github/workflows/release.yml` will
purchase, request, or configure any certificate on your behalf. Until the
GitHub secrets listed below are set, every signing/notarization step in the
release workflow is skipped via `if: env.<SECRET_NAME> != ''` guards, and
releases are produced **exactly as they are today**:

- Windows EXEs/installers: unsigned.
- macOS app/DMG: ad-hoc signed (`codesign --sign -`), not notarized. Users see
  an "unidentified developer" Gatekeeper prompt on first launch.
- Linux: unaffected either way (no code-signing concept for .deb/AppImage).

Nothing below is time-sensitive or blocking — set it up whenever you're ready
to ship signed/notarized releases.

---

## 1. Windows — Authenticode code signing

Pick one:

### Option A — OV (Organization Validation) code signing certificate

- Cost: roughly **$70–$200/year** from a budget CA (e.g. SSL.com, Certera,
  Sectigo resellers). Shop around — pricing varies a lot between resellers of
  the same underlying CA.
- Delivered as a `.pfx`/`.p12` file (private key + cert) protected by a
  password you choose at export/generation time.
- **SmartScreen caveat:** OV certs do **not** get instant Windows SmartScreen
  reputation. A newly-signed OV binary can still trigger a SmartScreen
  "Windows protected your PC" warning until it accumulates enough
  download/execution reputation across users. This can take days to weeks.
  EV (Extended Validation) certificates used to bypass this via instant
  reputation, but EV code-signing certs on physical/cloud HSMs are pricier,
  more cumbersome to provision (identity vetting, hardware token or cloud HSM
  enrollment), and increasingly being phased out by some CAs in favor of
  Trusted Signing (Option B). For a personal/OSS project, OV + patience (or
  Option B) is the practical choice.

### Option B — Azure Trusted Signing

- Cost: roughly **$9.99/month** (Basic tier, as of writing — verify current
  pricing on the Azure Trusted Signing page before committing).
- Cloud-hosted signing identity — no `.pfx` file to manage; signing happens
  via `signtool` talking to the Azure Trusted Signing endpoint using a
  short-lived Azure AD token. **This is a materially different integration
  than the PFX-based flow implemented in `release.yml` today** (it uses the
  `AzureSignTool` / Trusted Signing `signtool` plugin + Azure credentials,
  not `signtool sign /f cert.pfx /p password`). Requires identity
  verification through Azure (individual or organization).
- Same general SmartScreen reputation-building caveat applies as OV, though
  Microsoft has indicated Trusted Signing certs may build reputation faster
  since they're issued through a Microsoft-operated CA. Don't take this as a
  guarantee — verify current Microsoft documentation before deciding.
- **If you choose this option, the Windows signing steps in `release.yml`
  will need to be rewritten** to use the Trusted Signing signtool plugin
  instead of `/f <pfx> /p <password>`. The secret names below
  (`WINDOWS_CERT_PFX_BASE64` / `WINDOWS_CERT_PASSWORD`) are for the
  PFX-based (Option A) flow that's wired up right now.

### What the workflow expects today (Option A / PFX-based)

| Secret name | Format |
|---|---|
| `WINDOWS_CERT_PFX_BASE64` | Base64-encoded bytes of the `.pfx` file. |
| `WINDOWS_CERT_PASSWORD` | The plaintext password used to export/protect the `.pfx`. |

To produce the base64 value:

```bash
# macOS/Linux
base64 -i cert.pfx | tr -d '\n' > cert.pfx.b64
```

```powershell
# Windows PowerShell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx")) | Set-Clipboard
```

Paste the resulting string as the `WINDOWS_CERT_PFX_BASE64` secret value.

The workflow signs (SHA-256 digest, RFC3161 timestamp via
`http://timestamp.digicert.com`):

- `NexusMonitor.exe` (both win-x64 and win-arm64 publish outputs)
- `nexus.exe` (CLI, both win-x64 and win-arm64 publish outputs)
- The final Inno Setup installer EXEs (both architectures), after `iscc` runs

---

## 2. macOS — Developer ID signing + notarization

1. Enroll in the **Apple Developer Program** — **$99/year** —
   at https://developer.apple.com/programs/.
2. In Xcode (Settings → Accounts → Manage Certificates) or the Apple
   Developer portal (Certificates, Identifiers & Profiles → Certificates),
   create a **"Developer ID Application"** certificate. This is the
   certificate type required for signing software distributed outside the
   Mac App Store.
3. Export the certificate **and its private key** as a `.p12` file (Keychain
   Access → select both the cert and key → right-click → Export 2 items…),
   protected by a password you choose.
4. Generate an **app-specific password** for `notarytool` at
   https://appleid.apple.com/ (Sign-In and Security → App-Specific
   Passwords). **Do not use your main Apple ID password** — notarytool
   authentication requires an app-specific password.
5. Note your **Team ID** (Apple Developer portal → Membership details, or
   `xcrun altool --list-providers` if you have multiple teams) — a
   10-character alphanumeric string.

### What the workflow expects

| Secret name | Format |
|---|---|
| `MACOS_CERT_P12_BASE64` | Base64-encoded bytes of the `.p12` export (cert + private key). |
| `MACOS_CERT_PASSWORD` | The password used to protect the `.p12` export. |
| `APPLE_TEAM_ID` | Your 10-character Apple Developer Team ID. |
| `APPLE_ID` | The Apple ID email address for the developer account. |
| `APPLE_APP_PASSWORD` | An app-specific password generated at appleid.apple.com (NOT your Apple ID password). |

To produce the base64 value:

```bash
base64 -i DeveloperIDApplication.p12 | tr -d '\n' > cert.p12.b64
```

With all five secrets set, the workflow will:

1. Import the `.p12` into a throwaway keychain on the runner and resolve the
   `Developer ID Application: ...` identity from it.
2. Let `installer/macos/create-app-bundle.sh` sign the `.app` with that
   identity (hardened runtime + entitlements — already implemented in the
   script's "real identity" branch).
3. Submit the resulting `.dmg` to `xcrun notarytool submit --wait`.
4. Staple the notarization ticket with `xcrun stapler staple`.
5. Delete the throwaway keychain (always runs, even on failure).

If any of the five secrets is missing, none of the above runs — the DMG is
ad-hoc signed only, same as today.

---

## 3. Adding the secrets to GitHub

Repo → **Settings → Secrets and variables → Actions → New repository
secret**. Add each of the following (exact names, case-sensitive):

- `WINDOWS_CERT_PFX_BASE64`
- `WINDOWS_CERT_PASSWORD`
- `MACOS_CERT_P12_BASE64`
- `MACOS_CERT_PASSWORD`
- `APPLE_TEAM_ID`
- `APPLE_ID`
- `APPLE_APP_PASSWORD`

Windows and macOS signing are independent — you can configure one, both, or
neither. Each platform's guarded steps check only that platform's secrets.

---

## 4. Current behavior (no secrets set)

This is the state of the repo as of this writing, and will remain the state
until you deliberately add the secrets above:

- No certificates have been purchased or requested by this change.
- No workflow step will fail due to missing secrets — every signing step is
  `if: env.<SECRET_NAME> != ''` guarded and simply skips.
- Windows artifacts: unsigned EXEs and installers, identical to before this
  change.
- macOS artifacts: ad-hoc signed `.app`/`.dmg`, not notarized, identical to
  before this change.
- Linux artifacts: unaffected (no signing concept applies).

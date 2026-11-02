# Security

## Reporting a problem

If you find a security problem, please report it **privately**: open this repository's **Security** tab and
click **Report a vulnerability**. Please don't open a public issue for it.

You'll get an answer as soon as possible, and credit in the changelog if you'd like.

## How to check a download

Every release is built by GitHub Actions from the code in this repository, in public. You can check that
the file you downloaded is exactly that build:

1. **Fingerprint**: in PowerShell, `Get-FileHash .\LiteClip-1.2.zip` must match the SHA-256 on the release page.
2. **Attestation** (proof it was built here): with the GitHub CLI,
   `gh attestation verify LiteClip-1.2.zip --repo gurtspec/LiteClip`
3. **Build it yourself**: `build.ps1` builds the same app from source with Windows' own compiler.

## Supported versions

Only the newest release gets fixes.

# Security Policy

Cavdeck is a Windows desktop client for S3-compatible object storage. Reports involving storage credentials, user data, destructive operations, or the software update chain are especially important.

## Supported versions

Supported binary versions are identified in [GitHub Releases](https://github.com/imnbwd/Cavdeck/releases). If no release is listed, there is no supported installer version; reports affecting the source code are still welcome. Do not assume older builds receive security updates.

## Report a vulnerability privately

**Do not open a public GitHub Issue, Pull Request, or Discussion with exploit details, access credentials, or sensitive data.**

Use GitHub's [private vulnerability reporting](https://github.com/imnbwd/Cavdeck/security/advisories/new) (Security → Report a vulnerability) when that option is available.

If private vulnerability reporting is unavailable, check the maintainer's verified GitHub profile for a direct contact method. Do not post vulnerability details in issues, discussions, or pull requests.

When reporting privately, include only the information necessary to investigate:

- A clear description of the issue, its security impact, and the affected code or version.
- Reproduction steps or a minimal proof of concept using synthetic data and non-production resources.
- The Windows version, Cavdeck build/commit, and relevant storage provider, if applicable.
- Any mitigations or relevant prerequisites you have identified.

**Never send real AWS/S3 access keys, secret keys, session tokens, passwords, presigned URLs, private object contents, complete diagnostic dumps, or other people's data.** Use redacted examples and temporary, least-privilege test credentials only when necessary; revoke any credentials that may have been exposed.

Maintainers will evaluate privately reported issues and coordinate remediation and disclosure as appropriate. Response and remediation times depend on severity and available maintainer capacity; no fixed turnaround is guaranteed. Please allow time for investigation before public disclosure.

## Security-sensitive areas

Examples include credential exposure or persistence outside Windows Credential Manager, leaked object keys or signed URLs in diagnostics, unsafe overwrite/delete or cross-connection copy behavior, insecure network handling, and installer/update integrity. Ordinary functional bugs that do not expose private information or create a security risk belong in the [Bug report template](.github/ISSUE_TEMPLATE/bug_report.yml).

## Secure usage

- Use least-privilege credentials; use read-only access where possible.
- Treat presigned links as temporary bearer credentials.
- Review diagnostics and screenshots before sharing.
- Obtain builds only from [official project releases](https://github.com/imnbwd/Cavdeck/releases) or another explicitly verified project distribution channel, once available.

Security reports are not the place to request features or general usage support.

# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

> [!NOTE]
> The application to SignPath Foundation is under review. Until it is accepted, the Windows downloads are not signed.

## What is signed

- The Windows app's own files: `RemoteForKEF.exe`, `RemoteForKEF.dll` and `KefRemote.*.dll`.
- The Windows installers: `RemoteForKEF-win-x64-Setup.exe` and `RemoteForKEF-win-arm64-Setup.exe`.

Only files built from this repository's source are signed with this certificate. Third-party components shipped with the app, such as the .NET runtime, the Windows App SDK and Velopack, keep their own signatures, if they have any.

The macOS app is signed and notarized separately with an Apple Developer ID and is not covered by this policy.

## How a release is signed

1. A version tag is pushed to this repository.
2. The [Release workflow](../.github/workflows/release.yml) builds the app on GitHub-hosted runners and sends the files above to SignPath.
3. An approver checks the request in SignPath and approves it. Nothing is signed without that approval.
4. The workflow checks every signature and attaches the signed files to the release.

## Team roles

| Role | Members |
| --- | --- |
| Committers and reviewers | [kimdaehee0824](https://github.com/kimdaehee0824) |
| Approvers | [kimdaehee0824](https://github.com/kimdaehee0824) |

Changes from anyone else come in as pull requests and are reviewed before they are merged. Team members use multi-factor authentication on GitHub and SignPath.

## Privacy

See the [privacy policy](privacy.md). In short, the app sends no personal data anywhere.

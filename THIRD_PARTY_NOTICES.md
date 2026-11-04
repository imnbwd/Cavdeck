# Third-party components and license review

Cavdeck's own source code is licensed under **GNU GPL v3.0 or later** (`GPL-3.0-or-later`); see [LICENSE](LICENSE).

This document identifies **direct NuGet package references** declared in `Directory.Packages.props` at the time of review (2026-10-21). Third-party components **retain their own licenses and copyright notices**; the GPL license of Cavdeck does not replace them.

> **Scope:** This file tracks direct dependencies and license review. It is **not a complete attribution bundle for a redistributable installer**. Before distributing a binary, review the resolved dependency graph, original package notices, embedded native libraries, bundled executables, and applicable WebView2 Runtime terms.

## Direct runtime dependencies

| Package | Pinned version | Declared license | Package license / source |
| --- | --- | --- | --- |
| AWSSDK.S3 | 4.0.104.4 | Apache-2.0 | [NuGet package](https://www.nuget.org/packages/AWSSDK.S3/4.0.104.4) |
| Microsoft.Data.Sqlite | 10.0.13 | MIT | [NuGet package](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.13) |
| Microsoft.Extensions.DependencyInjection | 10.0.13 | MIT | [NuGet package](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection/10.0.13) |
| H.NotifyIcon.Wpf | 2.4.1 | MIT | [NuGet package](https://www.nuget.org/packages/H.NotifyIcon.Wpf/2.4.1) |
| Velopack | 1.2.161 | MIT | [NuGet package](https://www.nuget.org/packages/Velopack/1.2.161) |
| Microsoft.Web.WebView2 (SDK) | 1.0.4258.31 | BSD-3-Clause-style license (per package license file) | [Exact NuGet license](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4258.31/License) |

## Test / development dependencies

These are used in the test and build process; they are not assumed to be shipped with the end-user app.

| Package | Pinned version | Declared license | Package |
| --- | --- | --- | --- |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT | [NuGet](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/17.14.1) |
| xunit | 2.9.3 | Apache-2.0 | [NuGet](https://www.nuget.org/packages/xunit/2.9.3) |
| xunit.runner.visualstudio | 3.1.5 | Apache-2.0 | [NuGet](https://www.nuget.org/packages/xunit.runner.visualstudio/3.1.5) |

## Compatibility assessment

The directly referenced packages identified above declare permissive MIT, Apache-2.0, or BSD-style terms. **In general these licenses are compatible with GPLv3**, provided the applicable third-party copyright, attribution, and notice requirements are fulfilled. This preliminary finding **does not certify that the complete redistributable application complies with every license**.

The WebView2 **SDK** package and Microsoft Edge **WebView2 Runtime** are not the same component. The NuGet SDK package includes its own BSD-style license file. The Runtime and any runtime installer/bundling arrangement must be assessed against Microsoft's [WebView2 distribution guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution) and applicable terms before packaging.

### Outstanding release checks

1. On Windows, restore the pinned solution and enumerate **all transitive dependencies** with `dotnet list Cavdeck.sln package --include-transitive` (or the equivalent supported command).
2. Inspect package license files, embedded native assemblies, assets and any optional Velopack packaging tooling; retain complete copyright/license/NOTICE text as required in the binary distribution.
3. Review `Microsoft.Data.Sqlite`'s SQLitePCLRaw/SQLite native dependencies and other transitive packages at their **actually resolved versions**, not guessed versions.
4. Confirm WebView2 Runtime installation, bootstrapper bundling and end-user redistribution terms separately from the SDK.
5. Verify copyright/provenance and usage terms for checked-in icons, images, fonts, documentation and other non-code assets.
6. Validate the **exact release installer contents** and accompanying license notices before publishing; regenerate this inventory when dependencies change.

Reference: [GNU license compatibility guidance](https://www.gnu.org/licenses/license-compatibility.html).

This file records a review and open release tasks. It is **not legal advice** and does not replace the third-party notices required for a distributed installer.

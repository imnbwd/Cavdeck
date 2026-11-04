# Test strategy

Cavdeck uses xUnit across four Windows/.NET 10 test projects: `Cavdeck.Domain.Tests`, `Cavdeck.Application.Tests`, `Cavdeck.Infrastructure.Tests`, and `Cavdeck.App.Tests`. They cover domain rules, connections and Explorer, transfers and journals, S3 adapters, WPF resources, localization, and updates.

Run the solution tests on Windows:

```powershell
dotnet restore Cavdeck.sln
dotnet build Cavdeck.sln -c Release --no-restore
dotnet test Cavdeck.sln -c Release --no-build
```

The [CI workflow](../.github/workflows/ci.yml) is configured to run these checks on Windows. Passing them does not prove interoperability with real cloud storage or the safety of an installed release.

## Optional MinIO integration tests

The `S3ObjectTransferIntegrationTests` and `AwsS3GatewayCopyIntegrationTests` classes exercise real S3-compatible operations when a MinIO endpoint is configured. Set these environment variables for a **dedicated test instance**:

| Variable | Purpose |
| --- | --- |
| `CAVDECK_TEST_MINIO_ENDPOINT` | Test server URL, such as `http://127.0.0.1:9000` |
| `CAVDECK_TEST_MINIO_ACCESS_KEY` | Test-only access key |
| `CAVDECK_TEST_MINIO_SECRET_KEY` | Test-only secret key |

The tests use fictional local default credentials if the access-key variables are unset. They create, write to, and delete randomly named test buckets. **Never aim them at a production endpoint or use production credentials.** The configured account must be permitted to create and delete buckets.

**Important:** In the current test implementation, if `CAVDECK_TEST_MINIO_ENDPOINT` is absent, the methods return early. xUnit can therefore report them as *passed*, not *skipped*, even though no integration requests were made. A green CI run alone must not be interpreted as successful MinIO validation.

## Manual release testing

Manual release testing covers installed builds, AWS S3/R2/MinIO interoperability, Explorer, transfers, cross-connection copies, previews, security, localization, tray, DPI, and two-version upgrades. Release-blocking P0 cases must be completed on a release candidate, with PASS/FAIL/BLOCKED/N/A results and evidence recorded **outside source control**.

See [release validation](../CONTRIBUTING.md#release-validation). Passing CI does not prove installer trust or provider compatibility.

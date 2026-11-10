# Test strategy

Cavdeck uses xUnit across five Windows/.NET 10 test projects: `Cavdeck.Domain.Tests`, `Cavdeck.Application.Tests`, `Cavdeck.Infrastructure.Tests`, `Cavdeck.App.Tests`, and `Cavdeck.Cli.Tests`. They cover domain rules, connections and Explorer, transfers and journals, S3 adapters, WPF resources, localization, and updates.

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

## Optional CLI real-provider tests

The `Cavdeck.Cli.Tests/CloudStorageTests` checks exercise S3-compatible read/write behavior, conditional writes, and relevant copy/move/delete paths against **dedicated test storage**. They are intentionally not enabled by default.

Set `CAVDECK_CLI_TEST_<PROVIDER>_ENABLED=1` for each provider to test, plus these variables with the same prefix:

| Suffix | Purpose |
| --- | --- |
| `BUCKET` | Existing dedicated test bucket |
| `PREFIX` | Dedicated test object prefix **starting with `cavdeck-cli-test/`** |
| `ACCESS_KEY` | Test-only access key ID |
| `SECRET_KEY` | Test-only secret key |
| `ENDPOINT` | Required for `R2` and `MINIO`; not required for `AWS` |
| `REGION` | Optional; defaults to `auto` for R2 and `us-east-1` otherwise |

The supported `<PROVIDER>` values are `AWS`, `R2`, and `MINIO`. For example, R2 reads `CAVDECK_CLI_TEST_R2_ENABLED`, `CAVDECK_CLI_TEST_R2_BUCKET`, and the other `CAVDECK_CLI_TEST_R2_*` variables.

**Important:** These tests create objects, copy/move objects, and attempt to remove their test keys. They require permissions suitable for those operations and are **not safe to point at production storage or prefixes**. Use the `cavdeck-cli-test/` prefix, isolated credentials, and a dedicated test bucket. Missing configuration causes these `CloudFact` tests to be skipped.

For everyday CLI usage and provider limitations, see the [CLI User Guide](../docs/cli.md).

## Manual release testing

Manual release testing covers installed builds, AWS S3/R2/MinIO interoperability, Explorer, transfers, cross-connection copies, previews, security, localization, tray, DPI, and two-version upgrades. Release-blocking P0 cases must be completed on a release candidate, with PASS/FAIL/BLOCKED/N/A results and evidence recorded **outside source control**.

See [release validation](../CONTRIBUTING.md#release-validation). Passing CI does not prove installer trust or provider compatibility.

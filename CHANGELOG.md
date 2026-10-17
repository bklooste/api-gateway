# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `GLOBAL_`-prefixed env vars: environment-wide defaults from a shared env file, read at the lowest
  precedence (below `appsettings.json`). `DOTNET_` vars still override everything.
- Reads downstream OpenAPI 3.0, 3.1 and 3.2 documents (Microsoft.OpenApi 3.x). Before, a 3.2 document
  failed to load and that service's paths were missing from the merged document.
- `gateway:openApi:outputVersion` (`3.0` | `3.1` | `3.2`, default `3.1`): the version the merged document is
  emitted as. An unknown value fails startup.
- `{brand}` in `ProxyUrl`, filled from the brand header like `{userId}`.
- `{path:name}` scope placeholder: the request's value for the `{name}` segment of `ApiUrl`.
- `gateway:overwriteBrandQueryParam` (default `true`): turn off on an admin deployment so the caller's
  `?brand=` reaches the backend instead of being replaced with the admin's own brand.

### Changed

- **Breaking:** requires .NET 11 (`net11.0`, SDK pinned in `global.json`); the image is built on
  `aspnet:11.0-resolute-chiseled`.
- **Breaking:** the merged OpenAPI document is emitted as 3.1 by default instead of 3.0. Set
  `gateway:openApi:outputVersion: "3.0"` to keep the old output.
- **Breaking:** the gateway no longer sets a `customerId` query parameter (it only sets `userId` and `brand`),
  and a caller-sent `customerId` now passes through as an ordinary parameter. Backends must use `userId` for
  the caller's identity.
- **Breaking:** `customerid` is no longer in the default `gateway:headers:forwardedIdentityHeaders`. The
  authenticating proxy is expected neither to produce nor to pass it on.

### Fixed

- Downloads lost their `Content-Disposition` header, so files arrived without their name.

- Initial release. Config-driven routing, per-route scope authorization, URL rewriting, response
  projection, opt-in caching via http-rediscache, downstream OpenAPI merging and OTLP telemetry.
- Route table mounted separately from the image's own settings and watched for changes, so routing
  can be updated without a restart. A malformed file is rejected and the last good table keeps
  serving.

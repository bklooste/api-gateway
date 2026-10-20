# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Optional per-route `"Translate": true`: the response is routed through the `translate` endpoint
  (`translate/<path>?<query>&translateUpstream=<upstream>`), target language from `?lang=`. With `Cache` as
  well, the cache sits in front of the translate service and `lang` is added to the cache key. Routes
  without it, and deployments without a `translate` endpoint, are unchanged.
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
- `gateway:endpointRefreshMinutes` (default `5`, `0` disables the timer) and
  `gateway:endpointResolveTimeoutSeconds` (default `2`): a background runner re-reads `EndPoints` on every
  config change and on that interval, and publishes the result as a new immutable snapshot without blocking
  requests. A name new to the map goes live at once; a changed URL is adopted only once its host resolves,
  one entry at a time, otherwise the last-known-good URL is kept. A route file that fails to parse, or an
  `EndPoints` section that has gone empty, leaves the map as it was.

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
- `EndPoints` was read once at startup, so a route file edit that renamed a backend reloaded the routes
  but not the endpoint they named: every request on that route failed with "BaseAddress must be set" until
  the gateway was restarted. Endpoints now follow the route file like the route table does.

- Initial release. Config-driven routing, per-route scope authorization, URL rewriting, response
  projection, opt-in caching via http-rediscache, downstream OpenAPI merging and OTLP telemetry.
- Route table mounted separately from the image's own settings and watched for changes, so routing
  can be updated without a restart. A malformed file is rejected and the last good table keeps
  serving.

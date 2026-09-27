# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `{brand}` in `ProxyUrl`, filled from the brand header like `{userId}`.
- `{path:name}` scope placeholder: the request's value for the `{name}` segment of `ApiUrl`.
- `gateway:identityQueryParams`: which of `brand`/`customerId`/`userId` the gateway sets from the identity
  headers (default: all three). An admin deployment can leave `brand` to the caller.

### Fixed

- Downloads lost their `Content-Disposition` header, so files arrived without their name.

- Initial release. Config-driven routing, per-route scope authorization, URL rewriting, response
  projection, opt-in caching via http-rediscache, downstream OpenAPI merging and OTLP telemetry.
- Route table mounted separately from the image's own settings and watched for changes, so routing
  can be updated without a restart. A malformed file is rejected and the last good table keeps
  serving.

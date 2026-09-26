# api-gateway

[![CI](https://github.com/bklooste/api-gateway/actions/workflows/ci.yml/badge.svg)](https://github.com/bklooste/api-gateway/actions/workflows/ci.yml)
[![Image](https://img.shields.io/badge/ghcr.io-api--gateway-blue)](https://github.com/bklooste/api-gateway/pkgs/container/api-gateway%2Fgateway)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

A small **config-driven API gateway**. You give it a JSON route table; it matches incoming requests,
checks the caller's scopes, rewrites the URL, and forwards to your backend services — optionally
through a cache. Adding or changing a route is a config edit, not a deployment.

```jsonc
{
  "ProxyName": "offers",           // which backend (resolved from EndPoints)
  "ApiUrl":    "v3/nav",           // what callers request
  "ProxyUrl":  "v3/event/nav",     // what the backend serves
  "AnyScopesPredicate": [ "events-r" ],
  "Cache": { "CacheVars": [ "brand" ], "TtlSeconds": 60 }
}
```

What it does:

- **Routes and rewrites** — prefix matching with `{param}` wildcards carried across to the backend path.
- **Authorizes** — per-route scope predicates, with `{userId}`, `{brand}` and `{resource:Prop}`
  placeholders. The last resolves by fetching the target resource first, so you can gate on who owns it.
- **Caches** — routes opt in and are proxied via [http-rediscache](https://github.com/bklooste/http-rediscache).
- **Merges OpenAPI** — pulls each backend's document and presents one combined spec under the
  client-facing paths.
- **Reloads live** — edit the mounted route file (or ConfigMap) and routing changes within seconds,
  no restart.

> ### It does not authenticate
> This gateway performs **authorization only**. It must run behind an authenticating proxy
> (Envoy `ext_authz`, nginx `auth_request`, …) that validates the caller and passes their identity —
> **including their granted scopes** — as request headers. Anything that can reach the gateway
> directly can claim any identity. See [Where this sits](#where-this-sits).

## Quick start

```bash
docker compose up -d
```

That runs the gateway with a MockServer standing in for your backends, plus http-rediscache and its
Redis. The route table is [`config/routes.json`](config/routes.json); the backends are faked in
[`quickstart/mockserver-expectations.json`](quickstart/mockserver-expectations.json).

The identity headers below would normally be set by your authenticating proxy. In the quick start
you supply them yourself — which is exactly how you test authorization rules locally.

```bash
AUTH='-H auth-claim-scopes:events-r,profile-r -H userid:user-42 -H brand:acme'

# 1. Plain proxy
curl -s $AUTH localhost:8080/v1/events
# {"events":[{"id":"e1","name":"Demo Event"}]}

# 2. Path rewrite — callers see /v1/me/profile, the backend serves /v1/users/{userId}/profile.
#    {userId} comes from the trusted header, never from caller input.
curl -s $AUTH localhost:8080/v1/me/profile
# {"userId":"user-42","tier":"gold"}

# 3. Missing scope -> 403, and the backend is never called
curl -s -o /dev/null -w '%{http_code}\n' $AUTH localhost:8080/v1/admin/settings
# 403

# 4. Not in the route table -> 404
curl -s -o /dev/null -w '%{http_code}\n' $AUTH localhost:8080/v1/nope
# 404
```

### Caching

`/v1/slow` echoes the time the backend generated the response, so a cached reply is obvious — the
timestamp does not move. The cache key includes `brand`, so a different brand is a different entry:

```bash
curl -s -H auth-claim-scopes:events-r -H brand:acme   localhost:8080/v1/slow
# { "generatedAt": "2026-09-26T07:44:14.490Z" }
curl -s -H auth-claim-scopes:events-r -H brand:acme   localhost:8080/v1/slow
# { "generatedAt": "2026-09-26T07:44:14.490Z" }   <- same: served from cache
curl -s -H auth-claim-scopes:events-r -H brand:globex localhost:8080/v1/slow
# { "generatedAt": "2026-09-26T07:44:31.051Z" }   <- different brand, different entry
```

### Live route changes

With the stack still running, add a route to `config/routes.json` and watch it start serving —
no restart, no rebuild:

```bash
curl -s -o /dev/null -w '%{http_code}\n' $AUTH localhost:8080/v1/events2   # 404
# ... copy the v1/events block in config/routes.json, change ApiUrl to "v1/events2" ...
sleep 10
curl -s -o /dev/null -w '%{http_code}\n' $AUTH localhost:8080/v1/events2   # 200
docker compose logs gateway | grep "Route table"
# Route table reloaded: 4 -> 5 routes
```

Teardown: `docker compose down -v`

## Managing the route table

Configuration is layered, lowest precedence first:

| Layer | Source | Holds |
| --- | --- | --- |
| 1 | `appsettings.json`, baked into the image | Logging, `gateway` defaults, an empty route table |
| 2 | `/config/routes.json`, mounted | `proxyConfig` — the route table |
| 3 | `DOTNET_*` environment variables | Per-environment overrides, chiefly `EndPoints` |

The route table is a **separate mounted file** rather than a replacement `appsettings.json`, so
mounting it cannot silently drop the image's own defaults. Override the location with
`GATEWAY_ROUTE_FILE`. Nothing is baked into the image, so one image serves every gateway instance;
the route table is deployment config.

In Kubernetes that is a ConfigMap holding `routes.json`, mounted at `/config`. The real tables are
70 routes / 19 KB and 135 routes / 37 KB, comfortably inside the 1 MiB ConfigMap limit.

### Reloading

The mounted file is watched, so **editing the ConfigMap changes routing without a restart** —
`kubectl` applies it, the kubelet propagates it to the volume within about a minute, and the gateway
rebuilds. Three details make this safe:

- **Mount the directory, not the file.** Kubernetes updates a ConfigMap by swapping a `..data`
  symlink; a single-file bind mount pins the old inode and never sees the change.
- **Polling, not inotify.** The image sets `DOTNET_USE_POLLING_FILE_WATCHER=true`, because inotify
  does not reliably fire for that symlink swap. A timer re-reads every `ConfigReloadMinutes`
  (default 10) as a backstop.
- **Rebuilds never block a request.** The table is rebuilt on a background thread and swapped in
  atomically when complete; requests keep serving the previous table meanwhile. Build cost is
  therefore irrelevant — it is paid once per change, not per request.

A malformed file is rejected and logged, and the last good table stays in service. This needs the
explicit guard in `RouteTableProvider`: .NET's file configuration provider *clears its own data*
before reporting a parse error, so without it one bad config push would swap in a zero-route table
and 404 everything. `RouteReloadTests` asserts all of this against a real mounted volume.

## Configuration

| Section                | Purpose                                                            |
| ---------------------- | ------------------------------------------------------------------ |
| `EndPoints`            | Downstream service name → base URL. `ProxyName` resolves against it. |
| `proxyConfig`          | The route table. See `ProxyConfig.cs` for every field.              |
| `globalRequiredScopes` | Scopes folded into every route's `AllScopesPredicate`.              |
| `gateway`              | `GatewayOptions`: identity header names, timeouts, opt-in endpoints. |

A minimal route table:

```jsonc
{
  "EndPoints": { "offer": "http://offer-offerings:8080/", "cache": "http://cache:8080/" },
  "proxyConfig": [
    {
      "ProxyName": "offer",
      "ApiUrl": "v3/nav",             // what callers request
      "ProxyUrl": "v3/event/nav",     // what the downstream serves
      "AnyScopesPredicate": [ "events-r" ],
      "Cache": { "CacheVars": [ "brand" ], "TtlSeconds": 60 }
    }
  ]
}
```

`ApiUrl` matches as a prefix and `{param}` segments are wildcards, carried across to `ProxyUrl`.
Scope predicates support the placeholders `{userId}`, `{brand}` and `{resource:Prop}`, the last
resolved by fetching the target resource first (see `ResourceAuth`).

## Where this sits

Authentication happens **before** the gateway. The gateway only does authorization.

```
client ──► edge proxy (Envoy / nginx) ──► api-gateway ──► backend services
             │                              │
             │ validates the JWT / session  │ matches the route, checks the
             │ and REPLACES the identity    │ scopes in auth-claim-scopes,
             │ headers with trusted values  │ rewrites the URL, forwards
```

The edge proxy is responsible for validating the caller's credential, resolving it to a user, and
setting these headers on the request it forwards. The gateway reads them and trusts them completely:

| Header              | Contains                                   | Used for                                          |
| ------------------- | ------------------------------------------ | ------------------------------------------------- |
| `auth-claim-scopes` | The user's granted scopes, comma-separated | Every `AnyScopesPredicate` / `AllScopesPredicate` check |
| `userid`            | The user's id                              | The `{userId}` placeholder, in scopes and `ProxyUrl` |
| `brand`             | The user's brand/tenant                    | The `{brand}` placeholder in scope predicates      |

So a caller granted `events-r` and `bet-w` arrives at the gateway as:

```http
GET /v3/nav HTTP/1.1
auth-claim-scopes: events-r,bet-w
userid: 4f2e...
brand: acme
```

and a route declaring `"AnyScopesPredicate": ["events-r"]` passes, while one declaring
`["admin"]` gets a 403 before the backend is called.

The names are configurable under `gateway:headers` if your proxy emits different ones.

These headers are forwarded on to the backend services, which apply their own brand and ownership
rules to them. The gateway passes through whatever arrived — it does not strip or re-sign them — so
**the edge proxy must overwrite these headers rather than append to them.** A proxy that appends
leaves the client's original value in place next to the trusted one.

### Deployment requirement

**The gateway must not be reachable except through the authenticating proxy.** It has no way to
distinguish the proxy from anyone else, so anything that can open a connection to it can set
`auth-claim-scopes: admin` and act as any user. In Kubernetes that means a `ClusterIP` service with
no ingress of its own — only the edge proxy routes to it.

The corollary is that debugging locally needs no special mode: send the scopes you want as a header.

`gateway:enableDebugEndpoint` maps `GET /debug`, which echoes the trusted identity headers back. It
defaults to off and should stay off in any deployed instance.

There is no local-development auth bypass, and none should be added. Debugging against the gateway
means sending the scopes you want in `auth-claim-scopes`, so a bypass would buy nothing while adding
a mode in which authorization silently does not run.

## Caching

Routes opt in via `Cache`. The request is then sent to the `cache` endpoint
([http-rediscache](https://github.com/bklooste/http-rediscache)) with the real upstream URL as an
encoded query parameter. Only GET is cached unless the route pins `Method`, so a path that also
serves writes is not cached by accident.

## Telemetry

Traces, metrics and logs are exported over OTLP, configured entirely by the standard environment
variables (`OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES`). With no
endpoint set the exporters stay inert and the container runs normally. Health probes are excluded
from traces. `/health/live` and `/health/ready` are mapped ahead of the catch-all route.

## Tests

```bash
dotnet test ApiGateway.slnx        # unit tests; the compose-backed ones skip

# Full stack: the gateway image against the real cache container and a MockServer upstream
DOCKER_UID=$(id -u) DOCKER_GID=$(id -g) \
  docker compose -f tst/ApiGateway.Tests/test.compose.yml \
  up --build --abort-on-container-exit --exit-code-from tst
```

The compose stack asserts that a cached route hits the upstream exactly once, that a client cannot
smuggle its own `userId`, and that editing the mounted route file changes routing without a restart
(including that a malformed file is rejected and the previous table keeps serving). The
compose-backed tests skip unless `GatewayHttpUrl` is set, so a plain `dotnet test` needs no Docker.
## License

MIT — see [LICENSE](LICENSE).

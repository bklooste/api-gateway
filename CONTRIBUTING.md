# Contributing

Issues and pull requests are welcome.

- `dotnet test ApiGateway.slnx` must pass. For anything touching routing, authorization or the
  route reload path, also run the compose stack — it is the only place the container, the real
  cache and the mounted-volume reload are exercised together:

  ```bash
  DOCKER_UID=$(id -u) DOCKER_GID=$(id -g) \
    docker compose -f tst/ApiGateway.Tests/test.compose.yml \
    up --build --abort-on-container-exit --exit-code-from tst
  ```

- Changes to authorization behaviour need a test that fails without the change. A test that passes
  either way is worse than no test.
- Keep the project dependency-free beyond the framework, OpenAPI and OpenTelemetry packages. The
  project directory is the entire container build context, deliberately.

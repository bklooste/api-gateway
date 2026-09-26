# Test runner for the compose-backed gateway tests.
#
# Build context is the repository root:
#
#   docker build -f tst/ApiGateway.Tests/Dockerfile .
#
# Published self-contained so the runtime image stays small — the xUnit v3 test project builds
# to an executable that needs no SDK.

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY Directory.Build.props version.json ./
COPY src/ApiGateway/ src/ApiGateway/
COPY tst/ApiGateway.Tests/ tst/ApiGateway.Tests/

RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish tst/ApiGateway.Tests/ApiGateway.Tests.csproj \
        -c Release -o /app/publish -r linux-x64 --self-contained && \
    mkdir -p /app/results

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app/publish .
# The chiseled image runs as UID 64198; the TRX result file is written here.
COPY --from=build --chown=64198:64198 /app/results /app/results
ENTRYPOINT ["./ApiGateway.Tests"]

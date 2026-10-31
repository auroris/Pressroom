# .NET 10 official images use Ubuntu 24.04, not Debian. Digests refreshed 2026-10-31.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS builder
# Chromium list from Playwright 1.64.1 nativeDeps.ts, ubuntu24.04-x64.chromium.
RUN apt-get update && apt-get install -y --no-install-recommends \
    libasound2t64 libatk-bridge2.0-0t64 libatk1.0-0t64 libatspi2.0-0t64 libcairo2 \
    libcups2t64 libdbus-1-3 libdrm2 libgbm1 libglib2.0-0t64 libnspr4 libnss3 \
    libpango-1.0-0 libx11-6 libxcb1 libxcomposite1 libxdamage1 libxext6 libxfixes3 \
    libxkbcommon0 libxrandr2 fontconfig fonts-noto-core curl ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --uid 10001 --create-home pressroom
WORKDIR /src
COPY --chown=10001:10001 . .
USER 10001:10001
# The package check runs via docker run with the shipped seccomp profile, not in
# BuildKit's restricted RUN environment. See build/Build-Container.ps1.
ENTRYPOINT ["pwsh", "-NoProfile", "-File", "/src/build/Publish-Linux.ps1"]

FROM scratch AS verified
COPY artifacts/container-publish/ /payload/

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee AS runtime
# Playwright 1.64.1 Chromium shared libraries; no SDK, installer, or apt cache.
RUN apt-get update && apt-get install -y --no-install-recommends \
    libasound2t64 libatk-bridge2.0-0t64 libatk1.0-0t64 libatspi2.0-0t64 libcairo2 \
    libcups2t64 libdbus-1-3 libdrm2 libgbm1 libglib2.0-0t64 libnspr4 libnss3 \
    libpango-1.0-0 libx11-6 libxcb1 libxcomposite1 libxdamage1 libxext6 libxfixes3 \
    libxkbcommon0 libxrandr2 fontconfig fonts-noto-core curl ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --uid 10001 --create-home pressroom \
    && mkdir -p /app /etc/pressroom /var/lib/pressroom \
    && chown pressroom:pressroom /var/lib/pressroom
WORKDIR /app
COPY --from=verified /payload/ /app/
USER 10001:10001
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
VOLUME ["/var/lib/pressroom"]
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=3 CMD curl -fsS http://localhost:8080/v1/health || exit 1
ENTRYPOINT ["dotnet", "Pressroom.Server.dll"]
CMD ["--config", "/etc/pressroom/pressroom.settings.json"]

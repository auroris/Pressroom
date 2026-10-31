# Track Microsoft's current compatible Linux bases; the build helper always pulls.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS builder
# Chromium list from Playwright 1.64.1 nativeDeps.ts, ubuntu24.04-x64.chromium: the pinned
# PressroomPlaywrightVersion that release builds use. Recheck both lists when the pin changes;
# -PlaywrightVersion latest builds are verified by the sandboxed check, not by this comment.
RUN apt-get update && apt-get install -y --no-install-recommends \
    libasound2t64 libatk-bridge2.0-0t64 libatk1.0-0t64 libatspi2.0-0t64 libcairo2 \
    libcups2t64 libdbus-1-3 libdrm2 libgbm1 libglib2.0-0t64 libnspr4 libnss3 \
    libpango-1.0-0 libx11-6 libxcb1 libxcomposite1 libxdamage1 libxext6 libxfixes3 \
    libxkbcommon0 libxrandr2 fontconfig fonts-noto-core curl ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --uid 10001 --create-home pressroom
WORKDIR /src
COPY --chown=10001:10001 . .
RUN chown 10001:10001 /src
USER 10001:10001
# The package check runs via docker run with the shipped seccomp profile, not in
# BuildKit's restricted RUN environment. See build/Build-Container.ps1.
ENTRYPOINT ["pwsh", "-NoProfile", "-File", "/src/build/Publish-Linux.ps1"]

FROM scratch AS verified
COPY artifacts/container-publish/ /payload/

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# Pinned Playwright 1.64.1 Chromium shared libraries (as above); no SDK, installer, or apt cache.
# The image runs dotnet directly; run it with an init (Compose init: true, docker run --init).
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
LABEL org.opencontainers.image.source="https://github.com/auroris/Pressroom" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.title="Pressroom"
COPY --from=verified /payload/ /app/
USER 10001:10001
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=3 CMD curl -fsS http://localhost:8080/v1/health || exit 1
ENTRYPOINT ["dotnet", "Pressroom.Server.dll"]
CMD ["--config", "/etc/pressroom/pressroom.settings.json"]

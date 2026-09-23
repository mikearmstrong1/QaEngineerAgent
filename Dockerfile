# Build once; promote the resulting image digest unchanged between roles/environments.
FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble AS dotnet-build
WORKDIR /src
COPY global.json Directory.Build.props QualitySystem.sln ./
COPY src ./src
COPY tests ./tests
COPY prompts ./prompts
COPY schemas ./schemas
RUN dotnet restore QualitySystem.sln --locked-mode
RUN dotnet publish src/Quality.Api/Quality.Api.csproj -c Release --no-restore -o /publish /p:UseAppHost=false
RUN dotnet publish src/Quality.CommandCenter/Quality.CommandCenter.csproj -c Release --no-restore -o /command-center /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble AS dotnet-runtime
FROM mcr.microsoft.com/playwright:v1.63.0-noble AS prepared
USER root
# The execution adapter supports Chromium; remove unused vulnerable media plugins.
RUN apt-get update && apt-get install -y --no-install-recommends git \
    && apt-get purge -y gstreamer1.0-plugins-bad libgstreamer-plugins-bad1.0-0 \
    && rm -rf /var/lib/apt/lists/*
# Replace only the vulnerable bundled packages from pinned public tarballs.
# Installing inside npm's own project resolves unavailable development workspaces.
RUN set -eu; \
    npm install --global --prefix /usr npm@12.0.2; \
    mkdir -p /tmp/npm-fixes; cd /tmp/npm-fixes; \
    npm pack --ignore-scripts brace-expansion@5.0.9 ip-address@10.3.1 tar@7.5.21; \
    for package in brace-expansion ip-address tar; do \
        tar -xzf "${package}"-*.tgz; \
        rm -rf "/usr/lib/node_modules/npm/node_modules/${package}"; \
        mv package "/usr/lib/node_modules/npm/node_modules/${package}"; \
    done; \
    cd /tmp; rm -rf /tmp/npm-fixes /root/.npm
COPY --from=dotnet-runtime /usr/share/dotnet /usr/share/dotnet
ENV DOTNET_ROOT=/usr/share/dotnet PATH="/usr/share/dotnet:${PATH}" \
    ASPNETCORE_URLS=http://0.0.0.0:8080 Quality__Store=Postgres \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
WORKDIR /app
COPY package.json package-lock.json ./
COPY playwright ./playwright
RUN npm ci && npm run build --workspace @quality/playwright && rm -rf /root/.npm && mkdir -p /data/jobs /data/executions && chown -R pwuser:pwuser /data
COPY --from=dotnet-build /publish ./service
COPY --from=dotnet-build /command-center ./command-center
COPY schemas ./schemas
COPY prompts ./prompts
USER pwuser
EXPOSE 8080
ENTRYPOINT ["dotnet", "/app/service/Quality.Api.dll"]
CMD ["api"]

# Copy only the final filesystem, excluding deleted inherited caches from shipped layers.
FROM scratch
COPY --from=prepared / /
ENV DOTNET_ROOT=/usr/share/dotnet PATH="/usr/share/dotnet:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin" \
    ASPNETCORE_URLS=http://0.0.0.0:8080 Quality__Store=Postgres \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
WORKDIR /app
USER pwuser
EXPOSE 8080
ENTRYPOINT ["dotnet", "/app/service/Quality.Api.dll"]
CMD ["api"]

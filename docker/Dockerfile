# syntax=docker/dockerfile:1

# Build the .NET services without the test/tool projects. Keeping the restore
# layer separate lets Docker reuse it until a project file changes.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src ./src
COPY contracts/descriptors ./contracts/descriptors
RUN dotnet restore src/Anima.Knowledge.Service/Anima.Knowledge.Service.csproj \
 && dotnet restore src/Anima.Bus.Service/Anima.Bus.Service.csproj \
 && dotnet restore src/Anima.Workflow.Service/Anima.Workflow.Service.csproj \
 && dotnet restore src/Anima.Dashboard.Service/Anima.Dashboard.Service.csproj
RUN dotnet publish src/Anima.Knowledge.Service/Anima.Knowledge.Service.csproj \
      -c Release --no-restore -o /out/knowledge \
 && dotnet publish src/Anima.Bus.Service/Anima.Bus.Service.csproj \
      -c Release --no-restore -o /out/bus \
 && dotnet publish src/Anima.Workflow.Service/Anima.Workflow.Service.csproj \
      -c Release --no-restore -o /out/workflow \
 && dotnet publish src/Anima.Dashboard.Service/Anima.Dashboard.Service.csproj \
      -c Release --no-restore -o /out/dashboard

# Build the dashboard UI separately so npm dependencies are cached independently
# from the .NET source tree.
FROM node:24-alpine AS ui-build
WORKDIR /src/ui
COPY ui/package.json ui/package-lock.json ./
RUN npm ci
COPY ui/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
ENV ANIMA_REPO_ROOT=/app \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY --from=dotnet-build /out/knowledge ./services/knowledge
COPY --from=dotnet-build /out/bus ./services/bus
COPY --from=dotnet-build /out/workflow ./services/workflow
COPY --from=dotnet-build /out/dashboard ./services/dashboard
COPY --from=ui-build /src/ui/dist ./ui/dist
COPY contracts/descriptors ./contracts/descriptors

# Compose overrides the command per service. The default keeps `docker run`
# useful for a quick dashboard-only smoke.
ENTRYPOINT ["dotnet"]
CMD ["services/dashboard/Anima.Dashboard.Service.dll"]

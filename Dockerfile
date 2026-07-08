# ── Stage 1: Build ──────────────────────────────────────────
# Use the official .NET SDK image to compile the app
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /app

# Copy the project file and restore dependencies first
# (Docker caches this layer so it doesn't re-download packages every build)
COPY RssReader.csproj .
RUN dotnet restore

# Copy the rest of the source code
COPY . .

# Publish a release build into the /app/publish folder
RUN dotnet publish -c Release -o /app/publish

# ── Stage 2: Run ────────────────────────────────────────────
# Use the smaller runtime-only image (no compiler needed to run)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app

# Copy the published output from the build stage
COPY --from=build /app/publish .

# The app will be started by Railway using this command
ENTRYPOINT ["dotnet", "RssReader.dll"]

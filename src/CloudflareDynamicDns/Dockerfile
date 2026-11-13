FROM mcr.microsoft.com/dotnet/sdk:11.0-alpine AS base

WORKDIR /src
COPY Directory.Packages.props .
COPY /src .

RUN dotnet restore "CloudflareDynamicDns/CloudflareDynamicDns.csproj" --runtime linux-musl-x64
RUN dotnet publish "CloudflareDynamicDns/CloudflareDynamicDns.csproj" \
    -c Release -o /app/publish \
    --no-restore \
    --runtime linux-musl-x64 \
    --self-contained true \
    /p:PublishSingleFile=true

FROM mcr.microsoft.com/dotnet/runtime-deps:11.0-alpine AS final

RUN adduser --disabled-password \
  --home /app \
  --gecos '' dotnetuser && chown -R dotnetuser /app

RUN apk upgrade musl

WORKDIR /app
COPY --from=base /app/publish .

ENTRYPOINT ["./CloudflareDynamicDns"]

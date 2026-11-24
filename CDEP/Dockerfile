# API 서버 i-api 컨테이너 (지금은 쓰지 않음: API 서버는 사내 서버 PC 한 대에서 돌림, CDEP\README.md). NuGet 패키지 없이 .NET 10 만 씀.
# 설정은 settings.txt 와 같은 이름의 환경 변수로 (env.sample). 데이터(앱 목록 · 스크린샷 · 공지 · 스킬 · 백업)는 /data 를 영구 저장소로 연결하세요.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish i-api.csproj -c Release -o /app --nologo -p:ApiServerContainer=true

FROM mcr.microsoft.com/dotnet/runtime:10.0
ENV RELAY_BIND=0.0.0.0 \
    RELAY_PORT=2003 \
    RELAY_DATA_ROOT=/data
WORKDIR /app
COPY --from=build /app /app
RUN useradd --system --uid 10001 --home-dir /app iapi \
    && mkdir -p /data && chown iapi /data
USER iapi

VOLUME ["/data"]
EXPOSE 2003

CMD ["dotnet", "/app/i-api.dll"]

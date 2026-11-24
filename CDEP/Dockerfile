# API 서버 컨테이너 (지금은 쓰지 않음: API 서버는 사내 서버 PC 한 대에서 돌림, CDEP\README.md). 표준 라이브러리만 써서 pip 설치가 없습니다.
# 설정은 SETTINGS 와 같은 이름의 환경 변수로 (env.sample). 데이터(앱 목록 · 스크린샷 · 공지 · 스킬 · 백업)는 /data 를 영구 저장소로 연결하세요.
FROM python:3.12-slim

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    RELAY_BIND=0.0.0.0 \
    RELAY_PORT=2003 \
    RELAY_DATA_ROOT=/data

WORKDIR /app
COPY relay.py /app/relay.py

RUN useradd --system --uid 10001 --home-dir /app relay \
    && mkdir -p /data && chown relay /data
USER relay

VOLUME ["/data"]
EXPOSE 2003

# 서버가 응답하는지 (배포 환경이 따로 검사하면 지워도 됨)
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s \
    CMD python -c "import os, urllib.request; urllib.request.urlopen('http://127.0.0.1:%s/health' % os.environ.get('RELAY_PORT', '2003'), timeout=4)"

CMD ["python", "/app/relay.py"]

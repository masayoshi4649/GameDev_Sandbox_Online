# syntax=docker/dockerfile:1
# server を通常コンテキスト、build/server を artifact 名前付きコンテキストにします。
# Linux x86_64 向けのエクスポート出力をそのまま使用します。
FROM debian:bookworm-slim

# 自己完結型 .NET のネイティブ依存です。SDK や画面サーバーは導入しません。
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates libc6 libgcc-s1 libgssapi-krb5-2 libicu72 \
        libssl3 libstdc++6 tzdata zlib1g \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 10001 bayfront \
    && useradd --uid 10001 --gid 10001 --no-create-home \
        --home-dir /var/lib/bayfront --shell /usr/sbin/nologin bayfront \
    && mkdir -p /opt/bayfront /var/lib/bayfront /var/cache/bayfront \
    && chown 10001:10001 /var/lib/bayfront /var/cache/bayfront

# ----------------------------------------

WORKDIR /opt/bayfront
COPY --from=artifact / /opt/bayfront/
COPY run-server.sh /opt/bayfront/run-server.sh
RUN chmod 0755 /opt/bayfront/BayfrontServer.x86_64 /opt/bayfront/run-server.sh \
    && test -f /opt/bayfront/BayfrontServer.pck \
    && test -f /opt/bayfront/data_BayfrontSandbox_linuxbsd_x86_64/BayfrontSandbox.dll \
    && test -f /opt/bayfront/data_BayfrontSandbox_linuxbsd_x86_64/GodotSharp.dll

ENV BAYFRONT_BINARY=/opt/bayfront/BayfrontServer.x86_64 \
    BAYFRONT_PORT=24567 \
    BAYFRONT_SAVE_PATH=/var/lib/bayfront/real-bayfront.json \
    BAYFRONT_AUTOSAVE_SECONDS=30 \
    XDG_DATA_HOME=/var/lib/bayfront \
    XDG_CONFIG_HOME=/var/lib/bayfront/config \
    XDG_CACHE_HOME=/var/cache/bayfront
USER 10001:10001
EXPOSE 24567/udp
VOLUME ["/var/lib/bayfront"]
STOPSIGNAL SIGTERM
ENTRYPOINT ["/opt/bayfront/run-server.sh"]

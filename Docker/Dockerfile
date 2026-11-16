# Coral Archipelago - 24/7 Headless Linux Dedicated Server
# Build context should be project root (d:\D\GameS) after exporting Unity Linux Dedicated Server to ./Build/LinuxServer

FROM ubuntu:22.04

ENV DEBIAN_FRONTEND=noninteractive
ENV SAVE_DATA_PATH=/data/saves
ENV SERVER_PORT=7777

RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    libstdc++6 \
    zlib1g \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Copy compiled Unity Linux Dedicated Server build
COPY Build/LinuxServer/ /app/

RUN chmod +x /app/CoralServer.x86_64 && mkdir -p /data/saves/players

VOLUME ["/data/saves"]

EXPOSE 7777/udp

# -batchmode -nographics -dedicatedServer guarantees headless execution without GPU
ENTRYPOINT ["/app/CoralServer.x86_64", "-batchmode", "-nographics", "-dedicatedServer", "-logFile", "/dev/stdout"]

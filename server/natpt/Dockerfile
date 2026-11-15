# The punch-through server of the multiplayer feature (docs/deploy.md, "Punch-through server"): RakNet's NAT plugin, built from RakNet's public source (BSD licence).
# The game speaks the NAT message numbers of RakNet 4.04, whose source is not public; the last public source (4.081) numbers them one higher, so one line is changed.
# Build context: server/natpt. RakNet is cloned at a pinned commit, never vendored.
FROM debian:bookworm-slim AS build
RUN apt-get update && apt-get install -y --no-install-recommends clang libstdc++-12-dev git ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /src
RUN git clone https://github.com/facebookarchive/RakNet.git && git -C RakNet checkout 44ec8ec
# ID_NAT_PUNCHTHROUGH_REQUEST = 57 (4.04's value) shifts the NAT block down by one; the build fails if the line is not found
RUN sed -i 's/^\tID_NAT_PUNCHTHROUGH_REQUEST,/\tID_NAT_PUNCHTHROUGH_REQUEST = 57,/' RakNet/Source/MessageIdentifiers.h \
 && grep -q 'ID_NAT_PUNCHTHROUGH_REQUEST = 57,' RakNet/Source/MessageIdentifiers.h
COPY ptserver.cpp .
# the server needs neither the loggers (their name table does not match the changed numbers) nor ReplicaManager3 (clang rejects it)
RUN clang++ -std=gnu++11 -O2 -w -pthread -static-libstdc++ -static-libgcc -IRakNet/Source ptserver.cpp \
    $(ls RakNet/Source/*.cpp | grep -v -e Logger -e ReplicaManager3) -o ptserver

FROM debian:bookworm-slim
COPY --from=build /src/ptserver /usr/local/bin/ptserver
USER nobody
EXPOSE 10000/udp
ENTRYPOINT ["ptserver", "10000"]

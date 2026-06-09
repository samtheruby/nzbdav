# syntax=docker/dockerfile:1.4

# -------- Stage 1: Build frontend --------
FROM --platform=$BUILDPLATFORM node:alpine AS frontend-build

WORKDIR /frontend
COPY ./frontend ./

RUN npm install
RUN npm run build
RUN npm run build:server
RUN npm prune --omit=dev

# -------- Stage 2: Build backend --------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS backend-build

WORKDIR /backend
COPY ./backend ./

# Accept build-time architecture as ARG (e.g., x64 or arm64)
ARG TARGETARCH
RUN dotnet restore
RUN dotnet publish -c Release -r linux-musl-${TARGETARCH} -o ./publish

# -------- Stage 3: Fetch rclone binary --------
# Downloaded on the build platform (no emulation) and selected by target arch.
# Pinned for reproducibility; bump RCLONE_VER to update.
# NOTE: the arg is intentionally named RCLONE_VER, not RCLONE_VERSION. Docker
# exposes build args as env vars to RUN, and rclone reads RCLONE_<FLAG> env
# vars as flags, so RCLONE_VERSION would be parsed as the boolean --version
# flag and crash `rclone version`.
# NOTE: --links (used by the embedded mount in entrypoint/RcloneMountService)
# requires rclone >= 1.70.3.
FROM --platform=$BUILDPLATFORM alpine:3.21 AS rclone-build

ARG TARGETARCH
ARG RCLONE_VER=1.74.3

RUN apk add --no-cache curl unzip \
    && curl -fsSL "https://downloads.rclone.org/v${RCLONE_VER}/rclone-v${RCLONE_VER}-linux-${TARGETARCH}.zip" -o /tmp/rclone.zip \
    && unzip -q /tmp/rclone.zip -d /tmp \
    && mv "/tmp/rclone-v${RCLONE_VER}-linux-${TARGETARCH}/rclone" /usr/local/bin/rclone \
    && chmod 0755 /usr/local/bin/rclone \
    && /usr/local/bin/rclone version

# -------- Stage 4: Combined runtime image --------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine

# Label the image
ARG REPO_URL
LABEL org.opencontainers.image.source=${REPO_URL}

# Prepare environment
WORKDIR /app
RUN mkdir /config \
    && apk add --no-cache nodejs npm libc6-compat shadow su-exec bash curl tzdata fuse3

# Bundle rclone for the optional embedded mount (see entrypoint.sh / RCLONE_MOUNT).
# user_allow_other lets the mount be shared with other containers via --allow-other.
COPY --from=rclone-build /usr/local/bin/rclone /usr/local/bin/rclone
RUN touch /etc/fuse.conf \
    && grep -qxF user_allow_other /etc/fuse.conf || echo user_allow_other >> /etc/fuse.conf

# Copy frontend
COPY --from=frontend-build /frontend/node_modules ./frontend/node_modules
COPY --from=frontend-build /frontend/package.json ./frontend/package.json
COPY --from=frontend-build /frontend/dist-node/server.js ./frontend/dist-node/server.js
COPY --from=frontend-build /frontend/build ./frontend/build

# Copy backend
COPY --from=backend-build /backend/publish ./backend

# Entry and runtime setup
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# Set env variables
EXPOSE 3000
ARG NZBDAV_VERSION
ENV NZBDAV_VERSION=${NZBDAV_VERSION}
ENV NODE_ENV=production
ENV LOG_LEVEL=warning

CMD ["/entrypoint.sh"]

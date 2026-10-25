# syntax=docker/dockerfile:1.7
# Solidstudio OCPP Virtual Charge Point (Apache-2.0), built from a pinned commit for local development.
FROM node:22.23.3-alpine3.24

ARG VCP_COMMIT=69bc17c30ef89d4a96603a0be0cb10430744b651

ADD --checksum=sha256:af3842db01bdd19eeeb7a248b9e2153d18e3b98929900c0d0a4bd642006f4131 \
    https://github.com/solidstudiosh/ocpp-virtual-charge-point/archive/${VCP_COMMIT}.tar.gz /tmp/vcp.tar.gz

WORKDIR /app
RUN tar xzf /tmp/vcp.tar.gz --strip-components=1 \
    && rm /tmp/vcp.tar.gz \
    && npm ci --no-audit --no-fund \
    && chown -R node:node /app

COPY --chmod=755 run.sh /usr/local/bin/run-simulator

USER node
ENV ADMIN_PORT=9999
CMD ["run-simulator"]

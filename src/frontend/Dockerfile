# FlowDeck dashboard.
#
# Built from src/frontend, which is self-contained:
#
#     docker build -f src/frontend/Dockerfile src/frontend

# ---------------------------------------------------------------- build
FROM node:24-alpine AS build
WORKDIR /src

# npm ci before the sources, so editing a component does not re-download the
# dependency tree.
COPY package.json package-lock.json ./
RUN npm ci

COPY . .

RUN npm run build -- --configuration production

# -------------------------------------------------------------- runtime
FROM nginx:1.29-alpine AS runtime

# Angular emits a browser bundle and, for a server build, a server one. Only
# the browser half is served here.
COPY --from=build /src/dist/flowdeck-dashboard/browser /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf

EXPOSE 80

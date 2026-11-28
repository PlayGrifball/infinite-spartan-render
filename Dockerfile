FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json InfiniteSpartanRender.slnx ./
COPY src/InfiniteSpartanRender/InfiniteSpartanRender.csproj src/InfiniteSpartanRender/
RUN dotnet restore src/InfiniteSpartanRender/InfiniteSpartanRender.csproj
COPY src/ src/
RUN dotnet publish src/InfiniteSpartanRender/InfiniteSpartanRender.csproj -c Release -o /out --no-restore

# Ekur needs Blender 5.x; its manifest says 4.3+.
FROM debian:trixie-slim AS blender
ARG BLENDER_SHA256=8019580ee1b7262e505f4196a00237ccf743c88d205b38d34201510676e60b09
ARG EKUR_COMMIT=5d896f3b7bfa47a76afe4342ffefb39f110cf920
RUN apt-get update && apt-get install -y --no-install-recommends curl xz-utils ca-certificates git libxi6 libxxf86vm1 libxfixes3 libxrender1 libgl1 libxkbcommon0 libsm6 libegl1 libx11-6 libxext6 libdbus-1-3 \
    && rm -rf /var/lib/apt/lists/*
# download.blender.org is blocked from the cluster's network; this mirror carries the same release.
RUN curl -fsSL -o /tmp/blender.tar.xz https://mirror.clarkson.edu/blender/release/Blender5.0/blender-5.0.1-linux-x64.tar.xz \
    && echo "${BLENDER_SHA256}  /tmp/blender.tar.xz" | sha256sum -c - \
    && mkdir /opt/blender && tar -xJf /tmp/blender.tar.xz -C /opt/blender --strip-components=1 && rm /tmp/blender.tar.xz
RUN git clone https://github.com/TheHaloArchive/ekur /opt/ekur && git -C /opt/ekur checkout "${EKUR_COMMIT}" \
    && git -C /opt/ekur submodule update --init --recursive
RUN cd /opt/ekur/addon && HOME=/tmp/h /opt/blender/blender --command extension build --output-dir /tmp \
    && HOME=/opt/blenderhome /opt/blender/blender --command extension install-file -r user_default /tmp/ekur-*.zip
COPY blender/bake.py /tmp/bake.py
RUN HOME=/opt/blenderhome /opt/blender/blender -b --python /tmp/bake.py
# Enable Ekur once as spartan.py does, so the extensions' compat cache (.cache/compat.dat) is already current: otherwise
# the first enable in each container rewrites it, which fails on a read-only root. The cache keeps the manifest's mtime
# to the nanosecond, and copying into the later stages keeps only whole seconds, so round every mtime down first.
RUN find /opt/blenderhome -exec sh -c 'for f; do touch -h -m -d "@$(stat -c %Y "$f")" "$f"; done' _ {} +
RUN BLENDER_USER_RESOURCES=/opt/blenderhome/.config/blender/5.0 HOME=/tmp/h /opt/blender/blender -b --factory-startup \
    --python-expr 'import addon_utils; addon_utils.enable("bl_ext.user_default.ekur", default_set=True)' \
    && chmod -R a+rX /opt/blenderhome

# The in-cluster runner for the integration tests (--target runner; .github/workflows/runner-image.yml): GitHub's runner
# image plus the .NET SDK and the same Blender, Ekur extension and libraries as the service image, so a job renders
# for real from the data volume the service itself uses. No secrets, hooks or credentials of its own.
FROM ghcr.io/actions/actions-runner:2.338.2@sha256:6d0b2d80d80f143f77dcb676e50920100bbb547e39e883454d57f45ab8c31300 AS runner
USER root
RUN apt-get update && apt-get install -y --no-install-recommends git curl libxi6 libxxf86vm1 libxfixes3 libxrender1 libgl1 libxkbcommon0 libsm6 libegl1 libx11-6 libxext6 libdbus-1-3 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /usr/share/dotnet /usr/share/dotnet
COPY --from=blender /opt/blender /opt/blender
COPY --from=blender /opt/ekur/assets /opt/ekur/assets
COPY --from=blender /opt/blenderhome /opt/blenderhome
ENV DOTNET_ROOT=/usr/share/dotnet \
    PATH=/usr/share/dotnet:$PATH \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    BLENDER_USER_RESOURCES=/opt/blenderhome/.config/blender/5.0
USER runner

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends libxi6 libxxf86vm1 libxfixes3 libxrender1 libgl1 libxkbcommon0 libsm6 libegl1 libx11-6 libxext6 libdbus-1-3 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=blender /opt/blender /opt/blender
COPY --from=blender /opt/ekur/assets /opt/ekur/assets
COPY --from=blender /opt/blenderhome /opt/blenderhome
WORKDIR /app
COPY --from=build /out .
COPY blender/ blender/
# The extension lives in the HOME Blender is started with; the runner sets HOME to a work dir, so point
# Blender at the installed copy through its user-resources variable instead.
ENV BLENDER_USER_RESOURCES=/opt/blenderhome/.config/blender/5.0 \
    ASPNETCORE_URLS=http://+:8080
USER 1654
EXPOSE 8080
ENTRYPOINT ["dotnet", "InfiniteSpartanRender.dll"]

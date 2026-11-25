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
    && HOME=/opt/blenderhome /opt/blender/blender --command extension install-file -r user_default -e /tmp/ekur-*.zip
COPY blender/bake.py /tmp/bake.py
RUN HOME=/opt/blenderhome /opt/blender/blender -b --python /tmp/bake.py && chmod -R a+rX /opt/blenderhome

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

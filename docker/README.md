# The Maquettiste image

`mattjcowan/maquettiste` is static-site-hosting 0.2.0 with the editor site (the SPA plus the `_functions/` handlers) and the
Maquettiste engine packages baked in. On first boot the entrypoint deploys the bundled site to `maquettiste.localhost`; on a
newer image it redeploys it and prunes the old engine package (phase2-design.md §6).

## Run it on your repository (local mode)

    cd <your repository>
    MAQUETTISTE_IMAGE=mattjcowan/maquettiste:latest docker compose -f <maquettiste>/docker/compose.yaml --project-directory . up -d
    # open http://maquettiste.localhost:8080

The compose file binds `127.0.0.1:8080` only, mounts `./.maquettiste` as the site's data folder (the model) and `./` as the
output root (`/repo`), and keeps the host's own state in the `maquettiste-host` volume.

Stop with `docker compose -f <maquettiste>/docker/compose.yaml --project-directory . down` (add `-v` to drop the host volume;
the model and generated files stay in your repository).

Run `maquettiste init` before the first `up`: without `.maquettiste/`, Docker creates the mount folder itself (root-owned on
Linux) and the editor starts on an empty project.

## The CLI in the image

The image also carries the `maquettiste` command line (`/usr/local/bin/maquettiste`, the CLI in `/opt/maquettiste/cli` on the
image's .NET runtime). Given a command, the entrypoint runs it instead of the editor:

    docker run --rm --user "$(id -u):$(id -g)" -v "$PWD:/repo" -w /repo mattjcowan/maquettiste:latest maquettiste generate --check

`--user` keeps the files it writes yours on Linux (the CLI works under any UID). docs/user-guide.md "The command line" has the
commands and a shell function; docs/mcp.md has the `.mcp.json` entry that runs `maquettiste mcp` from the image.

## Never delete the site

Never delete or rename the site `maquettiste.localhost` in the host's management UI or API. Its data folder is your bind-mounted
`.maquettiste/`, and static-site-hosting 0.2.0 deletes a site folder recursively, model included. Recovery is
`git checkout -- .maquettiste`; uncommitted model edits are lost.

## File ownership on Linux

The container runs as UID 1654 (`app`). The two bind mounts must be writable by it without changing their owner. Grant your own
UID default entries too, or you cannot edit or delete the files and folders the editor creates (they are owned by 1654, mode 755):

    setfacl -R -m "u:1654:rwX,d:u:1654:rwX,u:$(id -u):rwX,d:u:$(id -u):rwX" .maquettiste <output roots, such as db src/Generated>

`setfacl` comes with the `acl` package. Without ACLs, remove container-owned files as root inside a container:

    docker run --rm --user 0 -v "$PWD:/w" --entrypoint rm mattjcowan/maquettiste:latest -rf /w/<path>

Docker Desktop on macOS and Windows maps ownership itself; no ACLs are needed there.

## Who is "local" (no sign-in)

A request is trusted as the local developer only when its `Host` is `localhost`, a loopback address or a `*.localhost` name, it carries no
forwarding header, and its peer address is loopback or listed in `MAQUETTISTE_LOCAL_PEERS`. When that variable is empty the
entrypoint sets it to the container's default gateway (read from `/proc/net/route`), which is where a browser's request arrives
on Linux Docker Engine (for example `172.18.0.1`; measured). The chosen value is logged at start:

    docker compose -f <maquettiste>/docker/compose.yaml --project-directory . logs | grep "local peers"

Anywhere else, such as Docker Desktop (macOS, Windows, or Linux with Desktop) or a rootless or proxied setup, the browser's
requests may arrive from a different address, and you get the sign-in page. The editor then logs the address once:

    docker compose ... logs | grep "not a local peer"
    # maquettiste: a request for the local editor came from 192.168.65.1, which is not a local peer ... set MAQUETTISTE_LOCAL_PEERS=192.168.65.1

Either set that address (a comma-separated list of IP addresses) and restart:

    MAQUETTISTE_LOCAL_PEERS=192.168.65.1 docker compose ... up -d

or set a token and sign in with it on the sign-in page:

    MAQUETTISTE_EDITOR_TOKEN=$(openssl rand -hex 24) docker compose ... up -d

Never use `*`: the editor ignores it (and any entry that is not an IP address) and logs a warning, because it would trust every
peer. The Docker Desktop address has not been measured yet (no Docker Desktop machine was available); `192.168.65.1` above is
only an example of the shape to expect.

## Build and test the image (maintainers)

    docker build -f docker/Dockerfile -t mattjcowan/maquettiste:dev .
    mkdir -p ../smoketmp && TMPDIR=$PWD/../smoketmp sh docker/smoke.sh      # offline checks; prints "smoke: all checks passed"
    docker/dev-billing.sh                                                  # the billing fixture on 127.0.0.1:8080; rerunnable

Files: `Dockerfile` (multi-stage: engine packages, site zip, final image), `entrypoint.sh` (first-boot deploy, key seeding,
local peers), `compose.yaml` (local mode), `pack-site.sh` and `placeholder/` (the site zip when the SPA's own packer is absent),
`closure/` (the offline NuGet closure the host builds `_functions/` from), `smoke.sh`, `dev-billing.sh`.

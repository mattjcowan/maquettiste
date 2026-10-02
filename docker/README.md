# The Maquettiste image

`mattjcowan/maquettiste` is static-site-hosting 0.2.0 with the editor site (the SPA plus the `_functions/` handlers) and the
Maquettiste engine packages baked in. On first boot the entrypoint deploys the bundled site to `maquettiste.localhost`; on a
newer image it redeploys it and prunes the old engine package (phase2-design.md §6).

## Run it on your repository (local mode)

    cd <your repository>
    MAQUETTISTE_IMAGE=mattjcowan/maquettiste:latest docker compose -f <maquettiste>/docker/compose.yaml --project-directory . up -d
    # open http://maquettiste.localhost:8080

The compose file binds `127.0.0.1:8080` only, mounts `./.maquettiste` as the site's data folder (the model) and `./` as the
output root (`/repo`), and keeps the host's own state in the `maquettiste-host` volume. It works unchanged under Docker and
under Podman (`podman compose` or `podman-compose` with the same arguments); see File ownership.

Stop with `docker compose -f <maquettiste>/docker/compose.yaml --project-directory . down` (add `-v` to drop the host volume;
the model and generated files stay in your repository).

Run `maquettiste init` before the first `up`: without `.maquettiste/`, Docker creates the mount folder itself (root-owned on
Linux) and the editor starts on an empty project.

The top bar shows the release and the workspace under the project name (`v0.5.3 · feature/billing`), so editors on other ports
can be told apart. The container sees only `/repo`, so it takes the workspace from `MAQUETTISTE_WORKSPACE` when set, else the
branch in `.git/HEAD`, else, for a linked worktree (whose `.git` file points at a git folder that is not mounted), the worktree's
name:

    MAQUETTISTE_WORKSPACE="$(git rev-parse --abbrev-ref HEAD)" MAQUETTISTE_PORT=8081 docker compose -f <maquettiste>/docker/compose.yaml --project-directory . -p billing up -d

## Which version is running

`GET /api/health` and `GET /api/project` report `productVersion` (the release, such as `0.5.3`) and `build` (in the image, the
per-build package version, such as `0.5.3-b14a8131cfe39`, also in `/opt/maquettiste/engine.version`); their `engineVersion` is the
engine contract that packs' `engine` ranges are checked against, not the release. The image carries the OCI labels
`org.opencontainers.image.title`, `.version` (the release), `.revision` (the commit it was built from), `.source` and `.licenses`:

    docker inspect -f '{{ index .Config.Labels "org.opencontainers.image.version" }}' mattjcowan/maquettiste:latest

## The CLI in the image

The image also carries the `maquettiste` command line (`/usr/local/bin/maquettiste`, the CLI in `/opt/maquettiste/cli` on the
image's .NET runtime). Given a command, the entrypoint runs it instead of the editor:

    docker run --rm --user 0:0 -v "$PWD:/repo" -w /repo mattjcowan/maquettiste:latest maquettiste generate --check

`--user 0:0` starts the container as root so the entrypoint can repair what another user left in the project and then run the
command as the owner of the mounted folder, which is you (File ownership); the same form works under Docker and under rootless
Podman. docs/user-guide.md "The command line" has the commands and a shell function. For agents,
`maquettiste init --mcp --docker mattjcowan/maquettiste:<tag>` registers `maquettiste mcp` in `.mcp.json` as a
`"type": "stdio"` server that runs `/bin/sh -c` with one line: it adds `/opt/homebrew/bin`, `/usr/local/bin` and
`$HOME/.docker/bin` to the `PATH` (a client started from the desktop on the Mac does not inherit the shell's), then
`exec docker run -i --rm --user 0:0 -v "$(pwd -P):/repo" ...` of the image, the same form (`--runtime podman` writes `podman`),
with the server's stderr appended to `.maquettiste/.cache/mcp.log`, which git ignores. `$(pwd -P)` is the real path of the
folder the client starts the server in, so a repository behind a symbolic link mounts too. The line also passes
`-e MAQUETTISTE_WORKSPACE="$(git symbolic-ref --short -q HEAD 2>/dev/null || basename "$(pwd -P)")"`: the branch read on the
host, else the folder's name, which `get_project` reports as `workspace`. On Windows, which has no `/bin/sh`, the entry is
`docker run -i --rm --user 0:0 -v ${PWD}:/repo ... -e MAQUETTISTE_WORKSPACE ...` itself: the client expands `${PWD}`, and the
variable is passed on when the client's environment sets it (otherwise the server reads the branch from `.git`). Nothing else is
written to the repository (docs/mcp.md).

## Never delete the site

Never delete or rename the site `maquettiste.localhost` in the host's management UI or API. Its data folder is your bind-mounted
`.maquettiste/`, and static-site-hosting 0.2.0 deletes a site folder recursively, model included. Recovery is
`git checkout -- .maquettiste`; uncommitted model edits are lost.

## File ownership

Start the container as root (the compose file's `user: "0:0"`, or `docker run --user 0:0`) and the entrypoint picks the user
that runs the editor or the command, makes the files Maquettiste touches belong to that user, and drops to it with `setpriv`.
The user is the owner of the mounted model folder (`.maquettiste/`) for the editor, or of the mounted working directory
(`-w /repo`) for a command, which under Docker on Linux and on the Mac is you; the entrypoint hands its own volume (`/data`) and
`/home/app` to that user, and with nothing mounted it runs as the image's user, UID 1654 (`app`). Repair means that every file
or folder that another user owns (root, or 1654 from an older image) inside the model folder, inside the output roots that
`outputs.allow` lists, and for a command in the files `init` writes at the project root (`.mcp.json`, the modeling skill;
the repository's `.gitignore` is the customer's file and is left alone) is given to that user, without following symbolic
links and without changing the mount point itself; the entrypoint logs `repaired N files owned by another user under <path>`
when it changed any, so a run as root never leaves you needing `sudo chown`. When Docker shows the model folder itself as root's (Docker created it because `init` had not run, or an
earlier run as root did), the editor takes the owner of the repository mount `/repo` instead and claims the folder as well;
with no such owner it stays root and logs how to set the variables. `MAQUETTISTE_UID` sets the user id to run as (`0` stays
root; empty means the folder's owner) and `MAQUETTISTE_GID` the group id (empty means the folder's group; `0` with another user
is refused); repair then works for the user they name. Under rootless Podman, root in the container is you outside it: the
entrypoint recognizes a rootless runtime from the container's user mapping (`/proc/self/uid_map` maps root to your uid, where
Docker maps it to root), stays root and repairs to root, which is you, so the same `--user 0:0` and the same compose file work
unchanged and `--userns=keep-id` is no longer needed. Files are written 0644 and folders 0755 (umask 022); only the host's
deploy key is 0600.

`docker/smoke.sh` checks the default run as 1654, a run with `MAQUETTISTE_UID` set to the host user (saves come out owned by
that user), the editor started as root over a host-owned model folder with root-owned and 1654-owned files in it and in an
output root (it runs as the host user, repairs them and saves 0644 files), the editor over a root-owned model folder in a
host-owned repository (it runs as the repository's owner), a command started as root over a host-owned folder with a
root-owned `.maquettiste/.cache` (it repairs it, runs as the host user and `generate --check` succeeds), and a run started as
root with no variables over a root-owned model folder and repository, as rootless Podman shows them (the editor stays root and
its saves succeed).

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

    docker build -f docker/Dockerfile --build-arg MAQUETTISTE_REVISION="$(git rev-parse HEAD)" -t mattjcowan/maquettiste:dev .
    mkdir -p ../smoketmp && TMPDIR=$PWD/../smoketmp sh docker/smoke.sh      # offline checks; prints "smoke: all checks passed"
    docker/dev-billing.sh                                                  # the billing fixture on 127.0.0.1:8080; rerunnable

Files: `Dockerfile` (multi-stage: engine packages, site zip, final image), `entrypoint.sh` (first-boot deploy, key seeding,
local peers), `compose.yaml` (local mode), `pack-site.sh` and `placeholder/` (the site zip when the SPA's own packer is absent),
`closure/` (the offline NuGet closure the host builds `_functions/` from), `smoke.sh`, `dev-billing.sh`.

Two build arguments fill the OCI labels: `MAQUETTISTE_VERSION` (`.version`; it defaults to the `VersionPrefix` of
`Directory.Build.props`, which a test keeps in step, and the publish workflow passes the tag's version) and `MAQUETTISTE_REVISION`
(`.revision`, the commit; empty unless given, as the workflows do).

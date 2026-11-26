# The Maquettiste image

`mattjcowan/maquettiste` is static-site-hosting 0.4.0 with the editor site (the SPA plus the `_functions/` handlers) and the
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

## The assistant's AI provider

The editor's assistant (docs/user-guide.md, "The assistant") uses the AI the host gives the site; Maquettiste never holds a key.
It is one container: the image is static-site-hosting with the editor site in it, and the host's admin app answers on the
same port under the host name `maquettiste-admin.localhost` (`http://maquettiste-admin.localhost:8080/`, or
`MAQUETTISTE_ADMIN_HOST`), while the editor answers under its own names (see "Host names" below).
Sign in as `admin` with the password the host generated on first boot (it asks for a new one at the first sign-in):

    docker compose -f <maquettiste>/docker/compose.yaml --project-directory . exec maquettiste cat /data/config/bootstrap-password.txt

Then add a provider and its key under **AI › providers**, and pick it and the model under **Sites ›
maquettiste.localhost › AI**. The compose file passes `MAQUETTISTE_HOST_AI_URL` (default `http://maquettiste-admin.localhost:${MAQUETTISTE_PORT:-8080}/`)
so Settings › Assistant links there.

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
`.maquettiste/`, and static-site-hosting deletes a site folder recursively, model included. Recovery is
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

## Signing in

Without `MAQUETTISTE_EDITOR_TOKEN` nobody signs in: every request is the local developer, whatever address it comes from. The
compose file publishes the port on `127.0.0.1` only, so other machines cannot reach it.

With a token set, everyone signs in, the local developer included: the browser shows the sign-in page, and scripts send
`Authorization: Bearer <token>`. Set one when the editor is reachable from other machines:

    MAQUETTISTE_EDITOR_TOKEN=$(openssl rand -hex 24) docker compose ... up -d

## Host names

The host inside the image picks the app by the request's host name, so the names are settings:

| Setting | Default | What it does |
| --- | --- | --- |
| `MAQUETTISTE_EDITOR_HOST` | `localhost,127.0.0.1` | More names the editor answers at, comma-separated. It always answers at `maquettiste.localhost` too. A name that is not local (`localhost`, `*.localhost`, `127.x.x.x`) is public: the editor then refuses to start without `MAQUETTISTE_EDITOR_TOKEN` |
| `MAQUETTISTE_ADMIN_HOST` | `maquettiste-admin.localhost` | Where the host's admin app answers (AI providers, users). One name |
| `MAQUETTISTE_TRUST_PROXY` | `false` | `true` makes the host use the `X-Forwarded-Host`, `-Proto` and `-For` headers a reverse proxy sends. Set it only when the proxy is the only way in: anyone else could send those headers too |

Any other name never reaches the editor: the host answers it with a 404. That is also what stops DNS rebinding, a web page
on another name that points its name at your machine, while the editor runs without a token.

Several repositories on one machine: one container each, on its own port (`MAQUETTISTE_PORT=8081`, `8082`...), each opened at
`http://localhost:<port>`.

Behind a reverse proxy (nginx, Caddy, Traefik), with the proxy ending HTTPS:

    MAQUETTISTE_EDITOR_HOST=localhost,models.example.com MAQUETTISTE_TRUST_PROXY=true \
    MAQUETTISTE_EDITOR_TOKEN=$(openssl rand -hex 24) docker compose ... up -d

The proxy forwards to the container's port and sends the usual `X-Forwarded-*` headers. Publish the port only to the proxy.

## Build and test the image (maintainers)

    docker build -f docker/Dockerfile --build-arg MAQUETTISTE_REVISION="$(git rev-parse HEAD)" -t mattjcowan/maquettiste:dev .
    mkdir -p ../smoketmp && TMPDIR=$PWD/../smoketmp sh docker/smoke.sh      # offline checks; prints "smoke: all checks passed"
    docker/dev-billing.sh                                                  # the billing fixture on 127.0.0.1:8080; rerunnable

Files: `Dockerfile` (multi-stage: engine packages, site zip, final image), `entrypoint.sh` (first-boot deploy, key seeding), `compose.yaml` (local mode), `closure/` (the offline NuGet closure the host builds `_functions/` from), `smoke.sh`,
`dev-billing.sh`.

Two build arguments fill the OCI labels: `MAQUETTISTE_VERSION` (`.version`; it defaults to the `VersionPrefix` of
`Directory.Build.props`, which a test keeps in step, and the publish workflow passes the tag's version) and `MAQUETTISTE_REVISION`
(`.revision`, the commit; empty unless given, as the workflows do).

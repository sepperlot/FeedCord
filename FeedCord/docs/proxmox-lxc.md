# Running FeedCord in a Proxmox LXC Container

FeedCord runs great inside a lightweight Proxmox LXC container. Since FeedCord ships as a Docker image, all you need is an LXC with Docker (or Podman) installed - everything else (`compose.yml`, `appsettings.json`) works exactly as it does on any other Linux host. This guide walks through creating that LXC from scratch.

## 1. Create the LXC

Docker requires nesting to run inside an unprivileged LXC container, so make sure to enable that feature at creation time.

From the Proxmox shell:

```bash
pct create 200 local:vztmpl/debian-13-standard_13.6-1_amd64.tar.zst \
  --hostname feedcord \
  --cores 1 \
  --memory 512 \
  --swap 512 \
  --rootfs local-lvm:4 \
  --net0 name=eth0,bridge=vmbr0,ip=dhcp \
  --unprivileged 1 \
  --features nesting=1 \
  --onboot 1
```

Adjust the CTID, storage pool, and network bridge to match your environment. FeedCord is very lightweight - 512MB RAM and 4GB disk is comfortable headroom for the container plus the Docker runtime.

Start and enter the container:

```bash
pct start 200
pct enter 200
```

## 2. Install Docker inside the LXC

```bash
apt update && apt install -y ca-certificates curl gnupg
install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/debian/gpg -o /etc/apt/keyrings/docker.asc
chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/debian $(. /etc/os-release && echo $VERSION_CODENAME) stable" | tee /etc/apt/sources.list.d/docker.list > /dev/null
apt update
apt install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin
```

## 3. Set up FeedCord

Follow the main [Quick Setup](../../README.md#quick-setup) guide to create your `appsettings.json`, then use a Compose file instead of a bare `docker run`:

```bash
mkdir -p /opt/feedcord
cd /opt/feedcord
vi appsettings.json   # paste your config
touch feed_dump.csv     # see note below
vi compose.yml
```

```yaml
services:
  feedcord:
    image: sepperlot/feedcord:latest
    container_name: FeedCord
    restart: unless-stopped
    volumes:
      - /opt/feedcord/appsettings.json:/app/config/appsettings.json
      - /opt/feedcord/feed_dump.csv:/app/feed_dump.csv
```

> [!TIP]
> If `feed_dump.csv` doesn't already exist on the host when you bind-mount it, Docker creates a *directory* at that path instead of a file — which breaks the app. Pre-creating an empty file with `touch` avoids that.
<!-- fix MD028 -->
> [!NOTE]
> `feed_dump.csv` tracks the last-seen post per feed (with `PersistenceOnShutdown: true` in your config), so FeedCord knows where it left off across restarts instead of treating every post as new on first boot. Without this volume mount, the file lives only in the container's writable layer and gets wiped by `docker compose up -d --force-recreate`, image updates, or a host reboot — silently resetting every feed's watermark and potentially causing missed posts (published while the container was down) to never get sent to Discord.
>
> This fork publishes its own image to Docker Hub at [`sepperlot/feedcord`](https://hub.docker.com/r/sepperlot/feedcord). If you're running upstream FeedCord instead, use `qolors/feedcord:latest`.

```bash
docker compose up -d
docker logs -f FeedCord
```

## 4. (Optional) Run Docker without sudo

If you'd rather manage the container as a non-root user inside the LXC:

```bash
usermod -aG docker <username>
```

Log out and back in (or run `newgrp docker`) for the group membership to take effect. Note that membership in the `docker` group is effectively root-equivalent within that container, since the Docker daemon runs as root - fine for a single-purpose LXC like this one, but worth knowing if you share the container with other users.

## Notes

- No LXC- or Proxmox-specific configuration is required beyond enabling `nesting=1` at container creation - Docker inside the LXC behaves identically to Docker on any bare-metal or VM host.
- Take a Proxmox snapshot of the container once it's running cleanly; it's a trivial rollback point if a future config or image change breaks something.
- Editing/adding feeds later only requires updating `appsettings.json` and restarting the container (`docker compose up -d --force-recreate` if you've rebuilt a custom image, or a plain `docker compose restart` if only the mounted config changed).

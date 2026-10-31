# A fresh offline host for the installer's end-to-end test (tools/offline-e2e/run.sh): Debian with
# systemd and what a server has, nothing of the stack: no Docker, no NVIDIA toolkit. Built once, with
# the network; the test runs it with none.
FROM public.ecr.aws/docker/library/debian:trixie-slim
ENV container=docker DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
 && apt-get install -y --no-install-recommends systemd systemd-sysv dbus iproute2 iptables kmod procps \
      ca-certificates curl openssl sudo util-linux tar gzip xz-utils less \
 && apt-get clean && rm -rf /var/lib/apt/lists/* \
 && systemctl mask systemd-udevd.service systemd-udev-trigger.service systemd-logind.service \
      getty.target console-getty.service systemd-firstboot.service systemd-resolved.service \
 && rm -f /etc/machine-id && : > /etc/machine-id
STOPSIGNAL SIGRTMIN+3
CMD ["/sbin/init"]

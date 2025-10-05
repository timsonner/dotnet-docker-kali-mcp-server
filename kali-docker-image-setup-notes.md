
# Kali docker image setup

### Start container with TUN device enabled for using a VPN
```bash
docker run -d --name kali-mcp-persistent --cap-add=NET_ADMIN --device=/dev/net/tun kalilinux/kali-rolling sleep infinity
```

### Copy .ovpn file to docker container
```bash
docker cp ~/Downloads/timsonner.ovpn kali-mcp-persistent:/root/timsonner.ovpn
```

### Connect to docker container using bash
```bash
docker exec -it kali-mcp-persistent bash
# once zsh is installed, use: docker exec -it kali-mcp-persistent zsh
```

### Global var to get rid of apt errors
```bash
DEBIAN_FRONTEND=noninteractive
```

### Programs to install using apt
```bash
apt update && apt install -y \
	zsh git netexec nmap netcat-openbsd openvpn python3-pip \
	python3-setuptools pipx iputils-ping seclists wordlists \
	enum4linux-ng ruby-full
```

### Install Impacket
```bash
python3 -m pipx install impacket
```

### Set pipx path
```bash
pipx ensurepath
```

### Install Evil-WinRM
```bash
gem install evil-winrm
```
# Ubuntu 24.04 VM Installation and Operations Guide

This guide covers a native Ubuntu 24.04 x86-64 virtual machine. Unlike WSL, Ubuntu sees a passed-through USB reader directly; `usbipd` is not used.

## 1. VM requirements

Recommended minimums for development and commissioning:

- Ubuntu Server or Desktop 24.04 LTS, x86-64
- 2 virtual CPUs, 4 GB RAM, and 25 GB disk
- A bridged network adapter when using a TCP reader
- USB 2.0/3.0 controller and USB-device passthrough when using a serial reader
- Correct VM time and timezone

After installation:

```bash
sudo timedatectl set-timezone Asia/Jakarta
sudo apt-get update
sudo apt-get upgrade -y
sudo reboot
```

Take a VM snapshot before reader commissioning. Do not attach the USB serial adapter to both the host and VM simultaneously.

## 2. Install build and runtime dependencies

```bash
sudo apt-get update
sudo apt-get install -y \
  dotnet-sdk-10.0 aspnetcore-runtime-8.0 \
  build-essential git postgresql postgresql-client \
  curl jq valgrind strace lsof patchelf usbutils
sudo systemctl enable --now postgresql
dotnet --info
```

The application targets .NET 8. The .NET 10 SDK builds the repository's `.slnx` solution, while the .NET 8 and ASP.NET Core 8 runtimes execute the tests and development processes. Installing the .NET 10 SDK alone is not sufficient because .NET does not roll framework-dependent applications backward from runtime 10 to runtime 8. The generated Debian package is self-contained, so a target VM running only the package does not require a separate .NET runtime.

In the `dotnet --info` output, verify that both `Microsoft.NETCore.App 8.0.x` and `Microsoft.AspNetCore.App 8.0.x` appear under `.NET runtimes installed`.

If `dotnet-sdk-10.0` is unavailable in the configured Ubuntu repositories, install the Microsoft Ubuntu 24.04 package feed, then repeat the install. Do not replace `global.json` with a different SDK version.

## 3. Obtain and prepare the repository

Clone or copy the repository onto the VM's Linux filesystem, not a shared Windows folder. For example:

```bash
git clone 'REPLACE_WITH_YOUR_REPOSITORY_URL' ~/rrligthing-rfid-middleware
cd ~/rrligthing-rfid-middleware
cp .env.example .env
set -a
source .env
set +a
```

Replace `REPLACE_WITH_YOUR_REPOSITORY_URL` with the real repository URL. Keep `.env` private and never commit credentials.

## 4. Configure PostgreSQL for development

Create the local development role and databases:

```bash
sudo -u postgres createuser --login rfid
sudo -u postgres psql -c "ALTER ROLE rfid WITH PASSWORD 'rfid-dev-password';"
sudo -u postgres createdb -O rfid rfid_backend
sudo -u postgres createdb -O rfid rfid_edge
```

If an item already exists, skip its creation. Verify both databases:

```bash
PGPASSWORD=rfid-dev-password psql -h 127.0.0.1 -U rfid -d rfid_edge -c "select current_database(), current_user;"
PGPASSWORD=rfid-dev-password psql -h 127.0.0.1 -U rfid -d rfid_backend -c "select current_database(), current_user;"
```

The `rfid_edge` database contains the middleware's cached authorization state and local-only scan audit. The `rfid_backend` database supports the development backend. Production credentials must be different and stronger.

## 5. Build and run all tests

From the repository root:

```bash
set -a
source .env
set +a
export RFID_TEST_POSTGRES='Host=127.0.0.1;Port=5432;Database=rfid_edge;Username=rfid;Password=rfid-dev-password'
bash scripts/build.sh
```

Then run the native SDK lifecycle and memory checks:

```bash
LD_LIBRARY_PATH="$PWD/tests/RfidMiddleware.NativeTests/bin/Release/net8.0/native" \
  dotnet tests/RfidMiddleware.NativeTests/bin/Release/net8.0/RfidMiddleware.NativeTests.dll 100
bash scripts/native-stability.sh 100
```

Unlike WSL, native Ubuntu is the intended environment for the Valgrind wrapper. Investigate any native memory error before deploying.

## 6. First run with simulated hardware

Open three terminals. In each terminal:

```bash
cd ~/rrligthing-rfid-middleware
set -a
source .env
set +a
```

Terminal 1 starts the development backend:

```bash
dotnet run --project tools/RfidMiddleware.MockServer -c Release -- --reset --seed-count 100
```

Terminal 2 starts the middleware with the safe fake reader:

```bash
export RFID_ReaderMode=ManagedFake
dotnet run --project src/RfidMiddleware.Host -c Release -- run --config appsettings.json
```

Terminal 3 verifies operation:

```bash
dotnet run --project src/RfidMiddleware.Host -- status --config appsettings.json
dotnet run --project src/RfidMiddleware.Host -- reader test --config appsettings.json
dotnet run --project src/RfidMiddleware.Host -- reader watch --duration 5 --config appsettings.json
dotnet run --project src/RfidMiddleware.Host -- manifest status --config appsettings.json
dotnet run --project src/RfidMiddleware.Host -- audit stats --config appsettings.json
dotnet run --project src/RfidMiddleware.Host -- db verify --config appsettings.json
```

The backend listens on `http://127.0.0.1:5080`. The middleware polls it every five seconds. Scans remain in PostgreSQL and are not uploaded.

## 7. Connect a USB serial reader to the VM

Stop the simulated middleware with `Ctrl+C`.

1. In the VM application (Hyper-V, VMware, VirtualBox, or another hypervisor), select the physical USB-to-serial device and attach it to the Ubuntu VM.
2. Confirm that Ubuntu detects it:

```bash
lsusb
sudo dmesg | tail -n 40
ls -l /dev/ttyUSB* /dev/ttyACM* 2>/dev/null
ls -l /dev/serial/by-id/ 2>/dev/null
```

3. Add the operator to the serial-device group, then log out and back in:

```bash
sudo usermod -aG dialout "$USER"
```

4. Verify access after logging in again:

```bash
id
test -r /dev/ttyUSB0 && test -w /dev/ttyUSB0 && echo "serial access OK"
lsof /dev/ttyUSB0
```

Prefer the stable `/dev/serial/by-id/...` name shown by the VM. Use `/dev/ttyUSB0` only as a temporary fallback.

Start the real reader from the repository root:

```bash
set -a
source .env
set +a
export RFID_ReaderMode=Vendor
export RFID_VendorReader__Transport=Serial
export RFID_VendorReader__SerialDevice='/dev/serial/by-id/REPLACE_WITH_YOUR_READER_ID'
export RFID_VendorReader__Baud=115200
export RFID_VendorReader__Rs485Address=0
export RFID_NATIVE_DIR="$PWD/linux/x86_64/release"
export LD_LIBRARY_PATH="$PWD/linux/x86_64/release"
dotnet run --project src/RfidMiddleware.Host -c Release -- run --config appsettings.json
```

Replace `REPLACE_WITH_YOUR_READER_ID` with the exact filename listed on the VM. If the reader does not connect, confirm that the host released the USB device, no other process owns the port, the baud rate is 115200, and the RS-485 address is 0.

### Commission the reader over BAFO before permanent LAN use

Keep Ethernet and BAFO connected during commissioning. Stop middleware so it releases the exclusive reader lock. Read the current settings first:

```bash
export RFID_VendorReader__Transport=Serial
export RFID_VendorReader__SerialDevice='/dev/serial/by-id/REPLACE_WITH_BAFO_ID'
export RFID_VendorReader__Baud=115200
export RFID_VendorReader__Rs485Address=0

dotnet run --project src/RfidMiddleware.Host -c Release -- \
  reader network show --json --config src/RfidMiddleware.Host/appsettings.json
```

Preview a network change without writing:

```bash
dotnet run --project src/RfidMiddleware.Host -c Release -- \
  reader network configure \
  --ip 192.168.10.20 --mask 255.255.255.0 --gateway 192.168.10.1 \
  --port 1969 --dns 192.168.10.2 \
  --config src/RfidMiddleware.Host/appsettings.json
```

Repeat with `--apply` only after review. Apply mode backs up the old values, writes, performs an exact serial read-back, and reboots only on a match. Verify TCP, power-cycle, and verify again:

```bash
dotnet run --project src/RfidMiddleware.Host -c Release -- \
  reader network verify --address 192.168.10.20 --port 1969 \
  --config src/RfidMiddleware.Host/appsettings.json
```

Remove BAFO after persistence is proven. The reader keeps a fixed private address; the Ubuntu server may use DHCP because middleware initiates the TCP connection. Never publish port 1969 publicly or through Docker. For more than 100 metres of copper Ethernet, add a switch or use fiber/media converters. If verification fails, keep BAFO connected and restore the saved configuration before leaving.

## 8. Connect a TCP reader

Configure the VM network adapter in bridged mode or otherwise provide a route to the reader subnet. Confirm connectivity inside Ubuntu:

```bash
ip address
ip route
ping -c 3 192.168.1.223
timeout 3 bash -c '</dev/tcp/192.168.1.223/1969' && echo "reader port reachable"
```

Then start the middleware:

```bash
set -a
source .env
set +a
export RFID_ReaderMode=Vendor
export RFID_VendorReader__Transport=Tcp
export RFID_VendorReader__Address=192.168.1.223
export RFID_VendorReader__Port=1969
export RFID_NATIVE_DIR="$PWD/linux/x86_64/release"
export LD_LIBRARY_PATH="$PWD/linux/x86_64/release"
dotnet run --project src/RfidMiddleware.Host -c Release -- run --config appsettings.json
```

Change the IP and port only if the reader is configured differently.

## 9. Validate the real reader and alarm

In another terminal, load `.env` and run:

```bash
dotnet run --project src/RfidMiddleware.Host -- reader test --config appsettings.json
dotnet run --project src/RfidMiddleware.Host -- reader watch --duration 10 --config appsettings.json
dotnet run --project src/RfidMiddleware.Host -- manifest status --config appsettings.json
dotnet run --project src/RfidMiddleware.Host -- status --config appsettings.json
```

Temporary hardware-setup mode uses the reader's built-in buzzer and digital outputs 0 and 1 for `AlarmPulseMilliseconds` (1000 ms by default). Every channel is attempted independently. An unconfigured output can fail without blocking the buzzer, the other output, scan auditing, or continued inventory, but alarm health remains critical.

Physical test order:

1. Confirm the expected active manifest version.
2. Scan an authorized EPC and verify no output-0 pulse.
3. Scan an unauthorized EPC and verify output 0 pulses.
4. Scan the same unauthorized EPC repeatedly and verify the three-second per-EPC cooldown.
5. Disconnect the backend and confirm an unexpired cached manifest still works.
6. Stop PostgreSQL or disconnect the reader and confirm health becomes critical without a fault-only alarm pulse.
7. Complete the batch and confirm subsequent scans alarm while the middleware reports healthy intentional idle.

The current physical acceptance manifest must contain only:

```text
E28069950000400E45B4F53E
E280689400005010BF75955B
```

Authorize them through the mobile endpoint before expecting a silent scan. Mobile API examples are in the WSL guide and work unchanged on Ubuntu.

During the October 19, 2026 COM11 test, both EPCs above were repeatedly audited as authorized. Nearby EPC `E280689400004010BF78D96A` was audited as unauthorized and respected the three-second alarm cooldown. Output 0 returned `-3034`; setup mode now also attempts the built-in buzzer and output 1 so the hardware team can configure all channels.

## 10. Build and install the Debian service

Build a self-contained package on the Ubuntu VM:

```bash
bash scripts/build-deb.sh 0.1.0
sudo apt-get install -y ./artifacts/rfid-middleware_0.1.0_amd64.deb
```

Create production PostgreSQL credentials and the `rfid_edge` database. Then configure:

```bash
sudo cp /etc/rfid-middleware/environment.example /etc/rfid-middleware/environment
sudoedit /etc/rfid-middleware/environment
sudoedit /etc/rfid-middleware/appsettings.json
sudo chown root:rfid-middleware /etc/rfid-middleware/environment
sudo chmod 0640 /etc/rfid-middleware/environment
```

Required changes:

- Set `RFID_API_TOKEN` to the middleware token issued by the backend.
- Set `RFID_DatabaseConnectionString` to the production `rfid_edge` connection.
- Set `Id` to the middleware ID registered by the backend.
- Set `Server.BaseUrl` to the HTTPS backend address.
- Select `Serial` or `Tcp` and set its device/address values.
- Keep `AlarmMode=BuzzerAndDigitalOutputs` during hardware setup. After acceptance, select the final required channel and retain the commissioned pulse duration.

For a serial reader, confirm the service account is in `dialout`:

```bash
getent group dialout
sudo usermod -aG dialout rfid-middleware
```

Enable and start the service:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now rfid-middleware
sudo systemctl status rfid-middleware --no-pager
sudo journalctl -u rfid-middleware -n 100 --no-pager
```

Run the installed operational commands as the service user so they can access the restricted control socket:

```bash
sudo -u rfid-middleware /usr/lib/rfid-middleware/rfid-middleware status --config /etc/rfid-middleware/appsettings.json
sudo -u rfid-middleware /usr/lib/rfid-middleware/rfid-middleware reader test --config /etc/rfid-middleware/appsettings.json
sudo -u rfid-middleware /usr/lib/rfid-middleware/rfid-middleware manifest status --config /etc/rfid-middleware/appsettings.json
sudo -u rfid-middleware /usr/lib/rfid-middleware/rfid-middleware audit stats --config /etc/rfid-middleware/appsettings.json
```

## 11. Restart, stop, update, and inspect

```bash
sudo systemctl restart rfid-middleware
sudo systemctl stop rfid-middleware
sudo systemctl start rfid-middleware
sudo journalctl -u rfid-middleware -f
```

Before an update, back up `/etc/rfid-middleware` and PostgreSQL. Build the higher version and install it:

```bash
bash scripts/build-deb.sh 0.1.1
sudo apt-get install -y ./artifacts/rfid-middleware_0.1.1_amd64.deb
sudo systemctl restart rfid-middleware
```

The package configuration lives outside the application directory. Verify the service, manifest version, reader, authorized pass, and unauthorized output-0 pulse after every update. Retain the previously tested `.deb` so it can be reinstalled if rollback is needed.

## 12. Troubleshooting

Reader library:

```bash
file linux/x86_64/release/librfid.so
ldd linux/x86_64/release/librfid.so
```

Serial device:

```bash
lsusb
sudo dmesg | tail -n 40
ls -l /dev/serial/by-id/ /dev/ttyUSB* 2>/dev/null
lsof /dev/ttyUSB0
```

Database and backend:

```bash
sudo systemctl status postgresql --no-pager
PGPASSWORD='REPLACE_WITH_PASSWORD' psql -h 127.0.0.1 -U REPLACE_WITH_USER -d rfid_edge -c 'select now();'
curl -fsS 'https://REPLACE_WITH_BACKEND_HOST/healthz' | jq
```

Service diagnostics:

```bash
sudo journalctl -u rfid-middleware --since '30 minutes ago' --no-pager
sudo -u rfid-middleware /usr/lib/rfid-middleware/rfid-middleware db verify --config /etc/rfid-middleware/appsettings.json
sudo -u rfid-middleware /usr/lib/rfid-middleware/rfid-middleware diagnostics bundle --output /var/lib/rfid-middleware/diagnostics.json --config /etc/rfid-middleware/appsettings.json
```

A fresh installation with no known manifest is intentionally critical. A completed batch is healthy and intentionally idle, but any observed tag alarms until a new active manifest is assigned. During a backend outage, an unexpired cached manifest remains usable; after expiry, scans fail closed and alarm.

If health is critical with alarm error `-3034`, inspect the listed output number. Confirm that output 0 or 1 is enabled/configured by the vendor tool and wired correctly. Setup mode continues attempting the buzzer and the other output.

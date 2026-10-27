# Linux packaging

A minimal desktop entry is provided for manual installation. After publishing
for `linux-x64` or `linux-arm64`, install it under `/opt/airpdf` (or another
prefix) and place the launchers on `PATH`:

```bash
sudo install -d /opt/airpdf
sudo cp -r publish/linux-x64/* /opt/airpdf/
sudo ln -sf /opt/airpdf/AirPdf /usr/local/bin/airpdf
sudo install -Dm644 packaging/linux/airpdf.desktop \
  /usr/share/applications/airpdf.desktop
sudo install -Dm644 src/AirPdf.App/Assets/airpdf.png \
  /usr/share/icons/hicolor/1024x1024/apps/airpdf.png
```

`airpdf %f` in the desktop entry means "open the selected PDF". A `.deb`,
Flatpak, or AppImage is not produced yet; contributions are welcome.

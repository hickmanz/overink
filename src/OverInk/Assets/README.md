# OverInk app icons

Drop your files here (exact names):

| File | Purpose |
|------|---------|
| **`app.ico`** | Windows app icon (Explorer, taskbar, Alt+Tab). Use a multi-size ICO (16, 32, 48, 256 px). |
| **`app.png`** | Optional. Toolbar mascot & other UI (256×256 or 512×512 recommended). |

If `app.ico` is missing, the build still works and the tray uses the built-in squid drawing.
If `app.png` is missing, the toolbar uses the same built-in squid.

Convert PNG → ICO: [icoconvert.com](https://icoconvert.com) or ImageMagick:  
`magick app.png -define icon:auto-resize=256,128,64,48,32,16 app.ico`

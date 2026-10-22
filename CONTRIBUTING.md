# Contributing to MauseRecorder

Thanks for wanting to make MauseRecorder better! 🖱️

## Ways to help

- **Report a bug** — open an issue with your Windows version, what you did, what you expected, and what happened.
- **Suggest a feature** — fun ideas ("приколюхи") are very welcome.
- **Send a pull request** — fixes, polish, new fun modes, better rendering.
- **Share it** — stars, screenshots and shout-outs genuinely help.

## Development setup

1. Install the [.NET SDK 10](https://dotnet.microsoft.com/download) on Windows 10/11.
2. Clone and run:

   ```powershell
   git clone https://github.com/VolletG/MauseRecorder.git
   cd MauseRecorder
   dotnet run
   ```

3. Build with zero warnings before opening a PR:

   ```powershell
   dotnet build -v:minimal
   ```

## Project conventions

- **No external NuGet dependencies.** The app targets `net10.0-windows` with WPF only, so it can stay a single portable file.
- Keep the dark theme in `Themes/DarkTheme.xaml`; use the accent resources (`AccentBrush`, `AccentFaintBrush`, …) instead of hard-coded colours.
- Follow the existing naming and file layout (`Services/`, `Models/`, `Controls/`, `Windows/`).
- When you touch an asset or `.ico`, run `dotnet clean` before rebuilding — WPF caches resources aggressively.

## Code style

- C# with nullable reference types enabled and implicit usings.
- Prefer small, focused methods and clear naming over clever one-liners.
- Be careful with any code that runs on a low-level hook: keep it allocation-light and never block.

## Screenshots

If your change affects the UI, please update the images in `docs/screenshots/` and, if relevant, the banner (`powershell -File Assets/generate-banner.ps1`).

## Pull request checklist

- [ ] `dotnet build -v:minimal` passes with no warnings or errors.
- [ ] The app launches and both record & replay still work.
- [ ] No new third-party dependencies.
- [ ] Screenshots updated if the UI changed.

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).

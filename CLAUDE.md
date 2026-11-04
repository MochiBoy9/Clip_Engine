# Notes for working on Clip Engine

- `README.md`, `server/README.md` and `website/README.md` are written for the community (players and the curious). Keep them about what Clip Engine does and how to use it. Build, deploy, release and operator details go in `docs/DEVELOPMENT.md`.
- Never put operator-only tools, admin tokens, license keys or the key maker in the repository or on the website. The key maker is kept privately by the operator.
- Releases: see "Releasing" in `docs/DEVELOPMENT.md`. The version lives in `CMakeLists.txt` and `ui/ClipEngine.Desktop/ClipEngine.Desktop.csproj`; notes in `.github/release-notes.md`.
- Tests: `cd server && npm test`, `dotnet test ui/ClipEngine.Desktop.Tests`; the engine's C++ tests run on Windows CI.

# Third-party notices

Original code, UI assets and documentation derive from Pheeeeenom/PS3HDDTool.
Its full license is included unchanged as LICENSE.md; see NOTICE.md for authors.

Build/runtime dependencies include Microsoft .NET 10, Avalonia 11.1.0,
CommunityToolkit.Mvvm 8.2.2, Avalonia's Inter fonts, SkiaSharp/HarfBuzz native
components, the Tmds.DBus.Protocol 0.21.3 security backport and their transitive dependencies. These retain their own licenses;
the project's combined license does not replace third-party terms.

Binary packages include dependency inventory and available package/runtime license
and third-party notice files under third-party-licenses/. See that inventory for
resolved versions, license expressions and source repositories. Publicly available
SDK/NuGet tooling is used to build; source and build script accompany the release.
No Sony firmware, console keys or game content is bundled.

Canonical Avalonia/MicroCom/Tmds MIT notices and the Inter SIL Open Font License
are retained under licenses/, with upstream URLs and blob IDs in sources.json.
Avalonia.BuildServices is a build-time NuGet task with no license metadata in the
restored package; its DLLs are not redistributed in the application ZIPs. Its
presence in the dependency inventory is an identification record, not a relicensing.

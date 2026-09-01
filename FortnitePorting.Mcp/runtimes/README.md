# `CUE4Parse-Natives.dll`

`CUE4Parse-Natives.dll` is the unmanaged half of CUE4Parse. It provides the ACL
(Animation Compression Library) decoder plus the Oodle/ZLib shims. Practically every
Fortnite `UAnimSequence` is ACL-compressed, so **without this DLL every animation export
silently produces no `.ueanim`** - `AnimConverter.ConvertSequence` throws
`DllNotFoundException` and `ExportContext` (in the shared `FortnitePorting.Exporting`
project) downgrades it to a warning.

## Origin of the committed binary

* **File:** `CUE4Parse-Natives.dll`, 40,448 bytes, x64.
* **Extracted from:** the official **FortnitePorting v4.3.2** self-contained Windows build,
  from its single-file extraction directory
  (`%LOCALAPPDATA%\Temp\.net\FortnitePorting\Q9rQouotq5hF\CUE4Parse-Natives.dll`).
* **Why that source:** the official release is built from the *same* CUE4Parse fork this
  repository vendors under `Dependencies/CUE4Parse`, so the native ABI matches the managed
  `CUE4Parse.ACL.ACLNative` P/Invoke declarations exactly.

## Rebuilding it from source instead

The DLL is produced by the `Build-Natives` MSBuild target in
`Dependencies/CUE4Parse/CUE4Parse/CUE4Parse.csproj`, which shells out to CMake against
`Dependencies/CUE4Parse/CUE4Parse-Natives`:

```powershell
cd Dependencies\CUE4Parse\CUE4Parse-Natives
cmake -B builddir -A x64
cmake --build builddir --config Release
# -> builddir\Release\CUE4Parse-Natives.dll
```

That target requires CMake plus the MSVC C++ toolchain. It is deliberately non-fatal: when
CMake is missing it prints *"CUE4Parse-Natives build failed. Continuing without it"* and the
managed build succeeds anyway - which is exactly how the MCP server shipped without animation
support. If you rebuild it, drop the fresh binary in this folder and update the size above.

## How it reaches the output

`FortnitePorting.Mcp.csproj` copies it to the **root** of both the build and the publish
output (`<Link>CUE4Parse-Natives.dll</Link>`), next to `FortnitePorting.Mcp.exe`, which is
where the default P/Invoke probing path looks for it. `get_status` reports
`nativeAnimationSupport` so a client can tell whether the load actually succeeded.

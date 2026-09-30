# Bundled Python (optional)

`python-3.14.7-embed-amd64.zip` is Python's official Windows "embeddable" package, downloaded
unmodified from python.org:

- URL: https://www.python.org/ftp/python/3.14.7/python-3.14.7-embed-amd64.zip
- SHA-256: `d297e5ff019966817ad8502465176139f2d3d840fa4ed84b13bed399a6ab1f15` (matches python.org)
- Includes SQLite 3.50.4, the only non-trivial module `Analysis/build_db.py` needs.

**What uses it.** Every Windows 64-bit build unpacks it into `<build>/Python/`
(`Assets/Scripts/Experiment/Editor/CopyExperimentDataOnBuild.cs`). At the end of a session
`PostSessionHook` runs `Analysis/build_db.py` with that `python.exe`, so a lab PC with no Python
installed still gets its `.db` beside the CSVs.

**It is optional.** Without this zip, the build step logs a warning and the game falls back to
`python` on PATH; without any Python, the session runs and logs exactly the same, only without
the `.db` (build it later with `python Analysis/build_db.py --data <build>/Data`).

**Updating.** Drop a newer `python-X.Y.Z-embed-amd64.zip` here and delete the old one (the
build takes the newest by name). `build_db.py` uses only the standard library, so no packages
are needed.

This folder is outside `Assets/` on purpose: Unity would otherwise try to import the DLLs
inside as plugins.

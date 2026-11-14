# TileVox Unity

Unity package ID: `com.pidev.tilevox`

Check out this repository at `<Unity project>/Packages/com.pidev.tilevox`.
Unity treats that directory as an embedded package, so edits in these working
trees are visible in the game immediately.

This package contains three Git submodules:

- `GeometryCore/`: [petersvp/GeometryCore](https://github.com/petersvp/GeometryCore)
- `VoxelCore/`: [petersvp/VoxelCore](https://github.com/petersvp/VoxelCore)
- `TileVox/`: [petersvp/TileVox](https://github.com/petersvp/TileVox)

After cloning, from the package root:

```sh
git submodule update --init --recursive
git -C GeometryCore switch main
git -C VoxelCore switch main
git -C TileVox switch main
```

Each submodule records a specific commit. To deliberately advance all three to the
current `main` branch heads, run:

```sh
git submodule update --remote --merge -- GeometryCore VoxelCore TileVox
```

Commit the updated submodule pointers in this repository after updating them.

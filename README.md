# TileVox Unity

Unity package ID: `com.pidev.tilevox`

Check out this repository at `<Unity project>/Packages/com.pidev.tilevox`.
Unity treats that directory as an embedded package, so edits in these working
trees are visible in the game immediately.

This package contains three Git submodules:

- `VoxelCore/`: [petersvp/VoxelCore](https://github.com/petersvp/VoxelCore)
- `TileVox/`: [petersvp/TileVox](https://github.com/petersvp/TileVox)
- `geometry3Sharp/`: [gradientspace/geometry3Sharp](https://github.com/gradientspace/geometry3Sharp) (Boost Software License 1.0)

After cloning, from the package root:

```sh
git submodule update --init --recursive
git -C VoxelCore switch main
git -C TileVox switch main
git -C geometry3Sharp switch master
```

Each submodule records a specific commit. To deliberately advance them to their
configured branch heads, run:

```sh
git submodule update --remote --merge -- VoxelCore TileVox geometry3Sharp
```

Commit the updated submodule pointers in this repository after updating them.

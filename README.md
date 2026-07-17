# TerritoryViewer

Windows GUI: territory ID in → folder of LGB sheets + collision geometry out.

## Build & run

Requires the **.NET 10 SDK** (Lumina 7.6.0 is net10-only). From the repo root:

```
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```

Then run `publish\TerritoryViewer.exe` (needs the **.NET 10 Desktop Runtime** on the target machine).

1. sqpack folder (defaults to `C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack`)
2. Territory ID = TerritoryType row (e.g. `1345`), or paste a bg level dir directly (e.g. `bg/ex5/07_mid_m6/dun/m6d2/level`)
3. Output folder → creates `lgb-<id>-<mapcode>\` inside it
4. "Export OBJ" writes a world-space collision mesh (~45 MB for a dungeon)

## Output sheets

**bg / planmap / planevent / planlive / planner / sound / vfx .csv** — one row per LGB instance:
`File,LayerId,LayerName,FestivalID,AssetType,InstanceId,Name,X,Y,Z,RotX,RotY,RotZ,ScaleX,ScaleY,ScaleZ,Extra`
Extra is type-specific, `;`-joined. Notably BG → `Asset;CollisionAsset;CollisionType;AttrMask;Attr;Visible`, CollisionBox → `Shape;Enabled;Priority;AttrMask;Attr;PushOut;CollisionAsset`, SharedGroup → `Asset;DoorState;RotState;TransformState`.

**collision.csv** — every collider in the territory, flattened (SGBs recursed, transforms composed to world space):
`Source,LgbFile,LayerId,LayerName,InstanceId,ParentChain,Kind,PcbPath,X..ScaleZ,AttrMask,Attr,WorldMinX..WorldMaxZ`

- Source: `Terrain` (tiles from `<levelBase>/collision/list.pcb`), `BgPart`, `CollisionBox`, `SgbBgPart`, `SgbCollisionBox`
- Kind: `Mesh` (explicit .pcb), `MeshDerived` (bg part had no CollisionAsset; pcb inferred by `/bgparts/`→`/collision/`, `.mdl`→`.pcb`, only emitted if the file exists), `ModelBox` (CollisionType=Box: collider is the *model's* bounding box — dims not available offline, so no world AABB; PcbPath column holds the .mdl), analytic `Box/Sphere/Cylinder/Board/BoardBothSides`, `Terrain`
- World AABB computed by transforming parsed pcb verts (or unit box for analytic shapes)

**pcb-meshes.csv** — per unique .pcb: `PcbPath,Version,Nodes,Verts,Tris,LocalMin*,LocalMax*` (`MISSING` row if unparseable).

**collision-mesh.obj** (optional) — all mesh colliders in world space, one `g <Source>_<InstanceId>_<n>` group per collider, pcb path as comment. Analytic shapes exported as unit primitives (16-seg cylinder; sphere approximated as cylinder). Loadable in Blender/MeshLab.

## Caveats

- Rotation convention: local = S·Rx·Ry·Rz·T, world = local × parent — matches the game for everything verified so far, but composed SGB rotations haven't been exhaustively checked against in-game collision.
- SGB member parsing uses reverse-engineered offsets (BgPart collision path @ +0x34, CollisionBox pcb @ +0x48). Validated on TT 1345 (1,126 sane pcb paths, one legitimately pathless LVD trigger); exotic SGB types may be missed.
- `ModelBox` colliders need the .mdl bounding box, which this tool doesn't read — rows are listed but have no geometry/AABB.
- Material bits (u64 per triangle) are parsed but only the per-instance AttrMask/Attr are exported; per-tri materials aren't in the sheets. vnavmesh notes: `(mat&0x410)==0x400` conditionally-inactive, `(mat&0x1F)==0x11` invisible walls, `0x200000` unlandable.

## Validation (TT 1345, m6d2)

All shared sheets byte-identical to the xivtool reference dump on cols 1–16 (6,321 bg rows). 395/395 pcbs parsed. collision.csv world AABBs match OBJ vertex bounds exactly. 5,830 colliders: 225 terrain tiles, 2,732 mesh, 1,142 derived-mesh, 592 model-box, 1,126 SGB mesh, 13 analytic.

## License

MIT — see [LICENSE](LICENSE). Reads only your own local game install via [Lumina](https://github.com/NotAdam/Lumina); ships no game assets.

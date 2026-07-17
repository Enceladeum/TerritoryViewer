// DumpCore - LGB/collision dump engine for one FFXIV territory.
// Shared by the WinForms GUI (TerritoryViewer) and the sandbox CLI test harness.
// Territory id (or bg level dir) in -> folder of CSV sheets (+ optional OBJ) out.
//
// Sheets produced:
//   bg.csv, planmap.csv, planevent.csv, planlive.csv, planner.csv, sound.csv, vfx.csv
//     - one row per LGB instance object, transform + type-specific Extra column
//   collision.csv     - unified collider list (terrain tiles, bg part pcbs incl. derived,
//                       CollisionBox/analytic shapes, SGB-nested colliders) with world
//                       transform and world AABB
//   pcb-meshes.csv    - every referenced .pcb file: version, nodes, verts, tris, local AABB
//   collision-mesh.obj (optional) - all collision geometry, world space, grouped per collider
//
// PCB format (per FFXIVClientStructs BGCollision/Mesh.cs):
//   FileHeader 0x10: version i32@4 (1|4 supported), totalNodes@8, totalPrims@0xC
//   FileNode  0x30: child1 i32@8 / child2 i32@0xC (byte offsets from node start),
//     AABB f32x6 @0x10, nVertsCompressed u16@0x28, nPrims u16@0x2A, nVertsRaw u16@0x2C,
//     then f32[3*nRaw], u16[3*nCompressed] (0..65535 lerp of AABB), Primitive[nPrims]
//   Primitive 0xC: v1,v2,v3 u8 @0..2, material u64 @4
// Terrain: <levelBase>/collision/list.pcb  (header 0x20: numMeshes i32@0, AABB@4;
//   entries 0x20: meshId i32@0, AABB@4) -> tr%04d.pcb tiles, identity transform.
//
// Rotation convention: local = S * Rx * Ry * Rz * T (System.Numerics row-vector),
// world = local * parentWorld. Matches single-level LGB placement; nested SGB chains
// with non-Y rotation may deviate slightly in axis order.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Lumina;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;
using Lumina.Text.ReadOnly;

namespace TerritoryViewer;

public static class DumpCore
{
    static string Csv(string s) =>
        s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r')
            ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    static string R(float f) => f.ToString("R", CultureInfo.InvariantCulture);

    // ---------- PCB parsing ----------
    public sealed class PcbMesh
    {
        public int Version;
        public int NodeCount;
        public List<Vector3> Verts = new();
        public List<(int a, int b, int c, ulong mat)> Tris = new();
        public Vector3 Min = new(float.MaxValue), Max = new(float.MinValue);
    }

    public static PcbMesh? ParsePcb(byte[] d)
    {
        if (d.Length < 0x40) return null;
        int version = BitConverter.ToInt32(d, 4);
        if (version != 1 && version != 4) return null;
        var m = new PcbMesh { Version = version };
        var stack = new Stack<int>();
        stack.Push(0x10); // root node follows header
        while (stack.Count > 0)
        {
            int o = stack.Pop();
            if (o <= 0 || o + 0x30 > d.Length) continue;
            m.NodeCount++;
            int c1 = BitConverter.ToInt32(d, o + 8);
            int c2 = BitConverter.ToInt32(d, o + 0xC);
            var bmin = new Vector3(BitConverter.ToSingle(d, o + 0x10), BitConverter.ToSingle(d, o + 0x14), BitConverter.ToSingle(d, o + 0x18));
            var bmax = new Vector3(BitConverter.ToSingle(d, o + 0x1C), BitConverter.ToSingle(d, o + 0x20), BitConverter.ToSingle(d, o + 0x24));
            int nComp = BitConverter.ToUInt16(d, o + 0x28);
            int nPrim = BitConverter.ToUInt16(d, o + 0x2A);
            int nRaw = BitConverter.ToUInt16(d, o + 0x2C);
            int vbase = m.Verts.Count;
            int pRaw = o + 0x30;
            int pComp = pRaw + 12 * nRaw;
            int pPrim = pComp + 6 * nComp;
            if (pPrim + 12 * nPrim > d.Length) continue;
            for (int i = 0; i < nRaw; i++)
            {
                var v = new Vector3(BitConverter.ToSingle(d, pRaw + 12 * i), BitConverter.ToSingle(d, pRaw + 12 * i + 4), BitConverter.ToSingle(d, pRaw + 12 * i + 8));
                m.Verts.Add(v); m.Min = Vector3.Min(m.Min, v); m.Max = Vector3.Max(m.Max, v);
            }
            var scale = (bmax - bmin) / 65535.0f;
            for (int i = 0; i < nComp; i++)
            {
                var v = bmin + scale * new Vector3(
                    BitConverter.ToUInt16(d, pComp + 6 * i),
                    BitConverter.ToUInt16(d, pComp + 6 * i + 2),
                    BitConverter.ToUInt16(d, pComp + 6 * i + 4));
                m.Verts.Add(v); m.Min = Vector3.Min(m.Min, v); m.Max = Vector3.Max(m.Max, v);
            }
            for (int i = 0; i < nPrim; i++)
            {
                int p = pPrim + 12 * i;
                m.Tris.Add((vbase + d[p], vbase + d[p + 1], vbase + d[p + 2], BitConverter.ToUInt64(d, p + 4)));
            }
            if (c1 != 0) stack.Push(o + c1);
            if (c2 != 0) stack.Push(o + c2);
        }
        return m;
    }

    // ---------- analytic shapes (unit-size, scaled by transform; per vnavmesh conventions) ----------
    static readonly Vector3[] BoxVerts;
    static readonly (int, int, int)[] BoxTris;
    static readonly Vector3[] CylVerts;
    static readonly (int, int, int)[] CylTris;
    static DumpCore()
    {
        BoxVerts = new Vector3[8];
        for (int i = 0; i < 8; i++)
            BoxVerts[i] = new((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1);
        BoxTris = new (int, int, int)[]
        {
            (0,1,3),(0,3,2),(4,6,7),(4,7,5), // -z,+z? (winding not critical for inspection)
            (0,4,5),(0,5,1),(2,3,7),(2,7,6),
            (0,2,6),(0,6,4),(1,5,7),(1,7,3)
        };
        const int N = 16;
        var cv = new List<Vector3>();
        for (int i = 0; i < N; i++)
        {
            float a = 2 * MathF.PI * i / N;
            cv.Add(new(MathF.Cos(a), -1, MathF.Sin(a)));
            cv.Add(new(MathF.Cos(a), 1, MathF.Sin(a)));
        }
        cv.Add(new(0, -1, 0)); cv.Add(new(0, 1, 0));
        var ct = new List<(int, int, int)>();
        for (int i = 0; i < N; i++)
        {
            int j = (i + 1) % N;
            ct.Add((2 * i, 2 * j, 2 * i + 1)); ct.Add((2 * i + 1, 2 * j, 2 * j + 1));
            ct.Add((2 * N, 2 * j, 2 * i));       // bottom
            ct.Add((2 * N + 1, 2 * i + 1, 2 * j + 1)); // top
        }
        CylVerts = cv.ToArray(); CylTris = ct.ToArray();
    }

    static Matrix4x4 LocalMatrix(Vector3 t, Vector3 r, Vector3 s) =>
        Matrix4x4.CreateScale(s) * Matrix4x4.CreateRotationX(r.X) * Matrix4x4.CreateRotationY(r.Y)
        * Matrix4x4.CreateRotationZ(r.Z) * Matrix4x4.CreateTranslation(t);

    // ---------- collider accumulation ----------
    sealed class Collider
    {
        public string Source = "", LgbFile = "", LayerName = "", Kind = "", PcbPath = "", ParentChain = "";
        public uint LayerId; public ulong InstanceId;
        public Vector3 T, Rot, S = Vector3.One;
        public Matrix4x4 World = Matrix4x4.Identity;
        public string AttrMask = "", Attr = "";
        public Vector3 Min = new(float.MaxValue), Max = new(float.MinValue);
        public bool HasGeom;
    }

    public static string Run(string sqpack, string territory, string outRoot, bool exportObj, Action<string> log)
    {
        var gd = new GameData(sqpack, new LuminaOptions { PanicOnSheetChecksumMismatch = false });

        // --- resolve territory id or level dir ---
        string levelDir, label = territory.Trim();
        if (uint.TryParse(label, out var terrId))
        {
            var tt = gd.Excel.GetSheet<RawRow>(null, "TerritoryType");
            if (!tt.HasRow(terrId)) throw new Exception($"TerritoryType {terrId} not found");
            var row = tt.GetRow(terrId);
            string? bg = null;
            for (var c = 0; c < row.Columns.Count && bg == null; c++)
                if (row.Columns[c].Type == ExcelColumnDataType.String)
                {
                    var s = row.ReadColumn(c) is ReadOnlySeString rss ? rss.ExtractText() : "";
                    if (s.Contains("/level/")) bg = s;
                }
            if (bg == null) throw new Exception($"no Bg path on TerritoryType {terrId}");
            levelDir = "bg/" + bg[..bg.LastIndexOf('/')];
            label = terrId.ToString();
        }
        else levelDir = label.TrimEnd('/');

        var dirName = levelDir.Split('/')[^2] == "level" ? levelDir.Split('/')[^3] : levelDir.Split('/')[^1];
        // levelDir like bg/.../<zone>/level -> zone name is second-to-last
        var parts0 = levelDir.Split('/');
        dirName = parts0[^1] == "level" && parts0.Length >= 2 ? parts0[^2] : parts0[^1];
        var outDir = Path.Combine(outRoot, $"lgb-{label}-{dirName}");
        Directory.CreateDirectory(outDir);
        log($"level dir: {levelDir}");
        log($"output:    {outDir}");

        var colliders = new List<Collider>();
        var pcbCache = new Dictionary<string, PcbMesh?>();
        PcbMesh? GetPcb(string path)
        {
            if (pcbCache.TryGetValue(path, out var m)) return m;
            var f = gd.GetFile(path);
            m = f == null ? null : ParsePcb(f.Data);
            pcbCache[path] = m;
            return m;
        }

        void FinishCollider(Collider c)
        {
            if (c.Kind is "Mesh" or "MeshDerived" or "Terrain")
            {
                var m = c.PcbPath.Length > 0 ? GetPcb(c.PcbPath) : null;
                if (m != null)
                {
                    c.HasGeom = true;
                    foreach (var v in m.Verts)
                    {
                        var w = Vector3.Transform(v, c.World);
                        c.Min = Vector3.Min(c.Min, w); c.Max = Vector3.Max(c.Max, w);
                    }
                }
            }
            else if (c.Kind is "Box" or "Board" or "BoardBothSides")
            {
                c.HasGeom = true;
                foreach (var v in BoxVerts)
                {
                    var w = Vector3.Transform(v, c.World);
                    c.Min = Vector3.Min(c.Min, w); c.Max = Vector3.Max(c.Max, w);
                }
            }
            else if (c.Kind is "Sphere" or "Cylinder")
            {
                c.HasGeom = true;
                foreach (var v in CylVerts)
                {
                    var w = Vector3.Transform(v, c.World);
                    c.Min = Vector3.Min(c.Min, w); c.Max = Vector3.Max(c.Max, w);
                }
            }
            colliders.Add(c);
        }

        // ---- SGB recursion (custom parser; offsets relative to instance start, see CutScan sgblayouts) ----
        var sgbVisited = new HashSet<string>();
        void ExpandSgb(string sgbPath, Matrix4x4 parentWorld, string chain, string srcLgb, uint layerId, string layerName, ulong rootInstId, int depth)
        {
            if (depth > 6) return;
            var f = gd.GetFile(sgbPath);
            if (f == null) return;
            var d = f.Data;
            uint U32(int at) => at >= 0 && at + 4 <= d.Length ? BitConverter.ToUInt32(d, at) : 0;
            float F32(int at) => BitConverter.ToSingle(d, at);
            string CStr(int at) { if (at <= 0 || at >= d.Length) return ""; int e = at; while (e < d.Length && d[e] != 0) e++; return Encoding.UTF8.GetString(d, at, e - at); }
            try
            {
                int nSec = (int)U32(8); int off = 0xC, scn = -1;
                for (int i = 0; i < nSec && off + 8 <= d.Length; i++)
                {
                    if (d[off] == 'S' && d[off + 1] == 'C' && d[off + 2] == 'N' && d[off + 3] == '1') { scn = off + 8; break; }
                    off += (int)U32(off + 4);
                }
                if (scn < 0) return;
                int offEmb = (int)U32(scn), numEmb = (int)U32(scn + 4);
                for (int g = 0; g < numEmb; g++)
                {
                    int lg = scn + offEmb + g * 0x10;
                    int offLayers = (int)U32(lg + 8), numLayers = (int)U32(lg + 0xC);
                    for (int k = 0; k < numLayers; k++)
                    {
                        int lyr = lg + offLayers + (int)U32(lg + offLayers + 4 * k);
                        int offInst = (int)U32(lyr + 8), numInst = (int)U32(lyr + 0xC);
                        for (int j = 0; j < numInst; j++)
                        {
                            int io = lyr + offInst + (int)U32(lyr + offInst + 4 * j);
                            uint ty = U32(io);
                            var lt = new Vector3(F32(io + 0xC), F32(io + 0x10), F32(io + 0x14));
                            var lr = new Vector3(F32(io + 0x18), F32(io + 0x1C), F32(io + 0x20));
                            var ls = new Vector3(F32(io + 0x24), F32(io + 0x28), F32(io + 0x2C));
                            var world = LocalMatrix(lt, lr, ls) * parentWorld;
                            if (ty == 1) // BgPart: asset @+0x30, collision pcb @+0x34 (offsets from instance start)
                            {
                                var asset = CStr(io + (int)U32(io + 0x30));
                                var coll = CStr(io + (int)U32(io + 0x34));
                                if (coll.Length == 0 && asset.EndsWith(".mdl"))
                                {
                                    var cand = asset.Replace("/bgparts/", "/collision/")[..^4] + ".pcb";
                                    if (cand != asset && gd.FileExists(cand)) coll = cand;
                                }
                                if (coll.Length > 0 && coll.EndsWith(".pcb"))
                                    FinishCollider(new Collider { Source = "SgbBgPart", LgbFile = srcLgb, LayerId = layerId, LayerName = layerName, InstanceId = rootInstId, ParentChain = chain, Kind = "Mesh", PcbPath = coll, World = world, T = lt, Rot = lr, S = ls });
                            }
                            else if (ty == 6) // nested SharedGroup
                            {
                                var asset = CStr(io + (int)U32(io + 0x30));
                                if (asset.EndsWith(".sgb") && sgbVisited.Add(chain + "|" + asset + "|" + j))
                                    ExpandSgb(asset, world, chain + ">" + Path.GetFileNameWithoutExtension(asset), srcLgb, layerId, layerName, rootInstId, depth + 1);
                            }
                            else if (ty == 57) // CollisionBox: TriggerBox shape @+0x30, collision pcb offset @+0x48
                            {
                                uint shape = U32(io + 0x30);
                                var coll = CStr(io + (int)U32(io + 0x48));
                                var kind = ShapeName(shape);
                                if (coll.EndsWith(".pcb")) { kind = "Mesh"; }
                                FinishCollider(new Collider { Source = "SgbCollisionBox", LgbFile = srcLgb, LayerId = layerId, LayerName = layerName, InstanceId = rootInstId, ParentChain = chain, Kind = kind, PcbPath = coll.EndsWith(".pcb") ? coll : "", World = world, T = lt, Rot = lr, S = ls, AttrMask = $"0x{U32(io + 0x3C):X}", Attr = $"0x{U32(io + 0x40):X}" });
                            }
                        }
                    }
                }
            }
            catch (Exception e) { log($"  sgb parse error {sgbPath}: {e.Message}"); }
        }

        // ---- per-LGB CSV dump + collider harvest ----
        var files = new[] { "bg", "planmap", "planevent", "planlive", "planner", "sound", "vfx" };
        bool any = false;
        foreach (var fname in files)
        {
            LgbFile? lgb = null;
            try { lgb = gd.GetFile<LgbFile>($"{levelDir}/{fname}.lgb"); }
            catch (Exception e) { log($"{fname}.lgb: parse error: {e.Message}"); continue; }
            if (lgb == null) continue;
            any = true;
            using var w = new StreamWriter(Path.Combine(outDir, fname + ".csv"), false, new UTF8Encoding(false));
            w.WriteLine("File,LayerId,LayerName,FestivalID,AssetType,InstanceId,Name,X,Y,Z,RotX,RotY,RotZ,ScaleX,ScaleY,ScaleZ,Extra");
            int count = 0;
            foreach (var layer in lgb.Layers)
            {
                foreach (var io in layer.InstanceObjects)
                {
                    var t = io.Transform;
                    var lt = new Vector3(t.Translation.X, t.Translation.Y, t.Translation.Z);
                    var lr = new Vector3(t.Rotation.X, t.Rotation.Y, t.Rotation.Z);
                    var ls = new Vector3(t.Scale.X, t.Scale.Y, t.Scale.Z);
                    string extra = io.Object switch
                    {
                        LayerCommon.ENPCInstanceObject e2 => $"BaseId={e2.ParentData.ParentData.BaseId}",
                        LayerCommon.PopRangeInstanceObject p2 => $"PopType={p2.PopType};Index={p2.Index}",
                        LayerCommon.ExitRangeInstanceObject x2 => $"ExitType={x2.ExitType};TerritoryType={x2.TerritoryType};Index={x2.Index};Shape={ShapeName((uint)x2.ParentData.TriggerBoxShape)}",
                        LayerCommon.MapRangeInstanceObject m2 => $"Map={m2.Map};PlaceNameBlock={m2.PlaceNameBlock};PlaceNameSpot={m2.PlaceNameSpot};Shape={ShapeName((uint)m2.ParentData.TriggerBoxShape)}",
                        LayerCommon.EventInstanceObject ev => $"BaseId={ev.ParentData.BaseId}",
                        LayerCommon.AetheryteInstanceObject ae => $"BaseId={ae.ParentData.BaseId}",
                        LayerCommon.BGInstanceObject bg2 => $"Asset={bg2.AssetPath};CollisionAsset={bg2.CollisionAssetPath};CollisionType={bg2.CollisionType};AttrMask=0x{bg2.AttributeMask:X};Attr=0x{bg2.Attribute:X};Visible={bg2.IsVisible}",
                        LayerCommon.SharedGroupInstanceObject sg => $"Asset={sg.AssetPath};DoorState={sg.InitialDoorState};RotState={sg.InitialRotationState};TransformState={sg.InitialTransformState}",
                        LayerCommon.CollisionBoxInstanceObject cb => $"Shape={ShapeName((uint)cb.ParentData.TriggerBoxShape)};Enabled={cb.ParentData.Enabled};Priority={cb.ParentData.Priority};AttrMask=0x{cb.AttributeMask:X};Attr=0x{cb.Attribute:X};PushOut={cb.PushPlayerOut};CollisionAsset={cb.CollisionAssetPath}",
                        LayerCommon.EventRangeInstanceObject er => $"Shape={ShapeName((uint)er.ParentData.TriggerBoxShape)};Enabled={er.ParentData.Enabled}",
                        LayerCommon.SoundInstanceObject so => $"Asset={so.AssetPath}",
                        LayerCommon.VFXInstanceObject vf => $"Asset={vf.AssetPath}",
                        LayerCommon.EnvSetInstanceObject es => $"Asset={es.AssetPath}",
                        LayerCommon.LightInstanceObject li => $"LightType={li.LightType};Range={R(li.RangeRate)}",
                        _ => ""
                    };
                    w.WriteLine(string.Join(",",
                        fname, layer.LayerId, Csv(layer.Name ?? ""), layer.FestivalID,
                        io.AssetType, io.InstanceId, Csv(io.Name ?? ""),
                        R(lt.X), R(lt.Y), R(lt.Z), R(lr.X), R(lr.Y), R(lr.Z), R(ls.X), R(ls.Y), R(ls.Z),
                        Csv(extra)));
                    count++;

                    // -------- collider harvest --------
                    var world = LocalMatrix(lt, lr, ls);
                    switch (io.Object)
                    {
                        case LayerCommon.BGInstanceObject bgo:
                        {
                            var coll = bgo.CollisionAssetPath ?? "";
                            var kind = "Mesh";
                            if (coll.Length == 0 && (bgo.AssetPath?.EndsWith(".mdl") ?? false))
                            {
                                var cand = bgo.AssetPath.Replace("/bgparts/", "/collision/")[..^4] + ".pcb";
                                if (cand != bgo.AssetPath && gd.FileExists(cand)) { coll = cand; kind = "MeshDerived"; }
                            }
                            if (coll.Length > 0)
                                FinishCollider(new Collider { Source = "BgPart", LgbFile = fname, LayerId = layer.LayerId, LayerName = layer.Name ?? "", InstanceId = io.InstanceId, Kind = kind, PcbPath = coll, World = world, T = lt, Rot = lr, S = ls, AttrMask = $"0x{bgo.AttributeMask:X}", Attr = $"0x{bgo.Attribute:X}" });
                            else if (bgo.CollisionType == ModelCollisionType.Box)
                                FinishCollider(new Collider { Source = "BgPart", LgbFile = fname, LayerId = layer.LayerId, LayerName = layer.Name ?? "", InstanceId = io.InstanceId, Kind = "ModelBox", PcbPath = bgo.AssetPath ?? "", World = world, T = lt, Rot = lr, S = ls, AttrMask = $"0x{bgo.AttributeMask:X}", Attr = $"0x{bgo.Attribute:X}" });
                            break;
                        }
                        case LayerCommon.CollisionBoxInstanceObject cbo:
                        {
                            var coll = cbo.CollisionAssetPath ?? "";
                            var kind = coll.EndsWith(".pcb") ? "Mesh" : ShapeName((uint)cbo.ParentData.TriggerBoxShape);
                            FinishCollider(new Collider { Source = "CollisionBox", LgbFile = fname, LayerId = layer.LayerId, LayerName = layer.Name ?? "", InstanceId = io.InstanceId, Kind = kind, PcbPath = coll.EndsWith(".pcb") ? coll : "", World = world, T = lt, Rot = lr, S = ls, AttrMask = $"0x{cbo.AttributeMask:X}", Attr = $"0x{cbo.Attribute:X}" });
                            break;
                        }
                        case LayerCommon.SharedGroupInstanceObject sgo:
                            if (sgo.AssetPath?.EndsWith(".sgb") ?? false)
                            {
                                sgbVisited.Clear();
                                ExpandSgb(sgo.AssetPath, world, Path.GetFileNameWithoutExtension(sgo.AssetPath), fname, layer.LayerId, layer.Name ?? "", io.InstanceId, 0);
                            }
                            break;
                    }
                }
            }
            log($"{fname}.lgb: {lgb.Layers.Length} layers, {count} instances -> {fname}.csv");
        }
        if (!any) throw new Exception($"no lgb files under {levelDir}");

        // ---- terrain collision ----
        var levelBase = levelDir.EndsWith("/level") ? levelDir[..^6] : levelDir;
        var listPath = $"{levelBase}/collision/list.pcb";
        var listFile = gd.GetFile(listPath);
        if (listFile != null)
        {
            var d = listFile.Data;
            int n = BitConverter.ToInt32(d, 0);
            for (int i = 0; i < n && 0x20 + 0x20 * i + 0x20 <= d.Length; i++)
            {
                int e = 0x20 + 0x20 * i;
                int meshId = BitConverter.ToInt32(d, e);
                FinishCollider(new Collider { Source = "Terrain", LgbFile = "terrain", Kind = "Terrain", PcbPath = $"{levelBase}/collision/tr{meshId:d4}.pcb", World = Matrix4x4.Identity });
            }
            log($"terrain: {n} collision tiles ({listPath})");
        }
        else log($"terrain: no {listPath} (indoor/instanced level or bgplate-less)");

        // ---- collision.csv ----
        using (var w = new StreamWriter(Path.Combine(outDir, "collision.csv"), false, new UTF8Encoding(false)))
        {
            w.WriteLine("Source,LgbFile,LayerId,LayerName,InstanceId,ParentChain,Kind,PcbPath,X,Y,Z,RotX,RotY,RotZ,ScaleX,ScaleY,ScaleZ,AttrMask,Attr,WorldMinX,WorldMinY,WorldMinZ,WorldMaxX,WorldMaxY,WorldMaxZ");
            foreach (var c in colliders)
            {
                var bb = c.HasGeom
                    ? string.Join(",", R(c.Min.X), R(c.Min.Y), R(c.Min.Z), R(c.Max.X), R(c.Max.Y), R(c.Max.Z))
                    : ",,,,,";
                w.WriteLine(string.Join(",",
                    c.Source, c.LgbFile, c.LayerId, Csv(c.LayerName), c.InstanceId, Csv(c.ParentChain), c.Kind, Csv(c.PcbPath),
                    R(c.T.X), R(c.T.Y), R(c.T.Z), R(c.Rot.X), R(c.Rot.Y), R(c.Rot.Z), R(c.S.X), R(c.S.Y), R(c.S.Z),
                    c.AttrMask, c.Attr, bb));
            }
        }
        log($"collision.csv: {colliders.Count} colliders " +
            $"(terrain {colliders.Count(c => c.Source == "Terrain")}, bgpart {colliders.Count(c => c.Source == "BgPart")}, " +
            $"box {colliders.Count(c => c.Source == "CollisionBox")}, sgb {colliders.Count(c => c.Source.StartsWith("Sgb"))})");

        // ---- pcb-meshes.csv ----
        using (var w = new StreamWriter(Path.Combine(outDir, "pcb-meshes.csv"), false, new UTF8Encoding(false)))
        {
            w.WriteLine("PcbPath,Version,Nodes,Verts,Tris,LocalMinX,LocalMinY,LocalMinZ,LocalMaxX,LocalMaxY,LocalMaxZ");
            foreach (var (path, m) in pcbCache.OrderBy(kv => kv.Key))
            {
                if (m == null) { w.WriteLine($"{Csv(path)},MISSING,,,,,,,,,"); continue; }
                w.WriteLine(string.Join(",", Csv(path), m.Version, m.NodeCount, m.Verts.Count, m.Tris.Count,
                    R(m.Min.X), R(m.Min.Y), R(m.Min.Z), R(m.Max.X), R(m.Max.Y), R(m.Max.Z)));
            }
        }
        log($"pcb-meshes.csv: {pcbCache.Count} pcb files ({pcbCache.Count(kv => kv.Value == null)} missing/unparsed)");

        // ---- OBJ export ----
        if (exportObj)
        {
            long triTotal = 0;
            using var w = new StreamWriter(Path.Combine(outDir, "collision-mesh.obj"), false, new UTF8Encoding(false));
            w.WriteLine($"# FFXIV collision mesh, world space - {label} ({levelDir})");
            w.WriteLine("# groups: <Source>_<InstanceId>_<n>; analytic shapes are unit primitives scaled by transform");
            int vbase = 1, gi = 0;
            foreach (var c in colliders)
            {
                gi++;
                IReadOnlyList<Vector3> verts;
                IEnumerable<(int, int, int)> tris;
                if (c.Kind is "Mesh" or "MeshDerived" or "Terrain")
                {
                    var m = c.PcbPath.Length > 0 ? GetPcb(c.PcbPath) : null;
                    if (m == null || m.Tris.Count == 0) continue;
                    verts = m.Verts; tris = m.Tris.Select(t2 => (t2.a, t2.b, t2.c));
                }
                else if (c.Kind is "Box" or "Board" or "BoardBothSides" or "ModelBox") { verts = BoxVerts; tris = BoxTris; }
                else if (c.Kind is "Sphere" or "Cylinder") { verts = CylVerts; tris = CylTris; }
                else continue;
                w.WriteLine($"g {c.Source}_{c.InstanceId}_{gi}");
                if (c.PcbPath.Length > 0) w.WriteLine($"# {c.PcbPath}");
                foreach (var v in verts)
                {
                    var p = Vector3.Transform(v, c.World);
                    w.WriteLine($"v {R(p.X)} {R(p.Y)} {R(p.Z)}");
                }
                foreach (var (a, b, c3) in tris)
                { w.WriteLine($"f {vbase + a} {vbase + b} {vbase + c3}"); triTotal++; }
                vbase += verts.Count;
            }
            log($"collision-mesh.obj: {triTotal} triangles, {vbase - 1} vertices");
        }
        return outDir;
    }

    static string ShapeName(uint s) => s switch
    {
        1 => "Box", 2 => "Sphere", 3 => "Cylinder", 4 => "Board", 5 => "Mesh", 6 => "BoardBothSides",
        _ => $"Shape{s}"
    };
}

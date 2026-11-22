// Yassify engine (shared with Tailor) - FFXIV .mdl (v5/v6) reader and writer.
//
// The file is kept as raw sections; only what an edit touches is rebuilt, so
// a model that isn't changed writes back byte for byte. Supported edits:
//   - vertex positions, normals/tangents and bone weights, in place
//   - adding bones (names, bone tables, bone bounding boxes)
//   - widening 4-weight vertices to 8 weights (rebuilds the vertex buffers)
//   - adding or replacing a shape key (appends the shape's vertices)
// Anything the game reads by absolute offset (vertex/index data) is shifted
// when the header grows.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Yassify.Core;

public sealed class MdlException : Exception
{
    public MdlException(string msg) : base(msg) { }
}

public enum VUsage : byte { Position = 0, BlendWeights = 1, BlendIndices = 2, Normal = 3, UV = 4, Flow = 5, Binormal = 6, Color = 7 }

public sealed class VElement
{
    public byte Stream, Offset, Type, Usage, UsageIndex;
    public int Size => MdlFile.TypeSize(Type);
    public VElement Clone() => (VElement)MemberwiseClone();
}

public sealed class MdlMesh
{
    public ushort VertexCount;
    public ushort Pad;
    public uint IndexCount;
    public ushort MaterialIndex, SubMeshIndex, SubMeshCount, BoneTableIndex;
    public uint StartIndex;
    public uint[] VertexBufferOffset = new uint[3];
    public byte[] Stride = new byte[3];
    public byte StreamCount;
    public List<VElement> Elements = new();
}

public sealed class MdlLod
{
    public byte[] Raw = new byte[60];
    public ushort MeshIndex => BitConverter.ToUInt16(Raw, 0);
    public ushort MeshCount => BitConverter.ToUInt16(Raw, 2);
    public uint EdgeGeometrySize { get => BitConverter.ToUInt32(Raw, 28); }
    public uint EdgeGeometryDataOffset { get => BitConverter.ToUInt32(Raw, 32); set => BitConverter.TryWriteBytes(Raw.AsSpan(32), value); }
    public uint VertexBufferSize { get => BitConverter.ToUInt32(Raw, 44); set => BitConverter.TryWriteBytes(Raw.AsSpan(44), value); }
    public uint IndexBufferSize { get => BitConverter.ToUInt32(Raw, 48); set => BitConverter.TryWriteBytes(Raw.AsSpan(48), value); }
    public uint VertexDataOffset { get => BitConverter.ToUInt32(Raw, 52); set => BitConverter.TryWriteBytes(Raw.AsSpan(52), value); }
    public uint IndexDataOffset { get => BitConverter.ToUInt32(Raw, 56); set => BitConverter.TryWriteBytes(Raw.AsSpan(56), value); }
}

public sealed class MdlSubmesh
{
    public uint IndexOffset, IndexCount, AttributeMask;
    public ushort BoneStart, BoneCount;
}

/// <summary>A shape key: per LOD, a run of shape meshes.</summary>
public sealed class MdlShape
{
    public uint NameOffset;
    public string Name = "";
    public ushort[] MeshStart = new ushort[3];
    public ushort[] MeshCount = new ushort[3];
}

/// <summary>A shape key's part on one mesh: which index-buffer entries (of
/// that mesh, counted from its first index) use which replacement vertex
/// (counted from the mesh's first vertex).</summary>
public sealed class MdlShapeMesh
{
    public uint MeshIndexOffset;      // the mesh's StartIndex
    public uint ValueCount, ValueOffset;
}

public sealed class MdlFile
{
    public const uint V5 = 0x01000005, V6 = 0x01000006;

    // file header
    public uint Version;
    uint _stackSize, _runtimeSize;
    ushort _declCount, _hdrMaterialCount;
    public uint[] VertexOffset = new uint[3], IndexOffset = new uint[3], VertexBufferSize = new uint[3], IndexBufferSize = new uint[3];
    public byte LodCount;
    byte[] _hdrTail = new byte[3];         // EnableIndexBufferStreaming, EnableEdgeGeometry, padding

    public List<List<VElement>> Decls = new();
    byte[] _declRaw = Array.Empty<byte>();
    ushort _stringCount, _stringPad;
    public byte[] Strings = Array.Empty<byte>();
    byte[] _modelHeader = new byte[56];
    byte[] _elementIds = Array.Empty<byte>();
    public MdlLod[] Lods = new MdlLod[3];
    byte[] _extraLods = Array.Empty<byte>();
    public List<MdlMesh> Meshes = new();
    byte[] _attrOffsets = Array.Empty<byte>(), _terrainShadowMeshes = Array.Empty<byte>();
    public List<MdlSubmesh> Submeshes = new();
    byte[] _terrainShadowSubmeshes = Array.Empty<byte>(), _materialOffsets = Array.Empty<byte>();
    public List<uint> BoneNameOffsets = new();
    public List<List<ushort>> BoneTables = new();
    public List<MdlShape> Shapes = new();
    public List<MdlShapeMesh> ShapeMeshes = new();
    public List<(ushort Base, ushort Repl)> ShapeValues = new();
    byte[] _submeshBoneMap = Array.Empty<byte>();   // with its size prefix
    byte[] _padding = Array.Empty<byte>();          // pad byte + pad bytes
    byte[] _boundingBoxes = new byte[128];
    public List<byte[]> BoneBoxes = new();
    byte[] _trailing = Array.Empty<byte>();        // anything between the boxes and the data
    public byte[] Data = Array.Empty<byte>();      // vertex + index data, from the header end
    int _headerEnd;
    int _bboxAt;                                   // where the boxes began in the original

    public bool Dirty;                             // structure changed (bones added, widened)
    public List<string> BoneNames = new();
    public List<string> MaterialNames = new();
    public List<string> AttributeNames = new();

    // ---- header field views (ModelHeader, 56 bytes)
    ushort MH16(int o) => BitConverter.ToUInt16(_modelHeader, o);
    void SetMH16(int o, ushort v) => BitConverter.TryWriteBytes(_modelHeader.AsSpan(o), v);
    public ushort MeshCount => MH16(4);
    public ushort AttributeCount => MH16(6);
    public ushort SubmeshCount => MH16(8);
    public ushort MaterialCount => MH16(10);
    public ushort BoneCount { get => MH16(12); private set => SetMH16(12, value); }
    public ushort BoneTableCount => MH16(14);
    public ushort ShapeCount { get => MH16(16); private set => SetMH16(16, value); }
    public ushort ShapeMeshCount { get => MH16(18); private set => SetMH16(18, value); }
    public ushort ShapeValueCount { get => MH16(20); private set => SetMH16(20, value); }
    public byte ModelLodCount => _modelHeader[22];
    public byte Flags1 => _modelHeader[23];
    public ushort ElementIdCount => MH16(24);
    public byte TerrainShadowMeshCount => _modelHeader[26];
    public byte Flags2 => _modelHeader[27];
    public ushort TerrainShadowSubmeshCount => MH16(38);
    public ushort BoneTableArrayCountTotal { get => MH16(44); private set => SetMH16(44, value); }

    public static int TypeSize(byte t) => t switch
    {
        0 => 4, 1 => 8, 2 => 12, 3 => 16, 5 => 4, 6 => 4, 7 => 8, 8 => 4, 9 => 4, 10 => 8,
        13 => 4, 14 => 8, 15 => 4, 16 => 4, 17 => 8, _ => -1,
    };

    // ---------------------------------------------------------------- read
    public static MdlFile Read(byte[] b)
    {
        var m = new MdlFile();
        m.Parse(b);
        return m;
    }

    void Parse(byte[] b)
    {
        if (b.Length < 0x44) throw new MdlException("file too short");
        var r = new BinaryReader(new MemoryStream(b));
        Version = r.ReadUInt32();
        if (Version != V5 && Version != V6) throw new MdlException($"unknown .mdl version {Version:X8}");
        _stackSize = r.ReadUInt32();
        _runtimeSize = r.ReadUInt32();
        _declCount = r.ReadUInt16();
        _hdrMaterialCount = r.ReadUInt16();
        for (int i = 0; i < 3; i++) VertexOffset[i] = r.ReadUInt32();
        for (int i = 0; i < 3; i++) IndexOffset[i] = r.ReadUInt32();
        for (int i = 0; i < 3; i++) VertexBufferSize[i] = r.ReadUInt32();
        for (int i = 0; i < 3; i++) IndexBufferSize[i] = r.ReadUInt32();
        LodCount = r.ReadByte();
        _hdrTail = r.ReadBytes(3);
        if (r.BaseStream.Position != 0x44) throw new MdlException("header size");

        _declRaw = r.ReadBytes((int)_stackSize);
        if (_declRaw.Length != _declCount * 136) throw new MdlException("vertex declarations don't match the stack size");
        for (int d = 0; d < _declCount; d++)
        {
            var els = new List<VElement>();
            for (int j = 0; j < 17; j++)
            {
                int o = d * 136 + j * 8;
                if (_declRaw[o] == 255) break;
                els.Add(new VElement { Stream = _declRaw[o], Offset = _declRaw[o + 1], Type = _declRaw[o + 2], Usage = _declRaw[o + 3], UsageIndex = _declRaw[o + 4] });
            }
            Decls.Add(els);
        }

        _stringCount = r.ReadUInt16();
        _stringPad = r.ReadUInt16();
        uint ssize = r.ReadUInt32();
        Strings = r.ReadBytes((int)ssize);
        _modelHeader = r.ReadBytes(56);
        _elementIds = r.ReadBytes(ElementIdCount * 32);
        for (int i = 0; i < 3; i++) Lods[i] = new MdlLod { Raw = r.ReadBytes(60) };
        if ((Flags2 & 0x10) != 0) _extraLods = r.ReadBytes(120);
        for (int i = 0; i < MeshCount; i++)
        {
            var me = new MdlMesh
            {
                VertexCount = r.ReadUInt16(), Pad = r.ReadUInt16(), IndexCount = r.ReadUInt32(),
                MaterialIndex = r.ReadUInt16(), SubMeshIndex = r.ReadUInt16(), SubMeshCount = r.ReadUInt16(),
                BoneTableIndex = r.ReadUInt16(), StartIndex = r.ReadUInt32(),
            };
            for (int s = 0; s < 3; s++) me.VertexBufferOffset[s] = r.ReadUInt32();
            for (int s = 0; s < 3; s++) me.Stride[s] = r.ReadByte();
            me.StreamCount = r.ReadByte();
            me.Elements = i < Decls.Count ? Decls[i] : new List<VElement>();
            Meshes.Add(me);
        }
        _attrOffsets = r.ReadBytes(AttributeCount * 4);
        _terrainShadowMeshes = r.ReadBytes(TerrainShadowMeshCount * 20);
        for (int i = 0; i < SubmeshCount; i++)
            Submeshes.Add(new MdlSubmesh { IndexOffset = r.ReadUInt32(), IndexCount = r.ReadUInt32(), AttributeMask = r.ReadUInt32(), BoneStart = r.ReadUInt16(), BoneCount = r.ReadUInt16() });
        _terrainShadowSubmeshes = r.ReadBytes(TerrainShadowSubmeshCount * 12);
        _materialOffsets = r.ReadBytes(MaterialCount * 4);
        for (int i = 0; i < BoneCount; i++) BoneNameOffsets.Add(r.ReadUInt32());

        if (Version == V5)
        {
            for (int i = 0; i < BoneTableCount; i++)
            {
                var idx = new ushort[64];
                for (int k = 0; k < 64; k++) idx[k] = r.ReadUInt16();
                byte cnt = r.ReadByte();
                r.ReadBytes(3);
                var t = new List<ushort>();
                for (int k = 0; k < cnt && k < 64; k++) t.Add(idx[k]);
                BoneTables.Add(t);
            }
        }
        else
        {
            long tStart = r.BaseStream.Position;
            var heads = new (ushort off, ushort cnt)[BoneTableCount];
            for (int i = 0; i < heads.Length; i++) heads[i] = (r.ReadUInt16(), r.ReadUInt16());
            long end = tStart + 4L * heads.Length;
            for (int i = 0; i < heads.Length; i++)
            {
                long pos = tStart + 4L * i + heads[i].off * 4L;
                var t = new List<ushort>();
                for (int k = 0; k < heads[i].cnt; k++) t.Add(BitConverter.ToUInt16(b, (int)(pos + 2 * k)));
                BoneTables.Add(t);
                end = Math.Max(end, pos + 2L * heads[i].cnt);
            }
            if ((end - tStart) % 4 != 0) end += 4 - (end - tStart) % 4;
            // must match the canonical layout, or we can't rebuild it safely
            long expect = tStart + 4L * heads.Length;
            long words = 0;
            for (int i = 0; i < heads.Length; i++)
            {
                if (heads[i].off != heads.Length - i + words) throw new MdlException("unusual bone table layout");
                words += (heads[i].cnt * 2 + 3) / 4;
            }
            if (end != expect + words * 4) throw new MdlException("unusual bone table size");
            r.BaseStream.Position = end;
        }
        for (int i = 0; i < ShapeCount; i++)
        {
            var sh = new MdlShape { NameOffset = r.ReadUInt32() };
            for (int k = 0; k < 3; k++) sh.MeshStart[k] = r.ReadUInt16();
            for (int k = 0; k < 3; k++) sh.MeshCount[k] = r.ReadUInt16();
            Shapes.Add(sh);
        }
        for (int i = 0; i < ShapeMeshCount; i++)
            ShapeMeshes.Add(new MdlShapeMesh { MeshIndexOffset = r.ReadUInt32(), ValueCount = r.ReadUInt32(), ValueOffset = r.ReadUInt32() });
        for (int i = 0; i < ShapeValueCount; i++)
            ShapeValues.Add((r.ReadUInt16(), r.ReadUInt16()));
        uint sbm = r.ReadUInt32();
        r.BaseStream.Position -= 4;
        _submeshBoneMap = r.ReadBytes(4 + (int)sbm);
        byte pad = r.ReadByte();
        r.BaseStream.Position -= 1;
        _padding = r.ReadBytes(1 + pad);
        _bboxAt = (int)r.BaseStream.Position;
        _boundingBoxes = r.ReadBytes(128);
        for (int i = 0; i < BoneCount; i++) BoneBoxes.Add(r.ReadBytes(32));
        _headerEnd = (int)(0x44 + _stackSize + _runtimeSize);
        int here = (int)r.BaseStream.Position;
        if (here > _headerEnd) throw new MdlException("header runs past the data");
        _trailing = b.AsSpan(here, _headerEnd - here).ToArray();
        Data = b.AsSpan(_headerEnd).ToArray();

        BoneNames = new List<string>();
        foreach (var o in BoneNameOffsets) BoneNames.Add(Str(o));
        for (int i = 0; i < MaterialCount; i++) MaterialNames.Add(Str(BitConverter.ToUInt32(_materialOffsets, 4 * i)));
        for (int i = 0; i < AttributeCount; i++) AttributeNames.Add(Str(BitConverter.ToUInt32(_attrOffsets, 4 * i)));
        foreach (var sh in Shapes) sh.Name = Str(sh.NameOffset);
    }

    string Str(uint off)
    {
        if (off >= Strings.Length) return "";
        int e = Array.IndexOf(Strings, (byte)0, (int)off);
        if (e < 0) e = Strings.Length;
        return Encoding.UTF8.GetString(Strings, (int)off, e - (int)off);
    }

    // ---------------------------------------------------------------- write
    public byte[] Write()
    {
        var hdr = new MemoryStream();
        var w = new BinaryWriter(hdr);
        // body of the header after the declarations (the "runtime" part)
        w.Write(_stringCount);
        w.Write(_stringPad);
        w.Write((uint)Strings.Length);
        w.Write(Strings);
        w.Write(_modelHeader);
        w.Write(_elementIds);
        foreach (var l in Lods) w.Write(l.Raw);
        w.Write(_extraLods);
        foreach (var me in Meshes)
        {
            w.Write(me.VertexCount); w.Write(me.Pad); w.Write(me.IndexCount);
            w.Write(me.MaterialIndex); w.Write(me.SubMeshIndex); w.Write(me.SubMeshCount); w.Write(me.BoneTableIndex);
            w.Write(me.StartIndex);
            for (int s = 0; s < 3; s++) w.Write(me.VertexBufferOffset[s]);
            for (int s = 0; s < 3; s++) w.Write(me.Stride[s]);
            w.Write(me.StreamCount);
        }
        w.Write(_attrOffsets);
        w.Write(_terrainShadowMeshes);
        foreach (var s in Submeshes) { w.Write(s.IndexOffset); w.Write(s.IndexCount); w.Write(s.AttributeMask); w.Write(s.BoneStart); w.Write(s.BoneCount); }
        w.Write(_terrainShadowSubmeshes);
        w.Write(_materialOffsets);
        foreach (var o in BoneNameOffsets) w.Write(o);
        if (Version == V5)
        {
            foreach (var t in BoneTables)
            {
                for (int k = 0; k < 64; k++) w.Write(k < t.Count ? t[k] : (ushort)0);
                w.Write((byte)t.Count); w.Write((byte)0); w.Write((byte)0); w.Write((byte)0);
            }
        }
        else
        {
            int words = 0;
            for (int i = 0; i < BoneTables.Count; i++)
            {
                w.Write((ushort)(BoneTables.Count - i + words));
                w.Write((ushort)BoneTables[i].Count);
                words += (BoneTables[i].Count * 2 + 3) / 4;
            }
            foreach (var t in BoneTables)
            {
                foreach (var x in t) w.Write(x);
                if (t.Count % 2 == 1) w.Write((ushort)0);
            }
        }
        foreach (var sh in Shapes)
        {
            w.Write(sh.NameOffset);
            for (int k = 0; k < 3; k++) w.Write(sh.MeshStart[k]);
            for (int k = 0; k < 3; k++) w.Write(sh.MeshCount[k]);
        }
        foreach (var sm in ShapeMeshes) { w.Write(sm.MeshIndexOffset); w.Write(sm.ValueCount); w.Write(sm.ValueOffset); }
        foreach (var (bi, rv) in ShapeValues) { w.Write(bi); w.Write(rv); }
        w.Write(_submeshBoneMap);
        // padding: keep the boxes on the same 8-byte alignment as before
        long bboxAt = 0x44 + _declRaw.Length + hdr.Length + 1;
        int origPad = _padding[0];
        int pad = origPad;
        if (Dirty)
        {
            int want = _bboxAt % 8;
            pad = 0;
            while ((bboxAt + pad) % 8 != want) pad++;
        }
        w.Write((byte)pad);
        if (pad == origPad) w.Write(_padding, 1, origPad); else w.Write(new byte[pad]);
        w.Write(_boundingBoxes);
        foreach (var bb in BoneBoxes) w.Write(bb);
        w.Write(_trailing);
        w.Flush();

        byte[] runtime = hdr.ToArray();
        uint newHeaderEnd = (uint)(0x44 + _declRaw.Length + runtime.Length);
        long delta = newHeaderEnd - (long)_headerEnd;

        var file = new MemoryStream();
        var f = new BinaryWriter(file);
        f.Write(Version);
        f.Write((uint)_declRaw.Length);
        f.Write((uint)runtime.Length);
        f.Write(_declCount);
        f.Write(_hdrMaterialCount);
        uint oldEnd = (uint)_headerEnd;
        uint Shift(uint v) => v >= oldEnd ? (uint)(v + delta) : v;
        for (int i = 0; i < 3; i++) f.Write(Shift(VertexOffset[i]));
        for (int i = 0; i < 3; i++) f.Write(Shift(IndexOffset[i]));
        for (int i = 0; i < 3; i++) f.Write(VertexBufferSize[i]);
        for (int i = 0; i < 3; i++) f.Write(IndexBufferSize[i]);
        f.Write(LodCount);
        f.Write(_hdrTail);
        f.Write(DeclBytes());
        // LOD offsets are absolute too - patch them inside the runtime block
        if (delta != 0)
        {
            int lodAt = 8 + Strings.Length + 56 + _elementIds.Length;
            for (int i = 0; i < 3; i++)
            {
                foreach (int fo in new[] { 32, 52, 56 })
                {
                    int at = lodAt + 60 * i + fo;
                    uint v = BitConverter.ToUInt32(runtime, at);
                    if (v != 0) BitConverter.TryWriteBytes(runtime.AsSpan(at), Shift(v));
                }
            }
        }
        f.Write(runtime);
        f.Write(Data);
        f.Flush();
        return file.ToArray();
    }

    byte[] DeclBytes()
    {
        var o = (byte[])_declRaw.Clone();
        for (int d = 0; d < Decls.Count; d++)
        {
            var els = Decls[d];
            for (int j = 0; j < els.Count; j++)
            {
                int p = d * 136 + j * 8;
                o[p] = els[j].Stream; o[p + 1] = els[j].Offset; o[p + 2] = els[j].Type; o[p + 3] = els[j].Usage; o[p + 4] = els[j].UsageIndex;
            }
        }
        return o;
    }

    // ---------------------------------------------------------------- bones
    public int BoneIndex(string name) => BoneNames.IndexOf(name);

    /// <summary>Appends a string to the string block (kept a multiple of 4
    /// long like the game's files) and returns its offset.</summary>
    uint AddString(string s)
    {
        uint off = (uint)Strings.Length;
        var add = Encoding.UTF8.GetBytes(s + "\0");
        var ns = new byte[Strings.Length + add.Length];
        Buffer.BlockCopy(Strings, 0, ns, 0, Strings.Length);
        Buffer.BlockCopy(add, 0, ns, Strings.Length, add.Length);
        int padded = (ns.Length + 3) / 4 * 4;
        if (padded != ns.Length) Array.Resize(ref ns, padded);
        Strings = ns;
        _stringCount++;
        return off;
    }

    /// <summary>Adds a bone name to the model (if missing) and returns its index.</summary>
    public int AddBone(string name)
    {
        int i = BoneIndex(name);
        if (i >= 0) return i;
        if (BoneNames.Count >= 0xFFFF) throw new MdlException("too many bones");
        uint off = AddString(name);
        BoneNameOffsets.Add(off);
        BoneNames.Add(name);
        BoneBoxes.Add(BoneBoxes.Count > 0 ? (byte[])BoneBoxes[0].Clone() : DefaultBox());
        BoneCount = (ushort)BoneNames.Count;
        Dirty = true;
        return BoneNames.Count - 1;
    }

    static byte[] DefaultBox()
    {
        var bb = new byte[32];
        var v = new float[] { -0.077f, -0.077f, -0.077f, 1, 0.077f, 0.077f, 0.077f, 1 };
        Buffer.BlockCopy(v, 0, bb, 0, 32);
        return bb;
    }

    /// <summary>The index of a model bone in a mesh's bone table, adding it if
    /// there's room (64 per table). -1 if the table is full.</summary>
    public int TableSlot(int meshIndex, int boneIndex)
    {
        var me = Meshes[meshIndex];
        if (me.BoneTableIndex >= BoneTables.Count) return -1;
        var t = BoneTables[me.BoneTableIndex];
        int k = t.IndexOf((ushort)boneIndex);
        if (k >= 0) return k;
        if (t.Count >= 64) return -1;
        t.Add((ushort)boneIndex);
        Dirty = true;
        RecountTables();
        return t.Count - 1;
    }

    void RecountTables()
    {
        int tot = 0;
        foreach (var t in BoneTables) tot += (t.Count + 1) / 2 * 2;
        BoneTableArrayCountTotal = (ushort)tot;
    }

    // ---------------------------------------------------------------- bounding boxes
    public (Vector4 min, Vector4 max) Box(int i)
    {
        var s = _boundingBoxes.AsSpan(32 * i);
        return (new Vector4(BitConverter.ToSingle(s), BitConverter.ToSingle(s[4..]), BitConverter.ToSingle(s[8..]), BitConverter.ToSingle(s[12..])),
                new Vector4(BitConverter.ToSingle(s[16..]), BitConverter.ToSingle(s[20..]), BitConverter.ToSingle(s[24..]), BitConverter.ToSingle(s[28..])));
    }

    /// <summary>Grows the model's bounding boxes (0 and 1) to hold the points.</summary>
    public void GrowBoxes(Vector3 lo, Vector3 hi)
    {
        for (int i = 0; i < 2; i++)
        {
            var (mn, mx) = Box(i);
            var nmn = new Vector4(MathF.Min(mn.X, lo.X), MathF.Min(mn.Y, lo.Y), MathF.Min(mn.Z, lo.Z), mn.W);
            var nmx = new Vector4(MathF.Max(mx.X, hi.X), MathF.Max(mx.Y, hi.Y), MathF.Max(mx.Z, hi.Z), mx.W);
            if (nmn == mn && nmx == mx) continue;
            var s = _boundingBoxes.AsSpan(32 * i);
            BitConverter.TryWriteBytes(s, nmn.X); BitConverter.TryWriteBytes(s[4..], nmn.Y); BitConverter.TryWriteBytes(s[8..], nmn.Z);
            BitConverter.TryWriteBytes(s[16..], nmx.X); BitConverter.TryWriteBytes(s[20..], nmx.Y); BitConverter.TryWriteBytes(s[24..], nmx.Z);
        }
    }

    // ---------------------------------------------------------------- layout checks
    /// <summary>True if the vertex/index data is laid out the usual way (each
    /// LOD's vertices then its indices, back to back, meshes in order) - the
    /// only layout this rebuilds when it widens weights.</summary>
    public bool CanRebuildData(out string why)
    {
        why = "";
        if (TerrainShadowMeshCount != 0 || (Flags2 & 0x10) != 0) { why = "terrain shadow or extra LOD data"; return false; }
        uint pos = (uint)_headerEnd;
        for (int l = 0; l < LodCount; l++)
        {
            if (Lods[l].EdgeGeometrySize != 0) { why = "edge geometry"; return false; }
            if (VertexOffset[l] != pos || IndexOffset[l] != pos + VertexBufferSize[l]) { why = "unusual data layout"; return false; }
            uint vb = 0;
            for (int mi = Lods[l].MeshIndex; mi < Lods[l].MeshIndex + Lods[l].MeshCount; mi++)
            {
                var me = Meshes[mi];
                for (int s = 0; s < 3; s++)
                {
                    if (me.Stride[s] == 0) continue;
                    if (me.VertexBufferOffset[s] != vb) { why = "vertex buffers not back to back"; return false; }
                    vb += (uint)(me.Stride[s] * me.VertexCount);
                }
            }
            if (vb != VertexBufferSize[l]) { why = "vertex buffer size"; return false; }
            pos = IndexOffset[l] + IndexBufferSize[l];
        }
        if (pos != _headerEnd + Data.Length) { why = "data after the last LOD"; return false; }
        return true;
    }

    /// <summary>A weights or bone-numbers element that holds 4 to a point. The game's own files keep 4 weights as type 8;
    /// some exporters write them as type 5 (0.1.1: those were skipped, so only the bone numbers went to 8 and the game
    /// misread the piece - creases wherever a bone moves, the chest first).</summary>
    static bool FourWide(VElement e) =>
        (e.Usage == (byte)VUsage.BlendWeights && (e.Type == 8 || e.Type == 5)) || (e.Usage == (byte)VUsage.BlendIndices && e.Type == 5);

    /// <summary>True when a mesh keeps its weights and its bone numbers at different counts per point (4 and 8) - what
    /// 0.1.0 left on pieces with type-5 weights.</summary>
    public bool MixedWeights(int mesh)
    {
        var we = Element(mesh, VUsage.BlendWeights);
        var ie = Element(mesh, VUsage.BlendIndices);
        return we != null && ie != null && TypeSize(we.Type) != TypeSize(ie.Type);
    }

    public bool AnyMixedWeights
    {
        get
        {
            for (int mi = 0; mi < Meshes.Count; mi++) if (MixedWeights(mi)) return true;
            return false;
        }
    }

    /// <summary>Changes 4-weight vertices to 8 weights (DT's format) so a
    /// vertex can hold more bones. Rebuilds the vertex buffers; indices and
    /// everything else stay as they are. Returns false if the layout isn't one
    /// this can rebuild. onlyMixed: just the meshes whose weights and bone
    /// numbers don't match (MixedWeights), the rest stay as they are.</summary>
    public bool WidenTo8(out string why, bool onlyMixed = false)
    {
        if (!CanRebuildData(out why)) return false;
        bool any = false;
        var newEls = new List<List<VElement>>();
        var newStride = new List<byte[]>();
        for (int mi = 0; mi < Meshes.Count; mi++)
        {
            var me = Meshes[mi];
            var els = new List<VElement>();
            foreach (var e in me.Elements) els.Add(e.Clone());
            bool widen = false;
            if (!onlyMixed || MixedWeights(mi))
                foreach (var e in els)
                    if (FourWide(e)) widen = true;
            var stride = (byte[])me.Stride.Clone();
            if (widen)
            {
                any = true;
                for (int s = 0; s < 3; s++)
                {
                    // each element moves by what the widened elements before it in
                    // this stream grew; gaps and trailing padding stay as they were
                    var inStream = els.FindAll(e => e.Stream == s);
                    if (inStream.Count == 0) continue;
                    inStream.Sort((a, b2) => a.Offset.CompareTo(b2.Offset));
                    int grow = 0;
                    foreach (var e in inStream)
                    {
                        e.Offset = (byte)(e.Offset + grow);
                        if (FourWide(e))
                        {
                            e.Type = 17;
                            grow += 4;
                        }
                    }
                    if (me.Stride[s] + grow > 255) { why = "vertex too large"; return false; }
                    stride[s] = (byte)(me.Stride[s] + grow);
                }
            }
            newEls.Add(els);
            newStride.Add(stride);
        }
        if (!any) { why = ""; return true; }

        // rebuild the data region
        var data = new MemoryStream();
        uint[] vOff = new uint[3], iOff = new uint[3], vSize = new uint[3];
        uint headerEnd = (uint)_headerEnd;    // the header doesn't change size here
        for (int l = 0; l < LodCount; l++)
        {
            vOff[l] = headerEnd + (uint)data.Length;
            uint vb = 0;
            var newVbo = new Dictionary<int, uint[]>();
            for (int mi = Lods[l].MeshIndex; mi < Lods[l].MeshIndex + Lods[l].MeshCount; mi++)
            {
                var me = Meshes[mi];
                var offs = new uint[3];
                for (int s = 0; s < 3; s++)
                {
                    if (me.Stride[s] == 0) continue;
                    offs[s] = vb;
                    int oldBase = (int)(VertexOffset[l] - _headerEnd + me.VertexBufferOffset[s]);
                    int ns = newStride[mi][s];
                    var buf = new byte[ns * me.VertexCount];
                    for (int v = 0; v < me.VertexCount; v++)
                    {
                        int src = oldBase + v * me.Stride[s];
                        int dst = v * ns;
                        // trailing padding of the stride, copied as is
                        foreach (var ne in newEls[mi])
                        {
                            if (ne.Stream != s) continue;
                            var oe = me.Elements.Find(x => x.Usage == ne.Usage && x.UsageIndex == ne.UsageIndex && x.Stream == ne.Stream)!;
                            int osz = TypeSize(oe.Type);
                            Buffer.BlockCopy(Data, src + oe.Offset, buf, dst + ne.Offset, osz);   // widened: zero-filled rest
                        }
                    }
                    data.Write(buf);
                    vb += (uint)buf.Length;
                }
                newVbo[mi] = offs;
            }
            vSize[l] = vb;
            iOff[l] = headerEnd + (uint)data.Length;
            data.Write(Data, (int)(IndexOffset[l] - _headerEnd), (int)IndexBufferSize[l]);
            foreach (var kv in newVbo) Meshes[kv.Key].VertexBufferOffset = kv.Value;
        }
        uint end = headerEnd + (uint)data.Length;
        for (int l = 0; l < 3; l++)
        {
            if (l < LodCount)
            {
                // the LOD table mirrors these; old edge offset pointed at the index data
                if (Lods[l].EdgeGeometryDataOffset == IndexOffset[l]) Lods[l].EdgeGeometryDataOffset = iOff[l];
                Lods[l].VertexBufferSize = vSize[l];
                if (Lods[l].VertexDataOffset != 0) Lods[l].VertexDataOffset = vOff[l];
                if (Lods[l].IndexDataOffset != 0) Lods[l].IndexDataOffset = iOff[l];
                VertexOffset[l] = vOff[l]; IndexOffset[l] = iOff[l]; VertexBufferSize[l] = vSize[l];
            }
            else
            {
                if (VertexOffset[l] != 0) VertexOffset[l] = end;
                if (IndexOffset[l] != 0) IndexOffset[l] = end;
            }
        }
        for (int mi = 0; mi < Meshes.Count; mi++)
        {
            Meshes[mi].Elements = newEls[mi];
            Meshes[mi].Stride = newStride[mi];
            if (mi < Decls.Count) Decls[mi] = newEls[mi];
        }
        Data = data.ToArray();
        Dirty = true;
        return true;
    }

    // ---------------------------------------------------------------- vertices
    /// <summary>A mesh's triangle indices (into its own vertices).</summary>
    public ushort[] ReadIndices(int mesh)
    {
        int l = LodOf(mesh);
        var me = Meshes[mesh];
        int at = (int)(IndexOffset[l] - _headerEnd + me.StartIndex * 2);
        var res = new ushort[me.IndexCount];
        for (int k = 0; k < res.Length; k++) res[k] = BitConverter.ToUInt16(Data, at + 2 * k);
        return res;
    }

    public IEnumerable<int> LodMeshes(int lod)
    {
        if (lod >= LodCount) yield break;
        for (int mi = Lods[lod].MeshIndex; mi < Lods[lod].MeshIndex + Lods[lod].MeshCount; mi++) yield return mi;
    }

    public int LodOf(int mesh)
    {
        for (int l = 0; l < LodCount; l++)
            if (mesh >= Lods[l].MeshIndex && mesh < Lods[l].MeshIndex + Lods[l].MeshCount) return l;
        return -1;
    }

    public VElement? Element(int mesh, VUsage u, int usageIndex = 0)
        => Meshes[mesh].Elements.Find(e => e.Usage == (byte)u && e.UsageIndex == usageIndex);

    int ElementAt(int mesh, VElement e, int v)
    {
        int l = LodOf(mesh);
        var me = Meshes[mesh];
        return (int)(VertexOffset[l] - _headerEnd + me.VertexBufferOffset[e.Stream] + me.Stride[e.Stream] * v + e.Offset);
    }

    public Vector4 ReadVec(int mesh, VElement e, int v)
    {
        int p = ElementAt(mesh, e, v);
        var d = Data;
        switch (e.Type)
        {
            case 0: return new Vector4(BitConverter.ToSingle(d, p), 0, 0, 0);
            case 1: return new Vector4(BitConverter.ToSingle(d, p), BitConverter.ToSingle(d, p + 4), 0, 0);
            case 2: return new Vector4(BitConverter.ToSingle(d, p), BitConverter.ToSingle(d, p + 4), BitConverter.ToSingle(d, p + 8), 1);
            case 3: return new Vector4(BitConverter.ToSingle(d, p), BitConverter.ToSingle(d, p + 4), BitConverter.ToSingle(d, p + 8), BitConverter.ToSingle(d, p + 12));
            case 8: return new Vector4(d[p] / 255f, d[p + 1] / 255f, d[p + 2] / 255f, d[p + 3] / 255f);
            case 13: return new Vector4((float)BitConverter.ToHalf(d, p), (float)BitConverter.ToHalf(d, p + 2), 0, 0);
            case 14: return new Vector4((float)BitConverter.ToHalf(d, p), (float)BitConverter.ToHalf(d, p + 2), (float)BitConverter.ToHalf(d, p + 4), (float)BitConverter.ToHalf(d, p + 6));
            default: throw new MdlException($"can't read vertex element type {e.Type}");
        }
    }

    public void WriteVec(int mesh, VElement e, int v, Vector4 x)
    {
        int p = ElementAt(mesh, e, v);
        var d = Data;
        switch (e.Type)
        {
            case 2:
                BitConverter.TryWriteBytes(d.AsSpan(p), x.X); BitConverter.TryWriteBytes(d.AsSpan(p + 4), x.Y); BitConverter.TryWriteBytes(d.AsSpan(p + 8), x.Z);
                break;
            case 3:
                BitConverter.TryWriteBytes(d.AsSpan(p), x.X); BitConverter.TryWriteBytes(d.AsSpan(p + 4), x.Y); BitConverter.TryWriteBytes(d.AsSpan(p + 8), x.Z); BitConverter.TryWriteBytes(d.AsSpan(p + 12), x.W);
                break;
            case 8:
                d[p] = Unorm(x.X); d[p + 1] = Unorm(x.Y); d[p + 2] = Unorm(x.Z); d[p + 3] = Unorm(x.W);
                break;
            case 14:
                BitConverter.TryWriteBytes(d.AsSpan(p), (Half)x.X); BitConverter.TryWriteBytes(d.AsSpan(p + 2), (Half)x.Y);
                BitConverter.TryWriteBytes(d.AsSpan(p + 4), (Half)x.Z); BitConverter.TryWriteBytes(d.AsSpan(p + 6), (Half)x.W);
                break;
            default: throw new MdlException($"can't write vertex element type {e.Type}");
        }
    }

    static byte Unorm(float f) => (byte)Math.Clamp((int)MathF.Round(f * 255f), 0, 255);

    /// <summary>A vertex's weights as (table slot, 0-255) pairs, zeros left out.</summary>
    public List<(int slot, int w)> ReadWeights(int mesh, int v)
    {
        var res = new List<(int, int)>();
        var we = Element(mesh, VUsage.BlendWeights);
        var ie = Element(mesh, VUsage.BlendIndices);
        if (we == null || ie == null) return res;
        int pw = ElementAt(mesh, we, v), pi = ElementAt(mesh, ie, v);
        int n = Math.Min(TypeSize(we.Type), TypeSize(ie.Type));
        for (int k = 0; k < n; k++)
            if (Data[pw + k] != 0) res.Add((Data[pi + k], Data[pw + k]));
        return res;
    }

    public int WeightSlots(int mesh)
    {
        var we = Element(mesh, VUsage.BlendWeights);
        var ie = Element(mesh, VUsage.BlendIndices);
        if (we == null || ie == null) return 0;
        return Math.Min(TypeSize(we.Type), TypeSize(ie.Type));
    }

    /// <summary>Writes a vertex's weights (table slot, 0-255; must add up to
    /// 255 and fit the vertex's slots). Unused slots are zeroed.</summary>
    public void WriteWeights(int mesh, int v, IReadOnlyList<(int slot, int w)> ws)
    {
        var we = Element(mesh, VUsage.BlendWeights)!;
        var ie = Element(mesh, VUsage.BlendIndices)!;
        int n = WeightSlots(mesh);
        if (ws.Count > n) throw new MdlException("too many weights for this vertex format");
        int pw = ElementAt(mesh, we, v), pi = ElementAt(mesh, ie, v);
        for (int k = 0; k < n; k++)
        {
            Data[pw + k] = k < ws.Count ? (byte)ws[k].w : (byte)0;
            Data[pi + k] = k < ws.Count ? (byte)ws[k].slot : (byte)0;
        }
    }

    // ---------------------------------------------------------------- shape keys
    public int ShapeIndex(string name) => Shapes.FindIndex(x => x.Name == name);

    /// <summary>The LOD a shape-mesh entry belongs to (by its place in the shape's runs).</summary>
    IEnumerable<(int lod, MdlShapeMesh sm)> ShapeRuns(MdlShape sh)
    {
        for (int l = 0; l < 3; l++)
            for (int k = 0; k < sh.MeshCount[l]; k++)
            {
                int at = sh.MeshStart[l] + k;
                if (at < ShapeMeshes.Count) yield return (l, ShapeMeshes[at]);
            }
    }

    /// <summary>Which mesh of a LOD starts at this index offset.</summary>
    int MeshAt(int lod, uint startIndex)
    {
        foreach (int mi in LodMeshes(lod))
            if (Meshes[mi].StartIndex == startIndex) return mi;
        return -1;
    }

    /// <summary>{base vertex: replacement vertex} of a shape on a mesh (both
    /// counted from the mesh's first vertex). Empty if the shape doesn't touch it.</summary>
    public Dictionary<int, int> ShapeTargets(string name, int mesh)
    {
        var res = new Dictionary<int, int>();
        int si = ShapeIndex(name);
        if (si < 0) return res;
        int lod = LodOf(mesh);
        var idx = ReadIndices(mesh);
        foreach (var (l, sm) in ShapeRuns(Shapes[si]))
        {
            if (l != lod || sm.MeshIndexOffset != Meshes[mesh].StartIndex) continue;
            for (int k = 0; k < sm.ValueCount; k++)
            {
                var (bi, rv) = ShapeValues[(int)sm.ValueOffset + k];
                if (bi < idx.Length) res[idx[bi]] = rv;
            }
        }
        return res;
    }

    /// <summary>Appends copies of vertices to meshes ({mesh: [source vertex,
    /// ...]}); the copies get the next vertex numbers in order. Rebuilds the
    /// vertex data; indices are untouched.</summary>
    public bool AppendVertices(Dictionary<int, List<int>> copies, out string why)
    {
        if (!CanRebuildData(out why)) return false;
        foreach (var kv in copies)
            if (Meshes[kv.Key].VertexCount + kv.Value.Count > 0xFFFF) { why = "a part would have more than 65535 points"; return false; }
        var data = new MemoryStream();
        uint headerEnd = (uint)_headerEnd;
        uint[] vOff = new uint[3], iOff = new uint[3], vSize = new uint[3];
        for (int l = 0; l < LodCount; l++)
        {
            vOff[l] = headerEnd + (uint)data.Length;
            uint vb = 0;
            var newVbo = new Dictionary<int, uint[]>();
            foreach (int mi in LodMeshes(l))
            {
                var me = Meshes[mi];
                var offs = new uint[3];
                copies.TryGetValue(mi, out var add);
                for (int st = 0; st < 3; st++)
                {
                    if (me.Stride[st] == 0) continue;
                    offs[st] = vb;
                    int oldBase = (int)(VertexOffset[l] - _headerEnd + me.VertexBufferOffset[st]);
                    int n = me.Stride[st] * me.VertexCount;
                    data.Write(Data, oldBase, n);
                    vb += (uint)n;
                    if (add != null)
                        foreach (int src in add)
                        {
                            data.Write(Data, oldBase + src * me.Stride[st], me.Stride[st]);
                            vb += me.Stride[st];
                        }
                }
                newVbo[mi] = offs;
            }
            vSize[l] = vb;
            iOff[l] = headerEnd + (uint)data.Length;
            data.Write(Data, (int)(IndexOffset[l] - _headerEnd), (int)IndexBufferSize[l]);
            foreach (var kv in newVbo) Meshes[kv.Key].VertexBufferOffset = kv.Value;
        }
        uint end = headerEnd + (uint)data.Length;
        for (int l = 0; l < 3; l++)
        {
            if (l < LodCount)
            {
                if (Lods[l].EdgeGeometryDataOffset == IndexOffset[l]) Lods[l].EdgeGeometryDataOffset = iOff[l];
                Lods[l].VertexBufferSize = vSize[l];
                if (Lods[l].VertexDataOffset != 0) Lods[l].VertexDataOffset = vOff[l];
                if (Lods[l].IndexDataOffset != 0) Lods[l].IndexDataOffset = iOff[l];
                VertexOffset[l] = vOff[l]; IndexOffset[l] = iOff[l]; VertexBufferSize[l] = vSize[l];
            }
            else
            {
                if (VertexOffset[l] != 0) VertexOffset[l] = end;
                if (IndexOffset[l] != 0) IndexOffset[l] = end;
            }
        }
        foreach (var kv in copies) Meshes[kv.Key].VertexCount = (ushort)(Meshes[kv.Key].VertexCount + kv.Value.Count);
        Data = data.ToArray();
        Dirty = true;
        return true;
    }

    /// <summary>Adds the shape key `name`, or replaces it, so that with it on
    /// each listed point ({mesh: {base vertex: position}}) sits at the given
    /// position. Points not listed don't move with this shape. Replacement
    /// points the shape already had are reused; new ones are copies of their
    /// base point (same weights, normals, UVs). Returns the number of shape
    /// values written, or -1 (why says what stopped it).</summary>
    /// <summary>Index slots the last SetShape couldn't redirect (past 65535 in a part).</summary>
    public int Unreachable;

    public int SetShape(string name, Dictionary<int, Dictionary<int, Vector3>> targets, out string why)
    {
        why = "";
        Unreachable = 0;
        int si = ShapeIndex(name);
        // 1. what the shape points at now, per mesh
        var have = new Dictionary<int, Dictionary<int, int>>();
        for (int mi = 0; mi < Meshes.Count; mi++)
        {
            var t = ShapeTargets(name, mi);
            if (t.Count > 0) have[mi] = t;
        }
        // 2. new replacement points where needed
        var copies = new Dictionary<int, List<int>>();
        var repl = new Dictionary<int, Dictionary<int, int>>();
        foreach (var (mi, pts) in targets)
        {
            if (pts.Count == 0) continue;
            have.TryGetValue(mi, out var old);
            var map = new Dictionary<int, int>();
            int next = Meshes[mi].VertexCount;
            foreach (var v in pts.Keys.OrderBy(x => x))
            {
                if (old != null && old.TryGetValue(v, out var r)) { map[v] = r; continue; }
                if (!copies.TryGetValue(mi, out var list)) copies[mi] = list = new List<int>();
                list.Add(v);
                map[v] = next + list.Count - 1;
            }
            repl[mi] = map;
        }
        // 3. count values first - the header holds at most 65535
        var idxCache = new Dictionary<int, ushort[]>();
        int newValues = 0;
        foreach (var (mi, map) in repl)
        {
            var idx = idxCache[mi] = ReadIndices(mi);
            foreach (var x in idx) if (map.ContainsKey(x)) newValues++;
        }
        int otherValues = 0;
        for (int k = 0; k < Shapes.Count; k++)
            if (k != si) foreach (var (_l, sm) in ShapeRuns(Shapes[k])) otherValues += (int)sm.ValueCount;
        if (otherValues + newValues > 0xFFFF) { why = $"too many shape points ({otherValues + newValues}, the game allows 65535)"; return -1; }
        if (copies.Count > 0 && !AppendVertices(copies, out why)) return -1;
        // 4. positions of the replacement points
        foreach (var (mi, pts) in targets)
        {
            if (!repl.TryGetValue(mi, out var map)) continue;
            var pe = Element(mi, VUsage.Position);
            if (pe == null) continue;
            foreach (var (v, p) in pts)
            {
                var old = ReadVec(mi, pe, map[v]);
                WriteVec(mi, pe, map[v], new Vector4(p, pe.Type == 2 ? 1 : old.W));
            }
        }
        // 5. rebuild the shape tables: every other shape as it was, this one new
        if (si < 0)
        {
            var sh = new MdlShape { Name = name, NameOffset = AddString(name) };
            Shapes.Add(sh);
            si = Shapes.Count - 1;
        }
        var runs = new List<List<(int lod, MdlShapeMesh sm, List<(ushort, ushort)> vals)>>();
        for (int k = 0; k < Shapes.Count; k++)
        {
            var list = new List<(int, MdlShapeMesh, List<(ushort, ushort)>)>();
            if (k != si)
            {
                foreach (var (l, sm) in ShapeRuns(Shapes[k]))
                    list.Add((l, sm, ShapeValues.GetRange((int)sm.ValueOffset, (int)sm.ValueCount)));
            }
            else
            {
                for (int l = 0; l < LodCount; l++)
                    foreach (int mi in LodMeshes(l))
                    {
                        if (!repl.TryGetValue(mi, out var map) || map.Count == 0) continue;
                        var idx = idxCache[mi];
                        var vals = new List<(ushort, ushort)>();
                        for (int q = 0; q < idx.Length; q++)
                        {
                            if (!map.TryGetValue(idx[q], out var rv)) continue;
                            // a shape can only reach a part's first 65536 index slots
                            if (q > 0xFFFF) { Unreachable++; continue; }
                            vals.Add(((ushort)q, (ushort)rv));
                        }
                        if (vals.Count > 0)
                            list.Add((l, new MdlShapeMesh { MeshIndexOffset = Meshes[mi].StartIndex }, vals));
                    }
            }
            runs.Add(list);
        }
        var nsm = new List<MdlShapeMesh>();
        var nsv = new List<(ushort, ushort)>();
        for (int k = 0; k < Shapes.Count; k++)
        {
            var sh = Shapes[k];
            for (int l = 0; l < 3; l++)
            {
                var mine = runs[k].FindAll(x => x.lod == l);
                sh.MeshStart[l] = (ushort)(mine.Count > 0 || sh.MeshCount[l] > 0 || k == si ? nsm.Count : sh.MeshStart[l]);
                sh.MeshCount[l] = (ushort)mine.Count;
                foreach (var (_l, sm, vals) in mine)
                {
                    nsm.Add(new MdlShapeMesh { MeshIndexOffset = sm.MeshIndexOffset, ValueCount = (uint)vals.Count, ValueOffset = (uint)nsv.Count });
                    nsv.AddRange(vals);
                }
            }
        }
        ShapeMeshes = nsm;
        ShapeValues = nsv;
        ShapeCount = (ushort)Shapes.Count;
        ShapeMeshCount = (ushort)nsm.Count;
        ShapeValueCount = (ushort)nsv.Count;
        Dirty = true;
        return newValues;
    }
}

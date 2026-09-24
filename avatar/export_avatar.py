"""blender -b <avatar.blend> --python avatar/export_avatar.py -- <out.bin>

Exports the co-op player avatar (armature ARM_NAME + its skinned meshes) to the mod's binary format KBAV v1,
everything already in Unity space (Y up, left-handed, metres), the kobold facing +Z at rest:

  "KBAV" u32 version
  f32 eye_height, f32 hip_height, f32 leg_length, f32 arm_length
  u32 bone_count, per bone: str name, i32 parent, f32x3 local_pos, f32x4 local_rot (xyzw), f32x3 local_scale
  u32 role_count, per role: str role, i32 bone
  u32 vertex_count, f32x3 pos[], f32x3 normal[], f32x2 uv[], (u16x4 bone idx, f32x4 weight)[]
  u32 submesh_count, per submesh: u32 material, u32 index_count, u32 indices[]
  u32 shape_count, per shape: str name, u32 n, (u32 vertex, f32x3 delta)[]
  u32 material_count, per material: str name, f32x4 base colour, f32 smoothness, u32 png_len, png bytes
Strings are u16 length + utf8. Bind poses are computed in the mod from the rest bones, so the vertices are in
the armature's (root) space at rest.
"""
import io
import json
import math
import os
import struct
import sys

import bpy
import numpy as np
from mathutils import Matrix, Vector

ARGS = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
CFG_PATH = ARGS[1] if len(ARGS) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "avatar_config.json")
CFG_DIR = os.path.dirname(os.path.abspath(CFG_PATH))
OUT = ARGS[0] if ARGS else os.path.join(CFG_DIR, "kobold_avatar.bin")
CFG = json.load(open(CFG_PATH))
ARM = bpy.data.objects[CFG["armature"]]
MESHES = [bpy.data.objects[n] for n in CFG["meshes"]]
TEX_SIZE = CFG.get("texture_size", 1024)

# Blender (right-handed, Z up, character facing -Y) -> Unity (left-handed, Y up, facing +Z):
# unity = (x, z, -y) is a proper rotation; then mirror x -> -x to change handedness.
# Combined: unity = (-x, z, -y)  (a reflection, so triangle winding flips)
C = np.array([[-1, 0, 0], [0, 0, 1], [0, -1, 0]], float)


def to_u_point(v):
    return C @ np.asarray(v, float)


def to_u_matrix(m3):
    """a rotation matrix in Blender space -> the same rotation expressed in Unity space"""
    return C @ np.asarray(m3, float) @ C.T


def quat_from_matrix(m):
    """3x3 rotation (Unity space, proper) -> (x, y, z, w)"""
    m = np.asarray(m, float)
    t = np.trace(m)
    if t > 0:
        s = math.sqrt(t + 1.0) * 2
        w, x, y, z = .25 * s, (m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = math.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        w, x, y, z = (m[2, 1] - m[1, 2]) / s, .25 * s, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s
    elif m[1, 1] > m[2, 2]:
        s = math.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        w, x, y, z = (m[0, 2] - m[2, 0]) / s, (m[0, 1] + m[1, 0]) / s, .25 * s, (m[1, 2] + m[2, 1]) / s
    else:
        s = math.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        w, x, y, z = (m[1, 0] - m[0, 1]) / s, (m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, .25 * s
    return x, y, z, w


buf = bytearray()


def wstr(s):
    b = s.encode("utf8")
    buf.extend(struct.pack("<H", len(b)))
    buf.extend(b)


def wf(*xs):
    buf.extend(struct.pack(f"<{len(xs)}f", *xs))


def wi(*xs):
    buf.extend(struct.pack(f"<{len(xs)}i", *xs))


def wu(*xs):
    buf.extend(struct.pack(f"<{len(xs)}I", *xs))


# ------------------------------------------------------------------ bones (rest pose, armature space)
bones = [b for b in ARM.data.bones if b.name in CFG.get("keep_bones", [b.name for b in ARM.data.bones]) or not CFG.get("keep_bones")]
index = {b.name: i for i, b in enumerate(bones)}
S = CFG.get("scale", 1.0)
world = {}
for b in bones:
    M = ARM.matrix_world @ b.matrix_local
    R = to_u_matrix(np.array(M.to_3x3().normalized()))
    # Unity rotations must be proper: C flips handedness, so fix the bone frame by negating one axis
    if np.linalg.det(R) < 0:
        R[:, 0] *= -1
    world[b.name] = (to_u_point(M.translation) * S, R)

buf.extend(b"KBAV")
wu(1)
wf(*[CFG["metrics"][k] * S for k in ("eye_height", "hip_height", "leg_length", "arm_length")])
wu(len(bones))
for b in bones:
    p, R = world[b.name]
    par = b.parent if b.parent and b.parent.name in index else None
    if par is None:
        lp, lR = p, R
    else:
        pp, pR = world[par.name]
        lp = pR.T @ (p - pp)
        lR = pR.T @ R
    wstr(b.name)
    wi(index[par.name] if par else -1)
    wf(*lp)
    wf(*quat_from_matrix(lR))
    wf(1, 1, 1)
roles = CFG["roles"]
wu(len(roles))
for role, bone in roles.items():
    wstr(role)
    wi(index.get(bone, -1))

# ------------------------------------------------------------------ mesh (merged, evaluated at rest)
for ob in MESHES:  # shapes baked into the base (e.g. a neutral chest), everything else off
    if ob.data.shape_keys:
        for kb in ob.data.shape_keys.key_blocks[1:]:
            kb.value = CFG.get("bake_shapes", {}).get(kb.name, 0.0)
pos, nrm, uv, bidx, bw, tris_by_mat = [], [], [], [], [], {}
shape_deltas = {}
mat_names = []
vbase = 0
for ob in MESHES:
    arm_mod = next((m for m in ob.modifiers if m.type == "ARMATURE"), None)
    if arm_mod:
        arm_mod.show_viewport = False
    dg = bpy.context.evaluated_depsgraph_get()
    ev = ob.evaluated_get(dg)
    me = ev.to_mesh()
    me.calc_loop_triangles()
    Mw = ob.matrix_world
    groups = {g.index: g.name for g in ob.vertex_groups}
    # split vertices per loop where UVs / normals differ: one Unity vertex per unique (vertex, uv, normal)
    uvl = me.uv_layers.active.data if me.uv_layers.active else None
    key_to_idx = {}
    local_tris = {}
    for tri in me.loop_triangles:
        mi = tri.material_index
        name = ob.material_slots[mi].material.name if ob.material_slots and ob.material_slots[mi].material else "Default"
        if name not in mat_names:
            mat_names.append(name)
        out = []
        for li, vi in zip(tri.loops, tri.vertices):
            u = tuple(round(c, 5) for c in uvl[li].uv) if uvl else (0.0, 0.0)
            n = tuple(round(c, 3) for c in me.corner_normals[li].vector)
            key = (vi, u, n)
            if key not in key_to_idx:
                key_to_idx[key] = vbase + len(key_to_idx)
                v = me.vertices[vi]
                pos.append(to_u_point(Mw @ v.co) * S)
                nrm.append(to_u_point((Mw.to_3x3() @ Vector(n)).normalized()))
                uv.append(u)
                ws = sorted(((g.weight, groups[g.group]) for g in v.groups if groups.get(g.group) in index and g.weight > 0),
                            reverse=True)[:4]
                tot = sum(w for w, _ in ws) or 1.0
                ids = [index[n_] for _, n_ in ws] + [0] * (4 - len(ws))
                wts = [w / tot for w, _ in ws] + [0.0] * (4 - len(ws))
                if not ws:
                    ids[0], wts[0] = index[roles["hips"]], 1.0
                bidx.append(ids)
                bw.append(wts)
            out.append(key_to_idx[key])
        tris_by_mat.setdefault(mat_names.index(name), []).extend(out[::-1])  # reflection flips winding
    # shape keys we keep (blinks, mouth), as per-vertex deltas on the split vertices
    if ob.data.shape_keys:
        basis = ob.data.shape_keys.reference_key
        for kb in ob.data.shape_keys.key_blocks:
            if kb.name not in CFG.get("shapes", []):
                continue
            d = shape_deltas.setdefault(kb.name, {})
            for (vi, _, _), idx in key_to_idx.items():
                delta = Vector(kb.data[vi].co) - Vector(basis.data[vi].co)  # relative to the basis, like Unity
                if delta.length > 1e-6:
                    d[idx] = to_u_point(Mw.to_3x3() @ delta) * S
    vbase += len(key_to_idx)
    ev.to_mesh_clear()
    if arm_mod:
        arm_mod.show_viewport = True

wu(len(pos))
for p in pos:
    wf(*p)
for n in nrm:
    wf(*n)
for u in uv:
    wf(u[0], u[1])
for ids, wts in zip(bidx, bw):
    buf.extend(struct.pack("<4H", *ids))
    wf(*wts)
wu(len(tris_by_mat))
for mi, idx in sorted(tris_by_mat.items()):
    wu(mi, len(idx))
    wu(*idx)
wu(len(shape_deltas))
for name, d in shape_deltas.items():
    wstr(name)
    wu(len(d))
    for vi, delta in d.items():
        wu(vi)
        wf(*delta)

# ------------------------------------------------------------------ materials (base colour texture, downsized)
wu(len(mat_names))
for name in mat_names:
    spec = CFG["materials"].get(name, {})
    wstr(name)
    wf(*spec.get("color", [1, 1, 1, 1]))
    wf(spec.get("smoothness", 0.25))
    tex = spec.get("texture")
    png = b""
    if tex:  # already sized/prepared outside Blender (see avatar/README)
        png = open(os.path.join(CFG_DIR, tex), "rb").read()
    wu(len(png))
    buf.extend(png)

open(OUT, "wb").write(buf)
print(f"KBAV: {len(bones)} bones, {len(pos)} verts, {sum(len(v) for v in tris_by_mat.values()) // 3} tris, "
      f"{len(mat_names)} materials, {len(shape_deltas)} shapes, {len(buf) / 1e6:.2f} MB -> {OUT}")

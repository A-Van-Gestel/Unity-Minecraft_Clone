"""Generate rotation matrices for BurstCustomMeshRotationUtility.cs.
Convention: Facing6 block front faces TOWARD the player (opposite the facing direction).
- South(0): front stays +Z (identity)
- North(1): front→-Z (180° Y)
- Top(2): front→-Y (+90° X)
- Bottom(3): front→+Y (-90° X)
- West(4): front→+X (+90° Y in standard math)
- East(5): front→-X (-90° Y in standard math)
"""
import numpy as np
from scipy.spatial.transform import Rotation as R


def rot(axis, deg):
    return R.from_euler(axis, deg, degrees=True).as_matrix()


def fmt_mat(m, label):
    c0, c1, c2 = m[:, 0], m[:, 1], m[:, 2]

    def f(v):
        parts = []
        for x in v:
            x = int(round(x))
            parts.append(f"{x:2d}" if x >= 0 else f"{x:d}")
        return f"new float3({parts[0]}f, {parts[1]}f, {parts[2]}f)"

    return f"            // {label}\n            new float3x3({f(c0)}, {f(c1)}, {f(c2)}),"


I = np.eye(3)

# ===== Facing6 (block front faces toward player) =====
facing6 = [
    (I, "South(0): identity, front=+Z"),
    (rot('y', 180), "North(1): 180° Y, front=-Z"),
    (rot('x', 90), "Top(2): +90° X, front=-Y"),
    (rot('x', -90), "Bottom(3): -90° X, front=+Y"),
    (rot('y', 90), "West(4): +90° Y, front=+X"),
    (rot('y', -90), "East(5): -90° Y, front=-X"),
]

# Verify: front (+Z) should map to the expected direction
expected = [(0, 0, 1), (0, 0, -1), (0, -1, 0), (0, 1, 0), (1, 0, 0), (-1, 0, 0)]
for i, (m, l) in enumerate(facing6):
    got = tuple(int(round(x)) for x in m @ [0, 0, 1])
    ok = got == expected[i]
    print(f"Facing6[{i}]: front→{got} expected={expected[i]} {'✓' if ok else '✗'}")

# Cross-check against BurstFacing6MeshUtility face remap LUT
# face indices: 0=Back(-Z), 1=Front(+Z), 2=Top(+Y), 3=Bottom(-Y), 4=Left(-X), 5=Right(+X)
face_normals = {0: (0, 0, -1), 1: (0, 0, 1), 2: (0, 1, 0), 3: (0, -1, 0), 4: (-1, 0, 0), 5: (1, 0, 0)}
inv_normals = {v: k for k, v in face_normals.items()}

lut = [
    [0, 1, 2, 3, 4, 5], [1, 0, 2, 3, 5, 4], [3, 2, 0, 1, 4, 5],
    [2, 3, 1, 0, 4, 5], [5, 4, 2, 3, 0, 1], [4, 5, 2, 3, 1, 0],
]

print("\nCross-check vs face remap LUT:")
for fi, (m, l) in enumerate(facing6):
    ok = True
    for wf in range(6):
        wn = np.array(face_normals[wf])
        # Under this rotation, which original face ends up at world face wf?
        # inv_rotate the world normal to find original face
        orig_n = tuple(int(round(x)) for x in m.T @ wn)
        orig_face = inv_normals.get(orig_n, -1)
        expected_face = lut[fi][wf]
        if orig_face != expected_face:
            print(f"  MISMATCH facing={fi} wf={wf}: got={orig_face} expected={expected_face}")
            ok = False
    if ok:
        print(f"  Facing6[{fi}] ({l}): all 6 faces match LUT ✓")

# ===== Axis3 =====
axis3 = [
    (I, "Axis Y(0): identity"),
    (rot('z', -90), "Axis X(1): -90° Z, top→+X"),
    (rot('x', 90), "Axis Z(2): +90° X, top→+Z"),
]
print("\nAxis3 verification:")
for i, (m, l) in enumerate(axis3):
    up = tuple(int(round(x)) for x in m @ [0, 1, 0])
    exp = [(0, 1, 0), (1, 0, 0), (0, 0, 1)]
    print(f"  [{i}] up→{up} expected={exp[i]} {'✓' if up == exp[i] else '✗'}")

# Cross-check vs BurstAxis3MeshUtility face remap
axis_lut = [[0, 1, 2, 3, 4, 5], [0, 1, 4, 5, 3, 2], [3, 2, 0, 1, 4, 5]]
print("  Cross-check vs Axis3 face remap LUT:")
for ai, (m, l) in enumerate(axis3):
    ok = True
    for wf in range(6):
        wn = np.array(face_normals[wf])
        orig_n = tuple(int(round(x)) for x in m.T @ wn)
        orig_face = inv_normals.get(orig_n, -1)
        expected_face = axis_lut[ai][wf]
        if orig_face != expected_face:
            print(f"    MISMATCH axis={ai} wf={wf}: got={orig_face} expected={expected_face}")
            ok = False
    if ok:
        print(f"    Axis3[{ai}] ({l}): all 6 faces match LUT ✓")

# ===== HorizontalOnly =====
horiz = [
    (I, "North(0): identity"),
    (rot('y', 180), "South(1): 180° Y"),
    (rot('y', 90), "West(2): +90° Y"),
    (rot('y', -90), "East(3): -90° Y"),
]

# ===== Facing6Roll2 =====
facing6roll2 = []
facing_names = ["South", "North", "Top", "Bottom", "West", "East"]
for fi in range(6):
    fm = facing6[fi][0]
    # The facing axis is where the front (+Z) ends up
    facing_dir = fm @ np.array([0, 0, 1])
    for roll in range(4):
        if roll == 0:
            rm = I
        else:
            # Negate angle: the LUT defines CW looking along facing dir,
            # but scipy's from_rotvec is CCW around the positive axis (right-hand rule).
            rm = R.from_rotvec(np.radians(-roll * 90) * facing_dir).as_matrix()
        combined = rm @ fm
        facing6roll2.append((combined, f"Facing={facing_names[fi]}({fi}), Roll={roll}"))

# Verify all 24 are orthogonal with det=+1
for i, (m, l) in enumerate(facing6roll2):
    det = np.linalg.det(m)
    assert abs(det - 1.0) < 1e-9, f"Bad det at {i}: {det}"

# Cross-check Facing6Roll2 vs its LUT
f6r2_lut = [
    [0, 1, 2, 3, 4, 5], [0, 1, 4, 5, 3, 2], [0, 1, 3, 2, 5, 4], [0, 1, 5, 4, 2, 3],
    [1, 0, 2, 3, 5, 4], [1, 0, 4, 5, 2, 3], [1, 0, 3, 2, 4, 5], [1, 0, 5, 4, 3, 2],
    [3, 2, 0, 1, 4, 5], [5, 4, 0, 1, 3, 2], [2, 3, 0, 1, 5, 4], [4, 5, 0, 1, 2, 3],
    [2, 3, 1, 0, 4, 5], [4, 5, 1, 0, 3, 2], [3, 2, 1, 0, 5, 4], [5, 4, 1, 0, 2, 3],
    [5, 4, 2, 3, 0, 1], [2, 3, 4, 5, 0, 1], [4, 5, 3, 2, 0, 1], [3, 2, 5, 4, 0, 1],
    [4, 5, 2, 3, 1, 0], [3, 2, 4, 5, 1, 0], [5, 4, 3, 2, 1, 0], [2, 3, 5, 4, 1, 0],
]
print("\nFacing6Roll2 cross-check vs LUT:")
all_ok = True
for i, (m, l) in enumerate(facing6roll2):
    for wf in range(6):
        wn = np.array(face_normals[wf])
        orig_n = tuple(int(round(x)) for x in m.T @ wn)
        orig_face = inv_normals.get(orig_n, -1)
        if orig_face != f6r2_lut[i][wf]:
            print(f"  MISMATCH [{i}] {l} wf={wf}: got={orig_face} expected={f6r2_lut[i][wf]}")
            all_ok = False
if all_ok:
    print("  All 24×6 = 144 entries match ✓")

# ===== Output C# =====
print("\n" + "=" * 80)
print("C# CODE")
print("=" * 80)

for name, arr in [("s_axis3Matrices", axis3), ("s_facing6Matrices", facing6),
                  ("s_facing6Roll2Matrices", facing6roll2), ("s_horizontalOnlyMatrices", horiz)]:
    print(f"\n        private static readonly float3x3[] {name} =")
    print("        {")
    for m, l in arr:
        print(fmt_mat(m, l))
    print("        };")

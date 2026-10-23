"""Turns pose moves described in the kerbal's own directions into the bone rotations MunShot's pose files hold.

Directions are the kerbal's body space, as in the rig's rest pose (rest_pose.txt): +X is the kerbal's right, +Y up, +Z
forward (the way the kerbal faces). The rest pose is a T-pose: arms straight out to the sides, palms down, fingers along
the arm, face forward. Every move starts from the rest pose and is turned into the bone's local rotation (relative to
its parent at rest), which is what the game's animation sets and what a pose file's "bones" entry holds.

Angles follow Unity: a positive turn about an axis goes the way Quaternion.AngleAxis does, e.g. +90 about +Z turns +X
into +Y, +90 about +X turns +Z (forward) into -Y (down), +90 about +Y turns +Z into +X.
"""
import math
import os

HERE = os.path.dirname(os.path.abspath(__file__))
PREFIX = 'bone_kerbal_'

X, Y, Z = (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0)


# ---- Quaternions (x, y, z, w), as Unity's ----

def mul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def inv(q):
    return (-q[0], -q[1], -q[2], q[3])


def turn(axis, degrees):
    """Quaternion.AngleAxis(degrees, axis)."""
    n = math.sqrt(sum(c * c for c in axis))
    s = math.sin(math.radians(degrees) / 2) / n
    return (axis[0] * s, axis[1] * s, axis[2] * s, math.cos(math.radians(degrees) / 2))


def rotate(q, v):
    x, y, z, _ = mul(mul(q, (v[0], v[1], v[2], 0.0)), inv(q))
    return (x, y, z)


def to_euler(q):
    """Unity's Euler angles (Quaternion.eulerAngles: Z, then X, then Y), in degrees, 0 to 360."""
    x, y, z, w = q
    m02 = 2 * (x * z + w * y)
    m10 = 2 * (x * y + w * z)
    m11 = 1 - 2 * (x * x + z * z)
    m12 = 2 * (y * z - w * x)
    m22 = 1 - 2 * (x * x + y * y)
    ex = math.degrees(math.asin(max(-1.0, min(1.0, -m12))))
    if abs(m12) < 0.9999:
        ey = math.degrees(math.atan2(m02, m22))
        ez = math.degrees(math.atan2(m10, m11))
    else:  # looking straight up or down: Y and Z turn about the same axis
        m00 = 1 - 2 * (y * y + z * z)
        m20 = 2 * (x * z - w * y)
        ey = math.degrees(math.atan2(-m20, m00))
        ez = 0.0
    return tuple(round(a % 360.0, 2) % 360.0 for a in (ex, ey, ez))


def from_euler(e):
    """Quaternion.Euler(x, y, z)."""
    return mul(mul(turn(Y, e[1]), turn(X, e[0])), turn(Z, e[2]))


def same_rotation(a, b, tolerance=1e-3):
    return abs(abs(sum(p * q for p, q in zip(a, b))) - 1.0) < tolerance


# ---- The rig at rest ----

def load_rest(path=os.path.join(HERE, 'rest_pose.txt')):
    """{bone: (local rotation, rotation in body space, parent)}, names without the bone_kerbal_ prefix."""
    rest = {}
    for line in open(path, encoding='utf-8'):
        if line.startswith('#') or not line.strip():
            continue
        f = line.split()
        name = f[0][len(PREFIX):] if f[0].startswith(PREFIX) else f[0]
        parent = f[9][len(PREFIX):] if f[9].startswith(PREFIX) else f[9]
        rest[name] = (tuple(map(float, f[1:5])), tuple(map(float, f[5:9])), parent)
    return rest


class Pose:
    """One pose being written: each move sets a bone's local rotation; bones not moved aren't in the pose."""

    def __init__(self, rest):
        self.rest = rest
        self.bones = {}
        self.turns = {}

    def turn_animated(self, bone, rotation):
        """
        Turns a bone further from wherever the animation (e.g. the pose's base clip) has it, by a body-space rotation
        (from turn()), given as if the bone's parent were at rest. Goes in the pose file's "turns".
        """
        self._check(bone)
        if bone in self.bones:
            raise ValueError(f"'{bone}' is already set by this pose; a bone is either set or turned.")
        _, _, parent = self.rest[bone]
        parent_world = self.rest[parent][1] if parent in self.rest else (0.0, 0.0, 0.0, 1.0)
        self.turns[bone] = mul(mul(inv(parent_world), rotation), parent_world)

    def _check(self, bone):
        if bone not in self.rest:
            raise KeyError(f"No bone '{bone}' in the rig (names are without '{PREFIX}').")

    def at_rest(self, *bones):
        """Holds bones in their rest pose (e.g. a straight elbow or finger), whatever the animation does."""
        for bone in bones:
            self._check(bone)
            self.bones[bone] = self.rest[bone][0]

    def turn(self, bone, rotation):
        """Turns a bone by a body-space rotation (from turn()), starting from rest, with its parent at rest."""
        self._check(bone)
        local, world, parent = self.rest[bone]
        parent_world = self.rest[parent][1] if parent in self.rest else (0.0, 0.0, 0.0, 1.0)
        self.bones[bone] = mul(inv(parent_world), mul(rotation, world))

    def bend(self, bone, axis, degrees):
        """Turns a bone about one of its own axes (X, Y or Z), on top of where this pose has it (or rest)."""
        self._check(bone)
        start = self.bones.get(bone, self.rest[bone][0])
        self.bones[bone] = mul(start, turn(axis, degrees))

    def world(self, bone):
        """A bone's rotation in body space with this pose applied to it and its parents (unposed ones at rest)."""
        if bone not in self.rest:
            return (0.0, 0.0, 0.0, 1.0)
        _, _, parent = self.rest[bone]
        return mul(self.world(parent), self.bones.get(bone, self.rest[bone][0]))

    def aim(self, bone, along, target, side, target_side):
        """
        Points a bone: its own axis `along` (e.g. (-1, 0, 0), the way it points at rest) goes to body-space direction
        `target`, and its axis `side` (e.g. the palm's) goes as near `target_side` as it can while doing so. Works
        down a chain: aim a parent first, and its children are aimed relative to where it now is.
        """
        self._check(bone)
        rest_world = self.rest[bone][1]
        frame_from = _frame(rotate(rest_world, along), rotate(rest_world, side))
        frame_to = _frame(target, target_side)
        world = mul(mul(frame_to, inv(frame_from)), rest_world)
        _, _, parent = self.rest[bone]
        self.bones[bone] = mul(inv(self.world(parent)), world)

    def to_json_bones(self):
        """The pose file's "bones": Unity Euler angles, checked to give back the same rotation."""
        return _to_euler_list(self.bones)

    def to_json_turns(self):
        """The pose file's "turns", the same way."""
        return _to_euler_list(self.turns)


def _to_euler_list(rotations):
    """Rotations as Unity Euler angles, checked to give back the same rotation."""
    out = {}
    for bone, q in rotations.items():
        e = to_euler(q)
        if not same_rotation(from_euler(e), q, 1e-3):
            raise ValueError(f'{bone}: Euler angles {e} do not round-trip')
        out[bone] = [round(a, 2) for a in e]
    return out


def _normalize(v):
    n = math.sqrt(sum(c * c for c in v))
    return tuple(c / n for c in v)


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def _frame(forward, up):
    """The rotation that takes +X to `forward` and +Y as near `up` as it can (made square to `forward`)."""
    x = _normalize(forward)
    z = _normalize(_cross(x, up))
    y = _cross(z, x)
    # Rotation matrix columns x, y, z -> quaternion
    m00, m01, m02 = x[0], y[0], z[0]
    m10, m11, m12 = x[1], y[1], z[1]
    m20, m21, m22 = x[2], y[2], z[2]
    trace = m00 + m11 + m22
    if trace > 0:
        s = math.sqrt(trace + 1.0) * 2
        return ((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25 * s)
    if m00 > m11 and m00 > m22:
        s = math.sqrt(1.0 + m00 - m11 - m22) * 2
        return (0.25 * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s)
    if m11 > m22:
        s = math.sqrt(1.0 + m11 - m00 - m22) * 2
        return ((m01 + m10) / s, 0.25 * s, (m12 + m21) / s, (m02 - m20) / s)
    s = math.sqrt(1.0 + m22 - m00 - m11) * 2
    return ((m02 + m20) / s, (m12 + m21) / s, 0.25 * s, (m10 - m01) / s)


def direction(pose, bone, rest_direction):
    """
    Where a body-space direction attached to a bone at rest (e.g. +X, the way the right arm points) ends up once
    posed, with the bone's parent at rest. For checking a move before trying it in game.
    """
    _, world_rest, parent = pose.rest[bone]
    parent_world = pose.rest[parent][1] if parent in pose.rest else (0.0, 0.0, 0.0, 1.0)
    world = mul(parent_world, pose.bones.get(bone, pose.rest[bone][0]))
    return rotate(mul(world, inv(world_rest)), rest_direction)

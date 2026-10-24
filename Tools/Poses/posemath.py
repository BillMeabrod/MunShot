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
from collections import namedtuple

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

IDENTITY = (0.0, 0.0, 0.0, 1.0)

# A bone at rest: its local rotation, its rotation and position in body space, and its parent's name
RestBone = namedtuple('RestBone', 'local world parent position')


def load_rest(path=os.path.join(HERE, 'rest_pose.txt')):
    """{bone: RestBone}, names without the bone_kerbal_ prefix."""
    rest = {}
    for line in open(path, encoding='utf-8'):
        if line.startswith('#') or not line.strip():
            continue
        f = line.split()
        name = f[0][len(PREFIX):] if f[0].startswith(PREFIX) else f[0]
        parent = f[9][len(PREFIX):] if f[9].startswith(PREFIX) else f[9]
        rest[name] = RestBone(tuple(map(float, f[1:5])), tuple(map(float, f[5:9])), parent, tuple(map(float, f[10:13])))
    return rest


# Points along an arm the helmet check looks at, and how far a fingertip reaches past the last finger joint
ARM_POINTS = ('elbow', 'hand', 'hand_ach')
FINGERS = ('pointer', 'middle', 'pinky', 'thumb')
FINGERTIP = 0.03

# How far (degrees) a forearm may twist its palm either way from where it sits untwisted, and an upper arm roll about
# itself either way from where it sits when swung straight round from rest
MAX_FOREARM_TWIST = 90
MAX_UPPER_ARM_TWIST = 35

# The helmet's shell (mesh_helmet_01's bounds, with the visor's bulge at the front), in body space at rest, as a
# rounded box (|x/a|^3 + |y/b|^3 + |z/c|^3 = 1; the shell stays wide further down than an ellipsoid would). It hangs on
# neck_ach, so it tips with it. Checked against poses tried in game: hands 3.5 cm or less outside it touched the helmet
# or only just cleared it, so keep them at least HELMET_CLEARANCE out
HELMET_CENTRE = (0.0, 0.618, 0.037)
HELMET_HALF_SIZE = (0.30, 0.276, 0.265)
HELMET_ROUNDNESS = 3
HELMET_CLEARANCE = 0.04

# The suit's torso (mesh_spacesuit_01_hardware's bounds, a little rounder), in body space at rest, the same way. It
# moves with spine_lower
BODY_CENTRE = (0.0, 0.28, 0.02)
BODY_HALF_SIZE = (0.13, 0.10, 0.13)


class _Watched(dict):
    """A dict that calls `changed` whenever it's written to, so a Pose can drop results worked out from it."""

    def __init__(self, changed):
        super().__init__()
        self._changed = changed

    def __setitem__(self, key, value):
        super().__setitem__(key, value)
        self._changed()

    def __delitem__(self, key):
        super().__delitem__(key)
        self._changed()

    def _refuse(self, *args, **kwargs):
        raise TypeError('Set pose entries one at a time, so cached results are dropped.')

    update = setdefault = pop = popitem = clear = _refuse


class Pose:
    """One pose being written: each move sets a bone's local rotation; bones not moved aren't in the pose."""

    def __init__(self, rest):
        self.rest = rest
        # Each bone's body-space rotation and position are worked out from its parents again and again (the reach
        # solver asks thousands of times), so they're kept until the pose changes
        self._world, self._posed, self._position = {}, {}, {}
        self.bones = _Watched(self._changed)
        self.turns = _Watched(self._changed)
        self.moves = _Watched(self._changed)
        self.eyes = None
        self.lashes = False

    def move_animated(self, bone, offset):
        """
        Slides a bone from wherever the animation has it, by a body-space offset in metres, given as if its parent were
        at rest. Goes in the pose file's "moves".
        """
        self._check(bone)
        self.moves[bone] = rotate(inv(self._parent_rest_world(bone)), offset)

    def eye_image(self, image, size, depth=1.0):
        """
        Lays an image from the poses folder over the eyeballs, covering `size` (0.1 to 0.95) of each, at `depth` (0.5
        to 1.5) of the way from the eyeball's middle to its front.
        """
        if not 0.1 <= size <= 0.95:
            raise ValueError(f'eye image size {size} must be from 0.1 to 0.95')
        if not 0.5 <= depth <= 1.5:
            raise ValueError(f'eye image depth {depth} must be from 0.5 to 1.5')
        self.eyes = {'image': image, 'size': size, 'depth': depth}

    def turn_animated(self, bone, rotation):
        """
        Turns a bone further from wherever the animation (e.g. the pose's base clip) has it, by a body-space rotation
        (from turn()), given as if the bone's parent were at rest. Goes in the pose file's "turns".
        """
        self._check(bone)
        if bone in self.bones:
            raise ValueError(f"'{bone}' is already set by this pose; a bone is either set or turned.")
        parent_world = self._parent_rest_world(bone)
        self.turns[bone] = mul(mul(inv(parent_world), rotation), parent_world)

    def _changed(self):
        self._world.clear()
        self._posed.clear()
        self._position.clear()

    def _check(self, bone):
        if bone not in self.rest:
            raise KeyError(f"No bone '{bone}' in the rig (names are without '{PREFIX}').")

    def _parent_rest_world(self, bone):
        parent = self.rest[bone].parent
        return self.rest[parent].world if parent in self.rest else IDENTITY

    def at_rest(self, *bones):
        """Holds bones in their rest pose (e.g. a straight elbow or finger), whatever the animation does."""
        for bone in bones:
            self._check(bone)
            self.bones[bone] = self.rest[bone].local

    def set_local(self, bone, rotation):
        """Sets a bone's local rotation (relative to its parent) directly, e.g. one read from the game."""
        self._check(bone)
        self.bones[bone] = rotation

    def turn(self, bone, rotation):
        """Turns a bone by a body-space rotation (from turn()), starting from rest, with its parent at rest."""
        self._check(bone)
        self.bones[bone] = mul(inv(self._parent_rest_world(bone)), mul(rotation, self.rest[bone].world))

    def bend(self, bone, axis, degrees):
        """Turns a bone about one of its own axes (X, Y or Z), on top of where this pose has it (or rest)."""
        self._check(bone)
        start = self.bones.get(bone, self.rest[bone].local)
        self.bones[bone] = mul(start, turn(axis, degrees))

    def world(self, bone):
        """A bone's rotation in body space with this pose applied to it and its parents (unposed ones at rest)."""
        if bone not in self.rest:
            return IDENTITY
        if bone not in self._world:
            self._world[bone] = mul(self.world(self.rest[bone].parent), self.bones.get(bone, self.rest[bone].local))
        return self._world[bone]

    def aim(self, bone, along, target, side, target_side):
        """
        Points a bone: its own axis `along` (e.g. (-1, 0, 0), the way it points at rest) goes to body-space direction
        `target`, and its axis `side` (e.g. the palm's) goes as near `target_side` as it can while doing so. Works
        down a chain: aim a parent first, and its children are aimed relative to where it now is.
        """
        self._check(bone)
        rest_world = self.rest[bone].world
        frame_from = _frame(rotate(rest_world, along), rotate(rest_world, side))
        frame_to = _frame(target, target_side)
        world = mul(mul(frame_to, inv(frame_from)), rest_world)
        self.bones[bone] = mul(inv(self.world(self.rest[bone].parent)), world)

    # ---- Where things end up, for checking a pose before trying it in game ----
    # Bones the pose doesn't set are taken at rest, with its turns and moves on top. A base clip moves them
    # differently, so these are a guide for what the pose sets (arms, legs), not for what it leaves to the clip.

    def _posed_world(self, bone):
        """A bone's body-space rotation with this pose's sets and turns (turns applied to the rest pose)."""
        if bone not in self.rest:
            return IDENTITY
        if bone not in self._posed:
            rest = self.rest[bone]
            local = self.bones.get(bone)
            if local is None:
                local = mul(self.turns[bone], rest.local) if bone in self.turns else rest.local
            self._posed[bone] = mul(self._posed_world(rest.parent), local)
        return self._posed[bone]

    def position(self, bone):
        """A bone's position in body space (metres) with this pose applied."""
        if bone in self._position:
            return self._position[bone]
        rest = self.rest[bone]
        if rest.parent not in self.rest:
            return rest.position
        parent = self.rest[rest.parent]
        parent_turn = mul(self._posed_world(rest.parent), inv(parent.world))
        at = _add(self.position(rest.parent), rotate(parent_turn, _sub(rest.position, parent.position)))
        if bone in self.moves:
            at = _add(at, rotate(self._posed_world(rest.parent), self.moves[bone]))
        self._position[bone] = at
        return at

    def tip(self, bone, length):
        """The point `length` metres along a bone from its joint, the way it points (its own -X, as limbs and fingers do)."""
        return _add(self.position(bone), _scale(rotate(self._posed_world(bone), (-1.0, 0.0, 0.0)), length))

    def _helmet(self):
        """The helmet's turn from rest and its middle, in body space: it tips with neck_ach."""
        pivot = self.rest['neck_ach']
        tipped = mul(self._posed_world('neck_ach'), inv(pivot.world))
        return tipped, _add(self.position('neck_ach'), rotate(tipped, _sub(HELMET_CENTRE, pivot.position)))

    def helmet_gap(self, point):
        """Roughly how far (metres) a body-space point is outside the helmet's shell; negative is inside."""
        tipped, centre = self._helmet()
        return _box_gap(point, centre, tipped, HELMET_HALF_SIZE)

    def body_gap(self, point):
        """Roughly how far (metres) a body-space point is outside the suit's torso; negative is inside."""
        pivot = self.rest['spine_lower']
        turned = mul(self._posed_world('spine_lower'), inv(pivot.world))
        centre = _add(self.position('spine_lower'), rotate(turned, _sub(BODY_CENTRE, pivot.position)))
        return _box_gap(point, centre, turned, BODY_HALF_SIZE)

    def helmet_report(self, side):
        """The arm's points nearest the helmet: [(gap in metres, point)], nearest first."""
        gaps = [(self.helmet_gap(self.position(f'{side}_{b}')), b) for b in ARM_POINTS]
        gaps += [(self.helmet_gap(self.tip(f'{side}_{f}_03', FINGERTIP)), f'{f} tip') for f in FINGERS]
        return sorted(gaps)

    def reach(self, side, target, end='hand_ach', palm=None, hand=None, hand_palm=None, elbow=None, helmet=True):
        """
        Aims an arm (shoulder, elbow, hand; the collar at rest) so that `end` lands on body-space point `target`:
        'hand_ach' is the middle of the palm, 'tip' the middle fingertip (with the fingers straight). The elbow bends
        only the way the rig's does (towards the upper arm's front at rest, its own +Y on both sides), up to 150
        degrees. Optional body-space directions: `palm`, the way the forearm's palm side faces (default: as at rest,
        relative to the bend); `hand`, the way the hand points (default: on along the forearm, bending up to 80 degrees
        at the wrist); `hand_palm`, the way the hand's palm faces (default: the forearm's); `elbow`, the way the
        elbow's point should face if it can. With `helmet`, the arm and fingers keep HELMET_CLEARANCE out of the
        helmet; the elbow and wrist always keep out of the torso. Returns how far (metres) `end` ends up from `target`.
        """
        upper, fore, hand_bone = f'{side}_shoulder', f'{side}_elbow', f'{side}_hand'
        palm_side = (0, 0, -1) if side == 'r' else (0, 0, 1)
        front = (0, 1, 0)
        self.at_rest(f'{side}_collar')

        # The upper arm as it sits at rest under its (posed) parents: the way it points and its front
        at_rest = mul(self.world(self.rest[upper].parent), self.rest[upper].local)
        rest_along, rest_front = rotate(at_rest, (-1, 0, 0)), rotate(at_rest, front)
        max_twist = math.radians(MAX_UPPER_ARM_TWIST)

        def place(v):
            yaw, pitch, twist, bend = v
            u = (math.cos(pitch) * math.sin(yaw), math.sin(pitch), math.cos(pitch) * math.cos(yaw))
            # The way the elbow bends: the upper arm's front swung straight round from rest to its new direction,
            # then rolled about the arm by the twist, at most MAX_UPPER_ARM_TWIST (checked in game: rolling it freely
            # to aim the elbow wrung the shoulder)
            n0 = rotate(_swing(rest_along, u), rest_front)
            n = rotate(turn(u, math.degrees(max(-max_twist, min(max_twist, twist)))), n0)
            bent = max(0.0, min(math.radians(150), bend))
            f = _add(_scale(u, math.cos(bent)), _scale(n, math.sin(bent)))
            fore_front = _add(_scale(u, -math.sin(bent)), _scale(n, math.cos(bent)))
            self.aim(upper, (-1, 0, 0), u, front, n)
            self.aim(fore, (-1, 0, 0), f, front, fore_front)
            fore_palm = rotate(self.world(fore), palm_side)
            if palm:
                # The forearm twists its palm towards `palm`, but no more than 90 degrees from where it sits untwisted
                # (checked in game: more wrings the wrist like a sweet wrapper)
                fore_palm = _twist_towards(fore_palm, palm, f, math.radians(MAX_FOREARM_TWIST))
                self.aim(fore, (-1, 0, 0), f, palm_side, fore_palm)
            self.aim(hand_bone, (-1, 0, 0), hand or f, (0, -1, 0), hand_palm or fore_palm)
            return n, f

        def point():
            return self.tip(f'{side}_middle_03', FINGERTIP) if end == 'tip' else self.position(f'{side}_{end}')

        def cost(v):
            n, f = place(v)
            c = 1e4 * sum(m * m for m in _sub(point(), target))
            c += 1e2 * (max(0.0, v[3] - math.radians(150)) + max(0.0, -v[3])) ** 2
            c += 1e2 * max(0.0, abs(v[2]) - max_twist) ** 2
            # Rolling the upper arm only as far as it helps: each radian costs about as much as the elbow facing
            # the wrong way
            c += 0.1 * v[2] ** 2
            if hand:
                wrist = math.acos(max(-1.0, min(1.0, sum(x * y for x, y in zip(f, _normalize(hand))))))
                c += 1e1 * max(0.0, wrist - math.radians(80)) ** 2
            if elbow:
                # The elbow's point faces away from the way it bends
                c += 0.05 * (1.0 + sum(x * y for x, y in zip(n, _normalize(elbow))))
            if helmet:
                for g, _ in self.helmet_report(side):
                    c += 1e4 * max(0.0, HELMET_CLEARANCE - g) ** 2
            # The elbow and wrist stay out of the torso (the hand may rest on it)
            for joint in (fore, hand_bone):
                c += 1e4 * max(0.0, 0.01 - self.body_gap(self.position(joint))) ** 2
            return c

        d = _normalize(_sub(target, self.position(upper)))
        yaw0, pitch0 = math.atan2(d[0], d[2]), math.asin(max(-1.0, min(1.0, d[1])))
        best = None
        for twist0 in (-0.6, 0.0, 0.6):
            for bend0 in (0.3, 1.2):
                v, c = _nelder_mead(cost, [yaw0, pitch0, twist0, bend0], 0.4)
                if best is None or c < best[1]:
                    best = (v, c)
        place(best[0])
        return math.sqrt(sum(m * m for m in _sub(point(), target)))

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


def _swing(a, b):
    """The smallest rotation taking direction `a` to direction `b`."""
    a, b = _normalize(a), _normalize(b)
    axis = _cross(a, b)
    sin = math.sqrt(sum(c * c for c in axis))
    cos = sum(x * y for x, y in zip(a, b))
    if sin < 1e-9:
        if cos > 0:
            return IDENTITY
        # Opposite: half a turn about any axis square to them
        axis = _cross(a, (0.0, 1.0, 0.0) if abs(a[1]) < 0.9 else (1.0, 0.0, 0.0))
    return turn(axis, math.degrees(math.atan2(sin, cos)))


def _twist_towards(v, want, axis, limit):
    """`v` turned about `axis` towards `want` (both taken square to `axis`), by at most `limit` radians."""
    axis = _normalize(axis)

    def square(w):
        d = sum(a * b for a, b in zip(w, axis))
        return _normalize(_sub(w, _scale(axis, d)))
    a, b = square(v), square(want)
    angle = math.atan2(sum(x * y for x, y in zip(_cross(a, b), axis)), sum(x * y for x, y in zip(a, b)))
    angle = max(-limit, min(limit, angle))
    return rotate(turn(axis, math.degrees(angle)), a)


def _box_gap(point, centre, rotation, half_size):
    """Roughly how far a point is outside a rounded box (see HELMET_ROUNDNESS) turned by `rotation`; negative inside."""
    local = rotate(inv(rotation), _sub(point, centre))
    k = sum(abs(c / h) ** HELMET_ROUNDNESS for c, h in zip(local, half_size)) ** (1 / HELMET_ROUNDNESS)
    return (k - 1.0) * min(half_size)


def _nelder_mead(f, x0, step, iterations=400):
    """Minimises f from x0 (a small Nelder-Mead); returns (x, f(x))."""
    pts = [list(x0)] + [[x + (step if i == j else 0.0) for j, x in enumerate(x0)] for i in range(len(x0))]
    vals = [f(x) for x in pts]
    for _ in range(iterations):
        order = sorted(range(len(pts)), key=vals.__getitem__)
        pts, vals = [pts[i] for i in order], [vals[i] for i in order]
        if vals[-1] - vals[0] < 1e-10:
            break
        centre = [sum(x[i] for x in pts[:-1]) / (len(pts) - 1) for i in range(len(x0))]
        reflect = [c + (c - w) for c, w in zip(centre, pts[-1])]
        fr = f(reflect)
        if fr < vals[0]:
            expand = [c + 2 * (c - w) for c, w in zip(centre, pts[-1])]
            fe = f(expand)
            pts[-1], vals[-1] = (expand, fe) if fe < fr else (reflect, fr)
        elif fr < vals[-2]:
            pts[-1], vals[-1] = reflect, fr
        else:
            contract = [c + 0.5 * (w - c) for c, w in zip(centre, pts[-1])]
            fc = f(contract)
            if fc < vals[-1]:
                pts[-1], vals[-1] = contract, fc
            else:
                pts = [pts[0]] + [[b + 0.5 * (x - b) for x, b in zip(p, pts[0])] for p in pts[1:]]
                vals = [vals[0]] + [f(x) for x in pts[1:]]
    i = min(range(len(pts)), key=vals.__getitem__)
    return pts[i], vals[i]


def _add(a, b):
    return tuple(p + q for p, q in zip(a, b))


def _sub(a, b):
    return tuple(p - q for p, q in zip(a, b))


def _scale(v, k):
    return tuple(c * k for c in v)


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

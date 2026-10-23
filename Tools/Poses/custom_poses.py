"""Writes MunShot's hand-made poses to Assets/MunShot/Copied/Poses/custom.json.

Each pose is a recipe: optionally a game clip to start from (as the stock poses are), then moves in the kerbal's own
directions (see posemath.py), turned into the bones a pose file sets ("bones") or turns further from the clip
("turns"). Bones a pose doesn't touch keep the clip, or the game's animation. Edit a recipe here, never the JSON by
hand.
Run: python Tools/Poses/custom_poses.py
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from posemath import X, Y, Z, Pose, load_rest, mul, rotate, turn

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', '..', 'Assets', 'MunShot', 'Copied', 'Poses', 'custom.json')
REST = load_rest()


def close(a, b, tolerance=0.1):
    return all(abs(p - q) < tolerance for p, q in zip(a, b))


def unit(v):
    n = sum(c * c for c in v) ** 0.5
    return tuple(c / n for c in v)


def rock_on(slant=1):
    """
    Right hand up in devil horns beside the helmet, screaming joyfully at full force with fierce closed eyes, on the
    game's Happy emote. slant=1 slants the closed eyes down towards the nose (checked in game: fierce); -1 droops them
    outward (reads as sad).
    """
    p = Pose(REST)
    clip = {'ground': ('kerbal_emote_happy', 0.5), 'jetpack': ('kerbal_jp_emote_happy', 0.5)}

    # Right arm out to the side, about level with the shoulder and a little towards the camera, elbow straight, wrist
    # bent so the horns point up, leaning away from the head, with the palm facing forward. Kerbal arms are short and
    # the helmet is a sphere about 0.25 m in radius around the head: a raised hand ends behind or inside it (checked in
    # game: 15 degrees up puts the fingers through the glass, and 30 degrees forward puts the wrist on the helmet's
    # rim), while a level arm out to the side puts the hand beside the helmet's narrower lower half, clear of it.
    # The arm bones point along their own -X at rest; the palm faces their -Z (upper arm, forearm) or -Y (hand)
    arm = unit((0.936, 0.087, 0.341))           # 5 degrees up, 20 degrees towards the camera
    fingers_up = unit((0.50, 0.84, 0.20))       # up, leaning about 30 degrees away from the head
    palm_forward = (0.0, 0.0, 1.0)
    p.at_rest('r_collar')
    p.aim('r_shoulder', (-1, 0, 0), arm, (0, 0, -1), palm_forward)
    p.aim('r_elbow', (-1, 0, 0), arm, (0, 0, -1), palm_forward)
    p.aim('r_hand', (-1, 0, 0), fingers_up, (0, -1, 0), palm_forward)
    assert close(rotate(p.world('r_shoulder'), (-1, 0, 0)), arm), 'upper arm should point out'
    assert close(rotate(p.world('r_hand'), (-1, 0, 0)), fingers_up), 'hand should point up'
    assert rotate(p.world('r_hand'), (0, -1, 0))[2] > 0.9, 'palm should face forward'

    # Horns: pointer and pinky straight; middle finger curled into the palm (fingers curl about their own +Z); thumb
    # swung across the palm towards the fingers (about the hand's up axis at rest) and curled over the middle finger
    p.at_rest('r_pointer_01', 'r_pointer_02', 'r_pointer_03', 'r_pinky_01', 'r_pinky_02', 'r_pinky_03')
    p.bend('r_middle_01', Z, 85)
    p.bend('r_middle_02', Z, 95)
    p.bend('r_middle_03', Z, 65)
    p.turn('r_thumb_01', turn(Y, 45))
    p.bend('r_thumb_02', Z, 30)
    p.bend('r_thumb_03', Z, 35)

    # Screaming: jaw open as wide as the helmet allows (the lower jaw turns about the kerbal's right axis, its own +Z;
    # checked in game: 20 degrees or more pokes the chin out under the helmet's neck ring), the upper jaw lifted a
    # little (10 degrees or more lifts the mouth's corners into the closed eyes, whose slant swings the lower lids'
    # outer sides down towards them), and the head tipped back from where Happy has it,
    # which swings the chin up and makes the scream bigger
    p.bend('jaw_d', Z, 16)
    p.bend('jaw_u', Z, -6)
    p.turn_animated('head', turn(X, -18))

    # Fierce closed eyes for a joyful scream: the lids meet along a line slanted down towards the nose. Happy squeezes
    # the lids shut so far that they overlap and no seam shows, and a seam is what a slant shows on. So each eye's
    # lids open back just until they meet (about +X: - lifts an upper lid's front edge, + lowers a lower lid's; checked
    # in game: 12 and 8 degrees open a slit, 7 and 5 leave a white line, 1.5 and 1 hide the seam), then turn together
    # about the forward axis to slant the seam
    # (about +Z, + raises the kerbal's right side). Happy's eyeballs stay rolled up behind the lids
    for side, tilt in (('l', -40 * slant), ('r', 40 * slant)):
        p.turn_animated(f'{side}_eye_lid_u', mul(turn(Z, tilt), turn(X, -2.5)))
        p.turn_animated(f'{side}_eye_lid_d', mul(turn(Z, tilt), turn(X, 1.5)))
    return p, clip


POSES = [
    ('rock-on', 'Rock on', rock_on),
]


def entry(pose_id, name, recipe):
    pose, clip = recipe()
    e = {'id': pose_id, 'name': name}
    for version, (clip_name, time) in (clip or {}).items():
        e[version] = {'clip': clip_name, 'time': time}
    if pose.bones:
        e['bones'] = pose.to_json_bones()
    if pose.turns:
        e['turns'] = pose.to_json_turns()
    return e


def write(poses):
    # One line per bone, so changes read well in a diff
    def block(key, rotations):
        rows = ',\n'.join(f'        "{b}": {json.dumps(v)}' for b, v in rotations.items())
        return f'      "{key}": {{\n{rows}\n      }}'

    items = []
    for e in poses:
        parts = [f'      "id": {json.dumps(e["id"])}', f'      "name": {json.dumps(e["name"])}']
        parts += [f'      "{v}": {json.dumps(e[v])}' for v in ('ground', 'jetpack') if v in e]
        parts += [block(k, e[k]) for k in ('bones', 'turns') if k in e]
        items.append('    {\n' + ',\n'.join(parts) + '\n    }')
    text = '{\n  "version": 1,\n  "poses": [\n' + ',\n'.join(items) + '\n  ]\n}\n'
    json.loads(text)
    with open(OUT, 'w', encoding='utf-8', newline='\n') as f:
        f.write(text)


def main():
    poses = [entry(*p) for p in POSES]
    write(poses)
    print(f'{len(poses)} poses -> {os.path.normpath(OUT)}')


if __name__ == '__main__':
    main()

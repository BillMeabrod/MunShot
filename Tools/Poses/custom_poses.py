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


def anime_girl():
    """
    Both hands in peace signs held forward under the helmet, the right leg kicked up behind, hips swayed, head tilted,
    a small mouth and anime eyes: the idol pose.
    """
    p = Pose(REST)

    # Both arms reaching forward, a little down and out, elbows straight, so the hands sit in front of the chest under
    # the helmet's front edge; wrists bent so the peace signs point up, lean in towards each other, and tip forward
    # towards the camera to keep the fingers out of the helmet; palms facing forward.
    # Left bones mirror the right: they point along their own -X too, but the upper arm and forearm's palm side is +Z
    palm_forward = (0.0, 0.0, 1.0)
    # (Checked in game: kerbal arms can't put fingers leaning in towards the head clear of the helmet's side; held
    # forward, with the fingers upright, they touch the helmet's front, and still a little tipped 35 degrees forward,
    # so they tip 45)
    for side, out, palm in (('r', 1, (0, 0, -1)), ('l', -1, (0, 0, 1))):
        arm = unit((0.30 * out, -0.25, 0.92))
        fingers_up = unit((-0.30 * out, 0.68, 0.67))     # up, leaning in about 20 degrees and forward about 45
        p.at_rest(f'{side}_collar')
        p.aim(f'{side}_shoulder', (-1, 0, 0), arm, palm, palm_forward)
        p.aim(f'{side}_elbow', (-1, 0, 0), arm, palm, palm_forward)
        p.aim(f'{side}_hand', (-1, 0, 0), fingers_up, (0, -1, 0), palm_forward)
        assert close(rotate(p.world(f'{side}_hand'), (-1, 0, 0)), fingers_up), f'{side} hand should point up'
        # (tipping the fingers forward tips the palm up)
        assert rotate(p.world(f'{side}_hand'), (0, -1, 0))[2] > 0.65, f'{side} palm should face forward'

        # Peace sign: pointer and middle straight, pinky curled into the palm, thumb swung across the palm and curled
        # over the pinky. Fingers curl about their own +Z on both sides; the thumb swings the mirrored way on the left
        p.at_rest(*(f'{side}_{finger}_0{j}' for finger in ('pointer', 'middle') for j in (1, 2, 3)))
        p.bend(f'{side}_pinky_01', Z, 85)
        p.bend(f'{side}_pinky_02', Z, 95)
        p.bend(f'{side}_pinky_03', Z, 65)
        p.turn(f'{side}_thumb_01', turn(Y, 50 * out))
        p.bend(f'{side}_thumb_02', Z, 35)
        p.bend(f'{side}_thumb_03', Z, 35)

    # Right leg kicked up behind: the thigh swings a little back, the knee bends so the shin points back and slightly
    # up, and the foot points back with its sole to the sky. Leg bones point along their own -X; the thigh and shin's
    # front is their +Y, the foot's top is its +Y
    thigh = unit((0.1, -0.96, -0.26))
    shin = unit((0.05, 0.17, -0.98))
    toes = unit((0.0, -0.6, -0.8))
    p.aim('r_thigh', (-1, 0, 0), thigh, (0, 1, 0), (0, 0, 1))
    p.aim('r_knee', (-1, 0, 0), shin, (0, 1, 0), (0, -1, 0))
    p.aim('r_foot', (-1, 0, 0), toes, (0, 1, 0), (0, -0.8, 0.6))
    assert close(rotate(p.world('r_knee'), (-1, 0, 0)), shin), 'shin should point back'

    # Hips: the pelvis tips about 14 degrees, the standing (left) leg's hip up and out, and the lower spine leans back
    # the other way so the upper body stays upright: a gentle S-curve. Turns on top of the animation (about +Z, +
    # raises the kerbal's right side)
    p.turn_animated('pelvis', turn(Z, -14))
    p.turn_animated('spine_lower', turn(Z, 9))

    # Face: head tilted a little to the side, mouth closed and small, anime eyes. The lids keep the game's animation,
    # open and blinking
    p.at_rest('jaw_d', 'jaw_u')
    p.turn_animated('head', turn(Z, 10))

    # A small mouth: the lip bones slide in towards the middle, following the face's curve forward (the corners sit
    # 4 cm further back than the middle of the lips), the corners a touch up into a little smile
    for side, out in (('l', -1), ('r', 1)):
        p.move_animated(f'jaw_u_lips_{side}_corner', (-0.035 * out, 0.004, 0.025))
        p.move_animated(f'jaw_u_lips_{side}', (-0.028 * out, 0.0, 0.02))
        p.move_animated(f'jaw_d_lips_{side}', (-0.025 * out, 0.0, 0.018))
        p.move_animated(f'jaw_u_lips_{side}c', (-0.008 * out, 0.0, 0.003))
        p.move_animated(f'jaw_d_lips_{side}c', (-0.008 * out, 0.0, 0.003))

    # Anime eyes: each eyeball aimed straight ahead (at rest they turn 15 degrees outward), so both look at the camera;
    # eyeballs look along their own -Y with +Z up. Then the teal iris laid over each
    for side in ('l', 'r'):
        p.aim(f'{side}_eye', (0, -1, 0), (0, 0, 1), (0, 0, 1), (0, 1, 0))
        assert close(rotate(p.world(f'{side}_eye'), (0, -1, 0)), (0, 0, 1)), f'{side} eye should look ahead'
    p.eye_image('anime_teal.png', 0.6, 1.0)
    # Eyelashes, from the game's female eyelids
    p.lashes = True
    return p, None


def hanging_on():
    """
    Holding on for dear life: the right fist gripping out to the side, the body leaning away from it with the legs
    streaming and flailing and the left arm flung back, the head and helmet tipped away from the grip, terrified and
    staring at the hand. Turn the kerbal with the rings so the fist is on a hull and the body is swept back in the wind.
    (Checked in game: a grip above about 10 degrees over the shoulder ends up inside an upright helmet, and leaning the
    body hard away from a sideways grip folds the kerbal at the waist; so the helmet tips away and the lean is gentle.)
    """
    p = Pose(REST)
    # The game's Panic expression for the mouth's shape, which the bones below build on
    clip = {'ground': ('kerbal_exp_panic_01', 1.0)}

    # Leaning gently away from the grip: the lower spine 15 degrees and the hips 25 degrees towards the kerbal's left
    # (about +Z, + tips "up" towards the left). Set, so the arms and legs are aimed from where these leave them
    p.turn('spine_lower', turn(Z, 15))
    p.turn('pelvis', turn(Z, -25))

    # The helmet hangs on the neck attachment bone (with the neck and head), so tipping that 20 degrees to the left
    # takes the helmet away from the gripping arm, which can then come up higher
    p.turn_animated('neck_ach', turn(Z, 20))

    # Right arm out to the side, 20 degrees up and a little forward, palm down, in a tight fist (fingers curl about
    # their own +Z; the thumb swings across the fingers and curls over them). Arm bones point along their own -X; the
    # upper arm and forearm's palm side is their -Z, the hand's its -Y
    grip = unit((0.91, 0.34, 0.24))
    p.at_rest('r_collar')
    p.aim('r_shoulder', (-1, 0, 0), grip, (0, 0, -1), (0, -1, 0))
    p.aim('r_elbow', (-1, 0, 0), grip, (0, 0, -1), (0, -1, 0))
    p.aim('r_hand', (-1, 0, 0), grip, (0, -1, 0), (0, -1, 0))
    assert close(rotate(p.world('r_hand'), (-1, 0, 0)), grip), 'gripping hand should point out to the side'
    for finger in ('pointer', 'middle', 'pinky'):
        p.bend(f'r_{finger}_01', Z, 85)
        p.bend(f'r_{finger}_02', Z, 95)
        p.bend(f'r_{finger}_03', Z, 65)
    p.turn('r_thumb_01', turn(Y, 30))
    p.bend('r_thumb_02', Z, 40)
    p.bend('r_thumb_03', Z, 40)

    # Left arm flung out and back, a little below the shoulder so the tipped helmet clears it, elbow slightly bent,
    # hand open (the left arm's palm side is +Z)
    p.at_rest('l_collar')
    p.aim('l_shoulder', (-1, 0, 0), unit((-0.85, -0.10, -0.50)), (0, 0, 1), (0, -1, 0))
    p.aim('l_elbow', (-1, 0, 0), unit((-0.80, 0.10, -0.55)), (0, 0, 1), (0, -1, 0))
    p.at_rest(*(f'l_{finger}_0{j}' for finger in ('pointer', 'middle', 'pinky', 'thumb') for j in (1, 2, 3)))

    # Legs streaming away from the grip and behind, flailing: the right swung back with the knee bent, the left kicking
    # back and out. Behind the hips they stay clear of the jetpack's thrusters, which deploy beside the hips at the
    # front (checked in game: a leg kicking forward goes through one). With the jetpack deployed the left leg still
    # touches the jetpack's arm a little; legs hanging straight down between the arms would clear it, but the user
    # chose these more dramatic legs. Leg bones point along their own -X; the thigh and shin's front is their +Y
    p.aim('r_thigh', (-1, 0, 0), unit((-0.30, -0.90, -0.30)), (0, 1, 0), (0, 0, 1))
    p.aim('r_knee', (-1, 0, 0), unit((-0.25, -0.40, -0.88)), (0, 1, 0), (0, -1, 0))
    p.aim('l_thigh', (-1, 0, 0), unit((-0.45, -0.75, -0.48)), (0, 1, 0), (0, 0, 1))
    p.aim('l_knee', (-1, 0, 0), unit((-0.50, -0.55, -0.67)), (0, 1, 0), (0, 0, 1))

    # Terrified face: head turned 25 degrees towards the fist (about +Y, + turns the face to the kerbal's right) and
    # tipped back 10, which swings the chin up inside the helmet, jaw dropped as far as the helmet allows (checked in
    # game: 18 degrees with the head level pokes the chin out under the neck ring), the mouth's corners pulled down and out into a grimace (kerbal mouths curve up at rest, so an open mouth
    # alone reads as a cheerful shout), eyes wide (lids pulled back from wherever the clip has them) staring at the fist
    # (eyeballs look along their own -Y; aimed as if the head weren't turned, since the head's turn adds to it)
    p.turn_animated('head', mul(turn(Y, 25), turn(X, -10)))
    p.bend('jaw_d', Z, 13)
    p.bend('jaw_u', Z, -7)
    for side, out in (('l', -1), ('r', 1)):
        p.move_animated(f'jaw_u_lips_{side}_corner', (0.006 * out, -0.012, 0.0))
        p.move_animated(f'jaw_d_lips_{side}', (0.004 * out, -0.008, 0.0))
        p.turn_animated(f'{side}_eye_lid_u', turn(X, -20))
        p.turn_animated(f'{side}_eye_lid_d', turn(X, 10))
        p.aim(f'{side}_eye', (0, -1, 0), unit((0.41, 0.15, 0.90)), (0, 0, 1), (0, 1, 0))
    return p, clip


POSES = [
    ('rock-on', 'Rock on', rock_on),
    ('anime-girl', 'Anime Girl', anime_girl),
    ('hanging-on', 'Hanging On', hanging_on),
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
    if pose.moves:
        e['moves'] = {b: [round(c, 4) for c in v] for b, v in pose.moves.items()}
    if pose.eyes:
        e['eyes'] = pose.eyes
    if pose.lashes:
        e['lashes'] = True
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
        parts += [block(k, e[k]) for k in ('bones', 'turns', 'moves') if k in e]
        parts += [f'      "eyes": {json.dumps(e["eyes"])}'] if 'eyes' in e else []
        parts += ['      "lashes": true'] if e.get('lashes') else []
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

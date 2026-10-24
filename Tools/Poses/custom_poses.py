"""Writes MunShot's hand-made poses to Assets/MunShot/Copied/Poses/custom.json.

Each pose is a recipe: optionally a game clip to start from (as the stock poses are), then moves in the kerbal's own
directions (see posemath.py), turned into the bones a pose file sets ("bones") or turns further from the clip
("turns"). Bones a pose doesn't touch keep the clip, or the game's animation. Edit a recipe here, never the JSON by
hand.
Run: python Tools/Poses/custom_poses.py                  (every pose)
     python Tools/Poses/custom_poses.py superman cheerleader   (just these; the rest are kept from custom.json)
Rebuild every pose after changing anything they share (posemath.py, fist(), the other helpers).
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from posemath import HELMET_CLEARANCE, X, Y, Z, Pose, load_rest, mul, rotate, turn

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', '..', 'Assets', 'MunShot', 'Copied', 'Poses', 'custom.json')
REST = load_rest()


def close(a, b, tolerance=0.1):
    return all(abs(p - q) < tolerance for p, q in zip(a, b))


def unit(v):
    n = sum(c * c for c in v) ** 0.5
    return tuple(c / n for c in v)


# ---- Shared moves ----
# Arm and leg bones point along their own -X. Fingers curl about their own +Z on both sides; the thumb swings across
# the palm about the hand's up axis at rest, the mirrored way on the left

FINGERS = ('pointer', 'middle', 'pinky')
CURLED = (85, 95, 65)


def out_of(side):
    """+1 for the kerbal's right, -1 for its left: multiplies X to mirror a direction."""
    return 1 if side == 'r' else -1


def curl(p, side, fingers=FINGERS, joints=CURLED):
    for finger in fingers:
        for j, degrees in enumerate(joints, 1):
            p.bend(f'{side}_{finger}_0{j}', Z, degrees)


def thumb_across(p, side, swing, bend2, bend3):
    p.turn(f'{side}_thumb_01', turn(Y, swing * out_of(side)))
    p.bend(f'{side}_thumb_02', Z, bend2)
    p.bend(f'{side}_thumb_03', Z, bend3)


# The game's own hands, read with the pose lab's Dump rig from its Thumbs Up emote (kerbal_emote_thumbsup_loop at
# 1 s), as local rotations (x, y, z, w). The thumbs-up hand's fingers roll into a neat stack (knuckles about 50
# degrees, the next two joints about 80); the other hand's fist has the thumb across the fingers. (Checked in game:
# fingers curled the same set angle at each joint hooked like claws, and the other hand's own fingers, bent about 83
# degrees at the knuckle, sink their tips into the palm close up.)
THUMBS_UP_FINGERS_RIGHT = {
    'pointer_01': (0.0, 0.0, 0.361593, 0.932336),
    'pointer_02': (0.0, 0.0, 0.674324, 0.738435),
    'pointer_03': (0.0, 0.0, 0.657650, 0.753324),
    'middle_01': (0.038311, -0.008121, 0.440413, 0.896941),
    'middle_02': (0.0, 0.0, 0.627986, 0.778225),
    'middle_03': (0.0, 0.0, 0.657649, 0.753324),
    'pinky_01': (0.068179, -0.011974, 0.448325, 0.891186),
    'pinky_02': (0.0, 0.0, 0.627986, 0.778225),
    'pinky_03': (0.0, 0.0, 0.657649, 0.753324),
}
FIST_THUMB_LEFT = {
    'thumb_01': (0.029992, 0.217327, 0.110884, 0.969317),
    'thumb_02': (0.047261, 0.211179, -0.259010, -0.941320),
    'thumb_03': (0.053811, 0.200827, -0.253163, -0.944818),
}
# How much less than the game's fist the thumb swings across the fingers (about the hand's up axis), so it lies on
# them rather than through them (checked in game: the game's thumb showed the glove's inside poking through)
FIST_THUMB_OUT = 10


def mirrored(rotation):
    """
    A hand bone's local rotation for the other side: the two hands' local axes are each other's with Z flipped, so
    (x, y, z, w) becomes (-x, -y, z, w).
    """
    x, y, z, w = rotation
    return (-x, -y, z, w)


def curled_finger(p, side, finger):
    """A finger rolled into the palm, as the game's thumbs-up hand has it (THUMBS_UP_FINGERS_RIGHT)."""
    for j in (1, 2, 3):
        rotation = THUMBS_UP_FINGERS_RIGHT[f'{finger}_0{j}']
        p.set_local(f'{side}_{finger}_0{j}', rotation if side == 'r' else mirrored(rotation))


def tucked_thumb(p, side):
    """The thumb across curled fingers, as the game's fist has it (FIST_THUMB_LEFT), swung FIST_THUMB_OUT less."""
    for joint, rotation in FIST_THUMB_LEFT.items():
        p.set_local(f'{side}_{joint}', rotation if side == 'l' else mirrored(rotation))
    # thumb_across swings +angle across the palm on either side, so -angle swings it back out
    p.set_local(f'{side}_thumb_01', mul(turn(Y, -FIST_THUMB_OUT * out_of(side)), p.bones[f'{side}_thumb_01']))


def fist(p, side):
    for finger in FINGERS:
        curled_finger(p, side, finger)
    tucked_thumb(p, side)



def open_hand(p, side):
    p.at_rest(*(f'{side}_{finger}_0{j}' for finger in FINGERS + ('thumb',) for j in (1, 2, 3)))


def relaxed_hand(p, side):
    curl(p, side, joints=(20, 25, 15))
    thumb_across(p, side, 10, 10, 10)


def pointing_hand(p, side):
    p.at_rest(*(f'{side}_pointer_0{j}' for j in (1, 2, 3)))
    curl(p, side, ('middle', 'pinky'))
    thumb_across(p, side, 30, 40, 40)


def leg(p, side, thigh, shin, foot=None):
    """
    Aims a leg: thigh, shin and (optionally) foot directions in body space. A leg bone's front (its +Y; the foot's
    top) is taken square to it, on the forward or upper side, as a leg bends: forwards when it points down.
    """
    for bone, d in (('thigh', thigh), ('knee', shin), ('foot', foot)):
        if d is None:
            continue
        d = unit(d)
        p.aim(f'{side}_{bone}', (-1, 0, 0), d, (0, 1, 0), rotate(turn(X, -90), d))


def shoulder(p, side):
    return p.position(f'{side}_shoulder')


def toward(p, side, direction, reach=0.31):
    """The point `reach` metres from the shoulder in `direction` (a straight arm's fingertips reach about 0.31 m)."""
    s0 = shoulder(p, side)
    d = unit(direction)
    return tuple(a + reach * b for a, b in zip(s0, d))


def wide_eyes(p):
    """Lids pulled back from wherever the clip has them."""
    for side in ('l', 'r'):
        p.turn_animated(f'{side}_eye_lid_u', turn(X, -20))
        p.turn_animated(f'{side}_eye_lid_d', turn(X, 10))


def grimace(p):
    """Mouth corners pulled down and out, over an open jaw (kerbal mouths curve up at rest)."""
    for side in ('l', 'r'):
        out = out_of(side)
        p.move_animated(f'jaw_u_lips_{side}_corner', (0.006 * out, -0.012, 0.0))
        p.move_animated(f'jaw_d_lips_{side}', (0.004 * out, -0.008, 0.0))


def reach(p, side, target, **kwargs):
    """Pose.reach, failing loudly if the hand can't get within 4 cm of the target."""
    miss = p.reach(side, target, **kwargs)
    assert miss < 0.04, f'{side} hand misses its target {target} by {miss:.3f} m'
    return miss


def fling(p, side, direction, fallback=None, **kwargs):
    """
    A straight arm flung `direction` (its fingertips at full reach), or as near it as the helmet allows: the direction
    eases towards `fallback` (default: straight out to the side at shoulder height) until the arm clears. Prints how
    far it had to ease.
    """
    fallback = fallback or (out_of(side), 0.0, 0.0)
    d, f = unit(direction), unit(fallback)
    for step in range(11):
        t = step / 10
        aimed = unit(tuple(a + t * (b - a) for a, b in zip(d, f)))
        if p.reach(side, toward(p, side, aimed), end='tip', **kwargs) < 0.01:
            if t:
                print(f'    {side} arm eased {t:.0%} of the way from {direction} to {fallback} to clear the helmet')
            return aimed
    raise AssertionError(f'{side} arm can clear the helmet neither towards {direction} nor {fallback}')


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

    # Horns: pointer and pinky straight, the middle finger rolled into the palm and the thumb tucked over it, as the
    # game's own hands curl them (checked in game: curling each joint a set angle crumpled them together)
    p.at_rest('r_pointer_01', 'r_pointer_02', 'r_pointer_03', 'r_pinky_01', 'r_pinky_02', 'r_pinky_03')
    curled_finger(p, 'r', 'middle')
    tucked_thumb(p, 'r')

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
    # The hands go where a straight arm pointing `arm` puts them (checked in game: there they clear the helmet), but
    # the arm gets there through the reach solver, which rolls the upper arm only a little and turns the palm with
    # the forearm (checked in game: aiming each arm bone to face its palm forward wrung the shoulders)
    for side, out, palm in (('r', 1, (0, 0, -1)), ('l', -1, (0, 0, 1))):
        arm = unit((0.30 * out, -0.25, 0.92))
        fingers_up = unit((-0.30 * out, 0.68, 0.67))     # up, leaning in about 20 degrees and forward about 45
        p.at_rest(f'{side}_collar')
        p.aim(f'{side}_shoulder', (-1, 0, 0), arm, palm, palm_forward)
        p.aim(f'{side}_elbow', (-1, 0, 0), arm, palm, palm_forward)
        p.aim(f'{side}_hand', (-1, 0, 0), fingers_up, (0, -1, 0), palm_forward)
        reach(p, side, p.position(f'{side}_hand_ach'), palm=palm_forward, hand=fingers_up, hand_palm=palm_forward)
        assert close(rotate(p.world(f'{side}_hand'), (-1, 0, 0)), fingers_up), f'{side} hand should point up'
        # (tipping the fingers forward tips the palm up)
        assert rotate(p.world(f'{side}_hand'), (0, -1, 0))[2] > 0.65, f'{side} palm should face forward'

        # Peace sign: pointer and middle straight, the pinky rolled into the palm and the thumb tucked over it, as the
        # game's own hands curl them (checked in game: curling each joint a set angle hooked them like claws)
        p.at_rest(*(f'{side}_{finger}_0{j}' for finger in ('pointer', 'middle') for j in (1, 2, 3)))
        curled_finger(p, side, 'pinky')
        tucked_thumb(p, side)

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


def superman():
    """
    Flying like Superman: both fists punched out ahead, as high as the helmet lets them go, legs together with the left
    knee bent a little, toes pointed, head tipped back to look where it's going. Made upright: tip the kerbal forward
    with the rings to fly.
    """
    p = Pose(REST)
    clip = {'ground': ('kerbal_exp_happy_01', 1.0)}
    # Head tipped back (about +X, - tips the face up), which also lifts the helmet's front out of the arms' way
    p.turn_animated('neck_ach', turn(X, -25))
    p.turn('spine_lower', turn(X, -8))
    # Arms straight ahead and up past the head as far as the helmet allows, else straight ahead and level, knuckles up
    for side in ('r', 'l'):
        out = out_of(side)
        fling(p, side, (0.12 * out, 0.8, 0.6), fallback=(0.12 * out, -0.05, 0.99), palm=(0, -1, 0), hand_palm=(0, -1, 0),
              elbow=(out, 0, 0))
        fist(p, side)
    # Toes pointed straight down
    leg(p, 'r', (0.04, -1, 0.0), (0.03, -1, 0.0), (0.0, -1, 0.05))
    leg(p, 'l', (-0.04, -1, 0.05), (-0.03, -0.85, -0.5), (0.0, -1, 0.05))
    return p, clip


def floating_asleep():
    """Asleep in zero g: the game's Sleeping emote, arms drifting up in front, knees drifting up a little."""
    p = Pose(REST)
    clip = {'ground': ('kerbal_emote_sleeping', 2.0), 'jetpack': ('kerbal_jp_emote_sleeping', 2.0)}
    # Head drooping further forward than the emote has it (about +X, + tips the face down), helmet and all
    p.turn_animated('neck_ach', turn(X, 30))
    p.turn_animated('head', turn(X, 20))
    for side in ('r', 'l'):
        out = out_of(side)
        reach(p, side, (0.25 * out, 0.27, 0.17), palm=(0.3 * out, -0.9, 0.3), hand=(0.35 * out, -0.25, 0.9),
              elbow=(out, -0.4, 0))
        relaxed_hand(p, side)
    leg(p, 'r', (0.08, -0.85, 0.5), (0.05, -0.9, -0.4), (0.05, -0.75, 0.6))
    leg(p, 'l', (-0.1, -0.9, 0.35), (-0.05, -0.92, -0.35), (-0.05, -0.75, 0.6))
    return p, clip


def spacewalk_tumble():
    """
    Tumbling out of control: amazed, arms and legs flung out every which way, body twisted, head thrown back and
    sideways. Tip the kerbal any way with the rings.
    """
    p = Pose(REST)
    clip = {'ground': ('kerbal_exp_amazed_01', 1.0)}
    p.turn('spine_lower', mul(turn(Y, 15), turn(Z, -8)))
    p.turn('pelvis', turn(Y, -10))
    p.turn_animated('neck_ach', mul(turn(Z, 15), turn(X, -10)))
    fling(p, 'r', (0.85, 0.4, -0.35), palm=(0, -0.5, -0.8))
    open_hand(p, 'r')
    fling(p, 'l', (-0.75, -0.4, 0.5), palm=(0, -0.6, 0.8))
    open_hand(p, 'l')
    leg(p, 'r', (0.45, -0.65, 0.6), (0.35, -0.9, -0.25))
    leg(p, 'l', (-0.45, -0.8, -0.4), (-0.35, -0.9, 0.25))
    p.bend('jaw_d', Z, 12)
    p.bend('jaw_u', Z, -6)
    wide_eyes(p)
    return p, clip


def planting_the_flag():
    """The game's flag-planting animation, held as the pole goes in. (No flag in hand yet.)"""
    p = Pose(REST)
    clip = {'ground': ('kerbal_flag_plant', 4.0)}
    return p, clip


def hand_on_hip(p, side):
    """A fist resting on the hip, elbow out to the side and back."""
    out = out_of(side)
    # The fist carries straight on from the forearm (checked in game: setting the palm and hand directions as well
    # wrung the wrist)
    reach(p, side, (0.19 * out, 0.23, 0.0), elbow=(out, 0, -0.4))
    fist(p, side)


def thumbs_up():
    """Jeb's thumbs up: the game's Thumbs Up emote with the other fist on the hip and a cocky tilt of the head."""
    p = Pose(REST)
    clip = {'ground': ('kerbal_emote_thumbsup_loop', 1.0), 'jetpack': ('kerbal_jp_emote_thumbsup_loop', 1.0)}
    hand_on_hip(p, 'l')
    p.turn_animated('head', turn(Z, -8))
    return p, clip


def raised_fists(p, forward=0.35):
    """Both fists punched up and out as high as the helmet allows."""
    for side in ('r', 'l'):
        out = out_of(side)
        fling(p, side, (0.8 * out, 0.6, forward), palm=(0, 0, 1), elbow=(out, -0.5, 0))
        fist(p, side)


def victory_jump():
    """Leaping for joy: both fists up in a V, head back shouting, right knee tucked up, left leg kicked back."""
    p = Pose(REST)
    clip = {'ground': ('kerbal_exp_happy_02', 1.0)}
    p.turn_animated('neck_ach', turn(X, -15))
    raised_fists(p)
    leg(p, 'r', (0.1, -0.2, 0.98), (0.05, -0.95, -0.3), (0.0, -0.8, 0.6))
    leg(p, 'l', (-0.05, -0.97, -0.2), (-0.05, -0.75, -0.65), (0.0, -0.95, -0.3))
    p.bend('jaw_d', Z, 12)
    p.bend('jaw_u', Z, -6)
    return p, clip


def running_away():
    """Running away in a panic: mid-stride, arms flailing up, looking back over the right shoulder, terrified."""
    p = Pose(REST)
    clip = {'ground': ('kerbal_run', 0.13)}
    # The face turns to the kerbal's right (about +Y, +), helmet and all
    p.turn_animated('neck_ach', turn(Y, 40))
    p.turn_animated('head', turn(Y, 25))
    fling(p, 'r', (0.6, 0.5, -0.6), palm=(0, 0, -1))
    open_hand(p, 'r')
    fling(p, 'l', (-0.55, 0.55, 0.6), palm=(0, 0, 1))
    open_hand(p, 'l')
    p.bend('jaw_d', Z, 13)
    p.bend('jaw_u', Z, -7)
    grimace(p)
    wide_eyes(p)
    return p, clip


def sitting_on_the_edge():
    """Sitting on a ledge, legs dangling (one kicked forward), hands on the edge beside the hips, looking down."""
    p = Pose(REST)
    p.turn('spine_lower', turn(X, 8))
    leg(p, 'r', (0.12, -0.1, 0.99), (0.03, -0.95, 0.3))
    leg(p, 'l', (-0.12, -0.1, 0.99), (-0.03, -0.97, -0.2))
    for side in ('r', 'l'):
        out = out_of(side)
        reach(p, side, (0.19 * out, 0.17, 0.03), palm=(0, -1, 0), hand=(0.3 * out, -0.1, 0.95), hand_palm=(0, -1, 0),
              elbow=(0, 0, -1))
        curl(p, side, joints=(30, 20, 10))
    p.turn_animated('head', mul(turn(Z, 8), turn(X, 10)))
    return p, None


def dramatic_point():
    """
    Pointing dramatically into the distance: lunging forward, right arm straight out and up, left arm flung back,
    head up following the arm, eyes narrowed.
    """
    p = Pose(REST)
    p.turn('spine_lower', turn(X, 8))
    p.turn_animated('neck_ach', mul(turn(X, -10), turn(Y, 10)))
    fling(p, 'r', (0.35, 0.4, 0.85), fallback=(0.45, -0.1, 0.89), palm=(-0.5, -0.5, 0), elbow=(0, -1, 0))
    pointing_hand(p, 'r')
    fling(p, 'l', (-0.6, -0.5, -0.6), palm=(0, -0.3, -1))
    open_hand(p, 'l')
    leg(p, 'r', (0.1, -0.6, 0.8), (0.05, -0.95, 0.15))
    leg(p, 'l', (-0.08, -0.85, -0.5), (-0.08, -0.85, -0.5))
    for side in ('l', 'r'):
        p.turn_animated(f'{side}_eye_lid_u', turn(X, 8))
    return p, None


def cheerleader():
    """
    A cheer: right fist up high, left fist down low on the other diagonal, right knee lifted high in front with the
    toes pointed down, hips swayed, big smile.
    """
    p = Pose(REST)
    clip = {'ground': ('kerbal_exp_happy_02', 1.0)}
    p.turn_animated('pelvis', turn(Z, -8))
    fling(p, 'r', (0.8, 0.6, 0.25), palm=(0, 0, 1), elbow=(1, -0.5, 0))
    fist(p, 'r')
    fling(p, 'l', (-0.75, -0.65, 0.1), palm=(0, 0, 1))
    fist(p, 'l')
    leg(p, 'l', (-0.04, -1, 0.0), (-0.03, -1, 0.0))
    # Thigh lifted level in front and a little out, shin hanging straight down, toes pointed. (Checked in game: with
    # kerbal legs this short, a foot brought in to the other knee crosses the hips sideways, sole out)
    leg(p, 'r', (0.3, -0.05, 0.95), (0.08, -1, 0.05), (0.05, -0.95, 0.3))
    p.turn_animated('head', turn(Z, 12))
    return p, clip


POSES = [
    ('rock-on', 'Rock on', rock_on),
    ('anime-girl', 'Anime Girl', anime_girl),
    ('hanging-on', 'Hanging On', hanging_on),
    ('superman', 'Superman', superman),
    ('floating-asleep', 'Floating Asleep', floating_asleep),
    ('spacewalk-tumble', 'Spacewalk Tumble', spacewalk_tumble),
    ('planting-the-flag', 'Planting the Flag', planting_the_flag),
    ('thumbs-up', 'Thumbs Up', thumbs_up),
    ('victory-jump', 'Victory Jump', victory_jump),
    ('running-away', 'Running Away', running_away),
    ('sitting-on-the-edge', 'Sitting on the Edge', sitting_on_the_edge),
    ('dramatic-point', 'Dramatic Point', dramatic_point),
    ('cheerleader', 'Cheerleader', cheerleader),
]


def helmet_report(pose_id, pose):
    """Prints each posed arm's nearest point to the helmet, flagging any closer than HELMET_CLEARANCE."""
    for side in ('r', 'l'):
        if f'{side}_shoulder' not in pose.bones:
            continue
        gap, point = pose.helmet_report(side)[0]
        flag = '  <- check in game' if gap < HELMET_CLEARANCE else ''
        print(f'  {pose_id:22} {side} arm: nearest the helmet: {point} at {gap:+.3f} m{flag}')


def entry(pose_id, name, recipe):
    pose, clip = recipe()
    helmet_report(pose_id, pose)
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


def written():
    """The poses in custom.json as last written, by id; none if there's no file yet."""
    if not os.path.exists(OUT):
        return {}
    with open(OUT, encoding='utf-8') as f:
        return {e['id']: e for e in json.load(f)['poses']}


def main(only):
    known = [pose_id for pose_id, _, _ in POSES]
    unknown = [pose_id for pose_id in only if pose_id not in known]
    if unknown:
        sys.exit(f'No pose {", ".join(unknown)}. Poses: {", ".join(known)}')
    kept = written() if only else {}
    poses, rebuilt = [], 0
    for pose_id, name, recipe in POSES:
        if only and pose_id not in only and pose_id in kept:
            poses.append(dict(kept[pose_id], name=name))
        else:
            poses.append(entry(pose_id, name, recipe))
            rebuilt += 1
    write(poses)
    print(f'{len(poses)} poses ({rebuilt} rebuilt) -> {os.path.normpath(OUT)}')


if __name__ == '__main__':
    main(sys.argv[1:])
